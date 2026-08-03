using System.Buffers;
using System.IO.Compression;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>Eventos e metadados obtidos durante a leitura de um arquivo.</summary>
    public sealed record ResultadoLeituraArquivoLog(
        IReadOnlyList<ClefEvent> Eventos,
        long? OffsetFinal,
        int LinhasInvalidas,
        string? PrimeiroErro);

    public interface ILeitorArquivoLog
    {
        /// <param name="pool">
        /// Compartilha as strings repetidas entre eventos (nível, template, chaves de
        /// propriedade). Opcional: sem ele a leitura funciona igual, só ocupa mais memória.
        /// </param>
        Task<ResultadoLeituraArquivoLog> LerAsync(
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Interpreta um bloco de bytes já lido do arquivo, contendo apenas linhas completas.
        /// </summary>
        /// <remarks>
        /// Existe para o acompanhamento ao vivo, que precisa da mesma interpretação da carga
        /// mas não pode delegar a abertura do arquivo: quem acompanha é dono do offset, do
        /// reposicionamento após truncamento e do descarte da linha grande demais. Sem este
        /// método o tail chamava o parser estático e um leitor injetado cobria só a carga.
        /// <para><c>OffsetFinal</c> volta nulo — a posição no arquivo é de quem leu o bloco.</para>
        /// </remarks>
        /// <param name="inicioDoArquivo">
        /// Informa que o bloco começa no byte 0. Só então o BOM pode ser descartado: os mesmos
        /// três bytes no meio do arquivo são conteúdo de uma linha válida.
        /// </param>
        ResultadoLeituraArquivoLog LerTrecho(
            ReadOnlySpan<byte> bloco,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            bool inicioDoArquivo,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Responsável exclusivamente por abrir e interpretar arquivos CLEF. O processamento
    /// por linha permite isolar registros inválidos sem perder os eventos posteriores.
    ///
    /// <para>A leitura é feita em blocos de bytes com <see cref="ArrayPool{T}"/> e as linhas
    /// são recortadas no próprio buffer: o <see cref="StreamReader"/> anterior materializava
    /// uma string por linha (315 mil por carga real) só para o parser voltar a convertê-la em
    /// bytes lá dentro.</para>
    /// </summary>
    public sealed class LeitorArquivoLog : ILeitorArquivoLog
    {
        // 64 KB cobre a linha típica com folga; linha maior faz o buffer crescer (stack trace
        // grande em @x é comum).
        private const int TamanhoBuffer = 64 * 1024;

        private readonly long _limiarParalelo;
        private readonly long _segmentoMinimo;

        public LeitorArquivoLog()
            : this(limiarParalelo: 32L * 1024 * 1024, segmentoMinimo: 8L * 1024 * 1024)
        {
        }

        /// <summary>
        /// Ajusta quando a leitura de UM arquivo passa a ser dividida entre núcleos.
        /// Existe como construtor (e não como constantes) para os testes exercitarem o
        /// caminho paralelo com arquivos de quilobytes em vez de gigabytes.
        /// </summary>
        /// <param name="limiarParalelo">Tamanho a partir do qual o arquivo é segmentado.</param>
        /// <param name="segmentoMinimo">
        /// Menor fatia que vale um worker: abaixo disso o custo de abrir o stream e alinhar
        /// a fronteira supera o ganho de paralelismo.
        /// </param>
        public LeitorArquivoLog(long limiarParalelo, long segmentoMinimo)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limiarParalelo, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(segmentoMinimo, 1);
            _limiarParalelo = limiarParalelo;
            _segmentoMinimo = segmentoMinimo;
        }

        public async Task<ResultadoLeituraArquivoLog> LerAsync(
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arquivo);
            ArgumentNullException.ThrowIfNull(textosIgnorados);

            await using var streamArquivo = new FileStream(
                arquivo,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                useAsync: true);

            if (arquivo.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                // gz é um fluxo: não há como posicionar num byte do meio sem descomprimir
                // tudo antes dele, então o paralelismo por segmento não se aplica.
                await using var compactado = new GZipStream(streamArquivo, CompressionMode.Decompress);
                return await LerStreamAsync(
                    compactado,
                    arquivo,
                    textosIgnorados,
                    offsetFinal: null,
                    CacheDeTemplates.Para(pool),
                    verificarBom: true,
                    cancellationToken).ConfigureAwait(false);
            }

            // O Parallel.ForEachAsync do LogStore distribui por ARQUIVO: um único .clef
            // de 1 GB ocupava um núcleo só enquanto os outros 19 esperavam. Acima do
            // limiar, o próprio arquivo é dividido em segmentos alinhados a '\n'.
            if (streamArquivo.Length >= _limiarParalelo)
            {
                return await LerParaleloAsync(
                    streamArquivo,
                    arquivo,
                    textosIgnorados,
                    pool,
                    cancellationToken).ConfigureAwait(false);
            }

            return await LerStreamAsync(
                streamArquivo,
                arquivo,
                textosIgnorados,
                offsetFinal: () => streamArquivo.Position,
                CacheDeTemplates.Para(pool),
                verificarBom: true,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Divide o arquivo em segmentos que começam exatamente em início de linha e lê
        /// cada um num worker próprio, preservando a ordem do arquivo na concatenação.
        ///
        /// <para>As fronteiras são resolvidas ANTES dos workers: um seek por fronteira,
        /// avançando até o primeiro <c>\n</c>. Assim cada worker roda o laço sequencial
        /// já existente sobre um recorte fechado, sem coordenação entre eles — a linha
        /// que cruza uma fronteira bruta pertence, por construção, ao segmento anterior.</para>
        /// </summary>
        private async Task<ResultadoLeituraArquivoLog> LerParaleloAsync(
            FileStream stream,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool,
            CancellationToken cancellationToken)
        {
            // Comprimento capturado uma vez: o arquivo pode continuar crescendo (tail),
            // e ler além de L produziria segmentos com fronteiras móveis. O que entrar
            // depois fica para o acompanhamento ao vivo — OffsetFinal diz até onde fomos.
            var comprimento = stream.Length;
            var alvo = (long)Math.Clamp(Environment.ProcessorCount, 2, 32);
            var tamanhoSegmento = Math.Max(_segmentoMinimo, comprimento / alvo);

            var fronteiras = new List<long> { 0 };
            for (var bruta = tamanhoSegmento; bruta < comprimento; bruta += tamanhoSegmento)
            {
                var alinhada = await AcharInicioDeLinhaAsync(stream, bruta, comprimento, cancellationToken)
                    .ConfigureAwait(false);
                // Linha gigante pode empurrar a fronteira além da próxima bruta; só
                // fronteiras estritamente crescentes viram segmento.
                if (alinhada > fronteiras[^1] && alinhada < comprimento) fronteiras.Add(alinhada);
            }
            fronteiras.Add(comprimento);

            var cache = CacheDeTemplates.Para(pool);
            var tarefas = new Task<ResultadoLeituraArquivoLog>[fronteiras.Count - 1];
            for (var i = 0; i < tarefas.Length; i++)
            {
                var inicio = fronteiras[i];
                var fim = fronteiras[i + 1];
                var primeiro = i == 0;
                tarefas[i] = Task.Run(
                    () => LerSegmentoAsync(
                        arquivo, inicio, fim - inicio, primeiro, textosIgnorados, cache, cancellationToken),
                    cancellationToken);
            }

            var partes = await Task.WhenAll(tarefas).ConfigureAwait(false);

            var eventos = new List<ClefEvent>(partes.Sum(p => p.Eventos.Count));
            var invalidas = 0;
            string? primeiroErro = null;
            foreach (var parte in partes)
            {
                eventos.AddRange(parte.Eventos);
                invalidas += parte.LinhasInvalidas;
                primeiroErro ??= parte.PrimeiroErro;
            }

            return new ResultadoLeituraArquivoLog(eventos, comprimento, invalidas, primeiroErro);
        }

        private static async Task<ResultadoLeituraArquivoLog> LerSegmentoAsync(
            string arquivo,
            long inicio,
            long comprimento,
            bool primeiroSegmento,
            IReadOnlyList<string> textosIgnorados,
            CacheDeTemplates cache,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                arquivo,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: TamanhoBuffer,
                useAsync: true);
            stream.Position = inicio;

            return await LerStreamAsync(
                new RecorteDeLeitura(stream, comprimento),
                arquivo,
                textosIgnorados,
                offsetFinal: null,
                cache,
                verificarBom: primeiroSegmento,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Limita a leitura a um trecho do stream subjacente. Permite que o laço de
        /// leitura sequencial rode inalterado sobre UM segmento do arquivo: para ele, o
        /// fim do recorte é indistinguível do fim do arquivo.
        /// </summary>
        private sealed class RecorteDeLeitura : Stream
        {
            private readonly Stream _origem;
            private long _restante;

            public RecorteDeLeitura(Stream origem, long comprimento)
            {
                _origem = origem;
                _restante = comprimento;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> destino, CancellationToken cancellationToken = default)
            {
                if (_restante <= 0) return 0;
                var maximo = (int)Math.Min(destino.Length, _restante);
                var lidos = await _origem.ReadAsync(destino[..maximo], cancellationToken)
                    .ConfigureAwait(false);
                _restante -= lidos;
                return lidos;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_restante <= 0) return 0;
                var maximo = (int)Math.Min(count, _restante);
                var lidos = _origem.Read(buffer, offset, maximo);
                _restante -= lidos;
                return lidos;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Avança a partir de um ponto bruto até logo após o primeiro <c>\n</c>.</summary>
        private static async Task<long> AcharInicioDeLinhaAsync(
            FileStream stream,
            long posicaoBruta,
            long comprimento,
            CancellationToken cancellationToken)
        {
            stream.Position = posicaoBruta;
            var buffer = ArrayPool<byte>.Shared.Rent(TamanhoBuffer);
            try
            {
                var posicao = posicaoBruta;
                while (posicao < comprimento)
                {
                    var maximo = (int)Math.Min(buffer.Length, comprimento - posicao);
                    var lidos = await stream.ReadAsync(buffer.AsMemory(0, maximo), cancellationToken)
                        .ConfigureAwait(false);
                    if (lidos == 0) break;

                    var quebra = buffer.AsSpan(0, lidos).IndexOf((byte)'\n');
                    if (quebra >= 0) return posicao + quebra + 1;
                    posicao += lidos;
                }

                return comprimento;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task<ResultadoLeituraArquivoLog> LerStreamAsync(
            Stream stream,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            Func<long>? offsetFinal,
            CacheDeTemplates cache,
            bool verificarBom,
            CancellationToken cancellationToken)
        {
            var acumulador = new Acumulador(arquivo, textosIgnorados, cache);
            var buffer = ArrayPool<byte>.Shared.Rent(TamanhoBuffer);
            var preenchido = 0;
            // Segmentos que não são o primeiro começam no meio do arquivo: três bytes
            // EF BB BF ali seriam conteúdo de linha, nunca BOM.
            var bomVerificado = !verificarBom;

            try
            {
                while (true)
                {
                    var lidos = await stream
                        .ReadAsync(buffer.AsMemory(preenchido, buffer.Length - preenchido), cancellationToken)
                        .ConfigureAwait(false);
                    if (lidos == 0) break;
                    preenchido += lidos;

                    var inicio = 0;
                    if (!bomVerificado && preenchido >= 3)
                    {
                        bomVerificado = true;
                        // O StreamReader descartava o BOM sozinho; lendo bytes crus ele ficaria
                        // colado no '{' e a PRIMEIRA linha do arquivo viraria linha inválida.
                        if (ComecaComBom(buffer.AsSpan(0, preenchido))) inicio = 3;
                    }

                    var consumido = inicio + acumulador.ProcessarLinhas(
                        buffer.AsSpan(inicio, preenchido - inicio),
                        cancellationToken);
                    var resto = preenchido - consumido;
                    if (consumido > 0 && resto > 0) buffer.AsSpan(consumido, resto).CopyTo(buffer);
                    preenchido = resto;

                    if (preenchido == buffer.Length)
                    {
                        // A linha não coube: dobra o buffer em vez de desistir dela.
                        var maior = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, preenchido).CopyTo(maior);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = maior;
                    }
                }

                // Arquivo sem quebra de linha final: a última linha ainda está no buffer.
                if (preenchido > 0)
                {
                    var inicio = !bomVerificado && ComecaComBom(buffer.AsSpan(0, preenchido)) ? 3 : 0;
                    acumulador.ProcessarUltimaLinha(buffer.AsSpan(inicio, preenchido - inicio), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new ResultadoLeituraArquivoLog(
                acumulador.Eventos,
                offsetFinal?.Invoke(),
                acumulador.LinhasInvalidas,
                acumulador.PrimeiroErro);
        }

        /// <inheritdoc />
        public ResultadoLeituraArquivoLog LerTrecho(
            ReadOnlySpan<byte> bloco,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            bool inicioDoArquivo,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arquivo);
            ArgumentNullException.ThrowIfNull(textosIgnorados);

            if (inicioDoArquivo && ComecaComBom(bloco)) bloco = bloco[3..];

            var acumulador = new Acumulador(arquivo, textosIgnorados, CacheDeTemplates.Para(pool));
            // Sobra do bloco sem '\n' fica de fora de propósito: linha incompleta é linha
            // ainda sendo escrita, e interpretá-la seria ler JSON pela metade.
            acumulador.ProcessarLinhas(bloco, cancellationToken);

            return new ResultadoLeituraArquivoLog(
                acumulador.Eventos,
                null,
                acumulador.LinhasInvalidas,
                acumulador.PrimeiroErro);
        }

        private static bool ComecaComBom(ReadOnlySpan<byte> bloco) =>
            bloco.Length >= 3 && bloco[0] == 0xEF && bloco[1] == 0xBB && bloco[2] == 0xBF;

        public static bool DeveIgnorar(ClefEvent evento, IReadOnlyList<string> textosIgnorados)
        {
            foreach (var texto in textosIgnorados)
            {
                if (string.IsNullOrWhiteSpace(texto)) continue;
                if ((evento.Message ?? string.Empty).Contains(texto, StringComparison.OrdinalIgnoreCase)
                    || (evento.Exception ?? string.Empty).Contains(texto, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Junta o recorte das linhas com a contagem de falhas. Existe como classe porque
        /// <see cref="Span{T}"/> não pode aparecer em método <c>async</c> — o laço de bytes
        /// precisa ficar num método síncrono.
        /// </summary>
        private sealed class Acumulador
        {
            private readonly string _arquivo;
            private readonly IReadOnlyList<string> _textosIgnorados;
            private readonly CacheDeTemplates _cache;

            public Acumulador(string arquivo, IReadOnlyList<string> textosIgnorados, CacheDeTemplates cache)
            {
                _arquivo = arquivo;
                _textosIgnorados = textosIgnorados;
                _cache = cache;
            }

            public List<ClefEvent> Eventos { get; } = new();

            public int LinhasInvalidas { get; private set; }

            public string? PrimeiroErro { get; private set; }

            /// <summary>Consome as linhas completas do bloco e devolve quantos bytes saíram.</summary>
            public int ProcessarLinhas(ReadOnlySpan<byte> bloco, CancellationToken cancellationToken)
            {
                var consumido = 0;
                while (true)
                {
                    var restante = bloco[consumido..];
                    var quebra = restante.IndexOf((byte)'\n');
                    if (quebra < 0) break;

                    Processar(restante[..quebra], cancellationToken);
                    consumido += quebra + 1;
                }

                return consumido;
            }

            public void ProcessarUltimaLinha(ReadOnlySpan<byte> bloco, CancellationToken cancellationToken) =>
                Processar(bloco, cancellationToken);

            private void Processar(ReadOnlySpan<byte> linha, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (linha.Length > 0 && linha[^1] == (byte)'\r') linha = linha[..^1];
                if (LeitorClef.EhLinhaEmBranco(linha)) return;

                if (!LeitorClef.TentarLer(linha, _arquivo, _cache, out var evento, out var erro))
                {
                    LinhasInvalidas++;
                    PrimeiroErro ??= erro;
                    return;
                }

                if (!DeveIgnorar(evento!, _textosIgnorados)) Eventos.Add(evento!);
            }
        }
    }
}
