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
                await using var compactado = new GZipStream(streamArquivo, CompressionMode.Decompress);
                return await LerStreamAsync(
                    compactado,
                    arquivo,
                    textosIgnorados,
                    offsetFinal: null,
                    pool,
                    cancellationToken).ConfigureAwait(false);
            }

            return await LerStreamAsync(
                streamArquivo,
                arquivo,
                textosIgnorados,
                offsetFinal: () => streamArquivo.Position,
                pool,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<ResultadoLeituraArquivoLog> LerStreamAsync(
            Stream stream,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            Func<long>? offsetFinal,
            PoolDeTextos? pool,
            CancellationToken cancellationToken)
        {
            var acumulador = new Acumulador(arquivo, textosIgnorados, CacheDeTemplates.Para(pool));
            var buffer = ArrayPool<byte>.Shared.Rent(TamanhoBuffer);
            var preenchido = 0;
            var bomVerificado = false;

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
