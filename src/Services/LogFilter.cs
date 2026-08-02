using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Services
{
    /// <summary>Critérios de filtragem da lista de eventos.</summary>
    public sealed class LogFilterCriteria
    {
        /// <summary>Quando definido, só eventos cujo <see cref="ClefEvent.SourceFile"/> estiver no conjunto.</summary>
        public HashSet<string>? VisibleFiles { get; init; }

        /// <summary>
        /// Níveis aceitos (nomes do Serilog). <c>null</c> ou vazio = todos os níveis.
        /// É um conjunto, e não um nível único, para permitir combinações como
        /// "Error + Warning" e para dar acesso a Debug e Verbose, que antes não tinham
        /// como ser selecionados.
        /// </summary>
        public HashSet<string>? Levels { get; init; }

        /// <summary>
        /// Início do período, inclusivo. A hora conta: <c>15/06 08:30</c> descarta um evento
        /// das 08:29. Comparado contra o horário do evento como ele aparece na tela — o
        /// componente local do <see cref="DateTimeOffset"/>, sem conversão de fuso.
        /// </summary>
        public DateTime? From { get; init; }

        /// <summary>
        /// Fim do período, inclusivo, com a mesma semântica de hora do <see cref="From"/>.
        /// Quem monta o critério a partir de um campo com granularidade de minuto deve
        /// estender o valor até o fim do minuto (ver <c>LogViewer.AplicarFiltrosAsync</c>).
        /// </summary>
        public DateTime? To { get; init; }

        /// <summary>Busca textual em mensagem, exceção e valores das propriedades.</summary>
        public string? Search { get; init; }

        /// <summary>Interpreta <see cref="Search"/> como expressão regular.</summary>
        public bool UseRegex { get; init; }

        /// <summary>
        /// Quando a entrada já vem do mais recente para o mais antigo, dispensa a ordenação
        /// final — a filtragem preserva a ordem relativa da entrada. O <c>LogStore</c> mantém
        /// os eventos ordenados, então a UI liga isto e evita um O(n log n) por tecla
        /// digitada. Padrão <c>false</c>: quem não garante a ordem continua seguro.
        /// </summary>
        public bool InputAlreadySorted { get; init; }
    }

    /// <summary>
    /// Filtragem da lista de eventos. Vive fora do componente Razor para poder ser
    /// testada isoladamente — antes essa lógica estava embutida no
    /// <c>LogViewer.AplicarFiltros</c>, onde só era exercitada pela UI.
    /// </summary>
    public static class LogFilter
    {
        /// <summary>
        /// Limite por tentativa de correspondência. Uma expressão fornecida pelo usuário
        /// nunca pode manter indefinidamente a thread de filtragem ocupada.
        /// </summary>
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        /// <summary>Níveis do Serilog, do mais grave ao mais verboso.</summary>
        public static readonly string[] AllLevels =
            { "Fatal", "Error", "Warning", "Information", "Debug", "Verbose" };

        /// <summary>
        /// Ponto de virada medido: abaixo de 4 mil eventos as duas varreduras empatam
        /// (0,95x a 1,68x, sempre abaixo de 2,5 ms), e a partir daí o paralelo abre
        /// vantagem na busca textual — 1,6x em 4 mil, 12,8x em 8 mil, 5,0x em 50 mil.
        /// Não vale pagar particionamento e máscara para ganhar microssegundos.
        /// </summary>
        private const int MinimoParaParalelizar = 4_000;

        /// <summary>
        /// Cancelamento verificado por bloco, e não por evento como antes (que ainda
        /// checava uma vez por propriedade): a varredura de um bloco leva uma fração de
        /// milissegundo, então a interrupção continua imediata na prática, sem uma leitura
        /// volátil no meio do laço mais quente do aplicativo.
        /// </summary>
        private const int EventosPorBlocoDeCancelamento = 1_024;

        /// <summary>Diz se o texto é uma expressão regular válida (para avisar antes de filtrar).</summary>
        public static bool IsValidRegex(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return true;
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Aplica os critérios e devolve os eventos do mais recente para o mais antigo.</summary>
        public static List<ClefEvent> Apply(
            IEnumerable<ClefEvent> source,
            LogFilterCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(criteria);

            BuscaTextual? busca = null;
            if (!string.IsNullOrWhiteSpace(criteria.Search))
            {
                busca = BuscaTextual.Criar(criteria.Search!.Trim(), criteria.UseRegex);

                // Regex inválida: nenhum resultado, em vez de lançar enquanto o usuário digita.
                if (busca is null) return new List<ClefEvent>();
            }

            var resultado =
                source is IReadOnlyList<ClefEvent> indexavel && indexavel.Count >= MinimoParaParalelizar
                    ? FiltrarEmParalelo(indexavel, criteria, busca, cancellationToken)
                    : FiltrarSequencialmente(source, criteria, busca, cancellationToken);

            if (!criteria.InputAlreadySorted)
            {
                resultado.Sort((a, b) => Nullable.Compare(b.Timestamp, a.Timestamp));
            }

            return resultado;
        }

        private static List<ClefEvent> FiltrarSequencialmente(
            IEnumerable<ClefEvent> source,
            LogFilterCriteria criteria,
            BuscaTextual? busca,
            CancellationToken cancellationToken)
        {
            var contexto = busca?.AbrirContexto();
            var resultado = new List<ClefEvent>();
            var lidos = 0;

            foreach (var evento in source)
            {
                if ((lidos++ & (EventosPorBlocoDeCancelamento - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!PassaPelosFiltrosEstruturais(evento, criteria)) continue;
                if (busca is not null && !busca.Casa(evento, contexto!)) continue;
                resultado.Add(evento);
            }

            return resultado;
        }

        /// <summary>
        /// Cada partição escreve numa posição exclusiva da máscara — sem race e sem lock —
        /// e a coleta final percorre a máscara em ordem, então o resultado sai na ordem da
        /// entrada por construção. Medido na investigação: preservar a ordem assim saiu
        /// mais barato que um <c>ConcurrentBag</c> que nem ordem preserva.
        /// </summary>
        private static List<ClefEvent> FiltrarEmParalelo(
            IReadOnlyList<ClefEvent> eventos,
            LogFilterCriteria criteria,
            BuscaTextual? busca,
            CancellationToken cancellationToken)
        {
            var total = eventos.Count;
            var selecionados = new bool[total];

            // O caller real (LogStore.Snapshot) entrega um array; ler direto dele evita
            // uma chamada de interface por evento no laço mais quente do aplicativo.
            var vetor = eventos as ClefEvent[];
            var opcoes = new ParallelOptions { CancellationToken = cancellationToken };

            try
            {
                Parallel.ForEach(Particionar(total), opcoes, faixa =>
                {
                    // Um contexto por partição: nada compartilhado entre threads.
                    var contexto = busca?.AbrirContexto();

                    for (var i = faixa.Inicio; i < faixa.Fim; i++)
                    {
                        if (((i - faixa.Inicio) & (EventosPorBlocoDeCancelamento - 1)) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var evento = vetor is null ? eventos[i] : vetor[i];
                        if (!PassaPelosFiltrosEstruturais(evento, criteria)) continue;
                        if (busca is not null && !busca.Casa(evento, contexto!)) continue;
                        selecionados[i] = true;
                    }
                });
            }
            catch (AggregateException ex) when (ex.InnerException is not null)
            {
                // O LogViewer inspeciona InnerException para avisar sobre regex complexa;
                // o AggregateException do Parallel esconderia a RegexMatchTimeoutException.
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            var quantidade = 0;
            for (var i = 0; i < total; i++)
            {
                if (selecionados[i]) quantidade++;
            }

            // Capacidade exata: sem isso a List cresce dobrando e chega a alocar o dobro
            // do necessário — em 1 milhão de eventos isso são megabytes indo para o LOH.
            var resultado = new List<ClefEvent>(quantidade);
            for (var i = 0; i < total; i++)
            {
                if (selecionados[i]) resultado.Add(vetor is null ? eventos[i] : vetor[i]);
            }

            return resultado;
        }

        /// <summary>
        /// Partições fixas, ~8 por núcleo: pequenas o bastante para equilibrar carga entre
        /// núcleos de desempenho e de eficiência, grandes o bastante para o agendamento
        /// sumir. O mínimo evita fatiar demais um conjunto pequeno.
        /// </summary>
        private static (int Inicio, int Fim)[] Particionar(int total)
        {
            const int ParticoesPorNucleo = 8;

            var alvo = Math.Max(1, Environment.ProcessorCount * ParticoesPorNucleo);
            var tamanho = Math.Max(2_048, (total + alvo - 1) / alvo);
            var quantidade = (total + tamanho - 1) / tamanho;

            var faixas = new (int, int)[quantidade];
            for (var p = 0; p < quantidade; p++)
            {
                faixas[p] = (p * tamanho, Math.Min(total, (p + 1) * tamanho));
            }

            return faixas;
        }

        private static bool PassaPelosFiltrosEstruturais(ClefEvent evento, LogFilterCriteria criteria)
        {
            if (criteria.VisibleFiles is { } arquivos
                && (evento.SourceFile is null || !arquivos.Contains(evento.SourceFile)))
            {
                return false;
            }

            if (criteria.Levels is { Count: > 0 } niveis
                && (evento.Level is null || !niveis.Contains(evento.Level)))
            {
                return false;
            }

            // .DateTime, e não o instante UTC: a lista, a grade e o detalhe mostram o
            // horário com o offset gravado no log, então o filtro precisa comparar
            // contra o mesmo horário que o usuário está lendo na tela.
            if (criteria.From is { } inicio
                && (!evento.Timestamp.HasValue || evento.Timestamp.Value.DateTime < inicio))
            {
                return false;
            }

            if (criteria.To is { } fim
                && (!evento.Timestamp.HasValue || evento.Timestamp.Value.DateTime > fim))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Busca textual preparada uma vez por consulta e compartilhada por todas as
        /// partições. Só guarda estado imutável; o que cada partição precisa ter só para
        /// si (buffer de renderização e instância de <see cref="Regex"/>) fica no
        /// <see cref="ContextoDeBusca"/>.
        /// </summary>
        private sealed class BuscaTextual
        {
            private readonly string _termo;
            private readonly string? _padraoRegex;
            private readonly SearchValues<string>? _procura;
            private readonly bool _renderFiel;

            private BuscaTextual(string termo, string? padraoRegex, SearchValues<string>? procura, bool renderFiel)
            {
                _termo = termo;
                _padraoRegex = padraoRegex;
                _procura = procura;
                _renderFiel = renderFiel;
            }

            /// <summary>Devolve <c>null</c> quando a expressão regular não compila.</summary>
            public static BuscaTextual? Criar(string termo, bool usarRegex)
            {
                if (usarRegex)
                {
                    // Compila aqui só para saber se o padrão é válido; cada partição terá
                    // a sua própria instância (ver AbrirContexto).
                    try
                    {
                        _ = NovaRegex(termo);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }

                    return new BuscaTextual(termo, termo, null, renderFiel: true);
                }

                // FIDELIDADE. O texto que o Serilog renderiza para um valor string não é a
                // string: é ela entre aspas, com as aspas internas — e só elas — escapadas
                // por barra invertida (ScalarValue.Render). Comparar direto contra a string
                // subjacente é o que torna a busca barata, e para qualquer termo sem esses
                // dois caracteres os dois caminhos casam exatamente o mesmo conjunto. Quem
                // digita aspas ("RacClient") ou barra invertida cai no caminho fiel, que
                // renderiza como antes — custa cerca de 30% a mais e não muda resultado.
                var renderFiel = termo.Contains('"') || termo.Contains('\\');

                // SearchValues só compensa a partir de 2 caracteres. Medido sobre 2,4 M
                // strings: 'z' 223,6 ms contra 231,9 ms de string.Contains (empate), 'zq'
                // 33,0 contra 221,6 ms (6,7x), 'VAREJO' 31,9 contra 208,2 ms (6,5x).
                var procura = termo.Length >= 2
                    ? SearchValues.Create(new[] { termo }, StringComparison.OrdinalIgnoreCase)
                    : null;

                return new BuscaTextual(termo, null, procura, renderFiel);
            }

            /// <summary>
            /// Estado exclusivo de uma partição. A instância de <see cref="Regex"/> guarda
            /// UM runner reutilizável: com todas as partições batendo na mesma instância a
            /// troca falha quase sempre e cada correspondência aloca um runner novo —
            /// medido em 1 M de eventos, 1,5 GB por consulta viravam 5,6 GB.
            /// </summary>
            public ContextoDeBusca AbrirContexto() =>
                new(_padraoRegex is null ? null : NovaRegex(_padraoRegex));

            public bool Casa(ClefEvent evento, ContextoDeBusca contexto)
            {
                var regex = contexto.Regex;

                // Texto vazio no lugar de null: uma regex como "^$" casava um evento sem
                // mensagem antes desta mudança, e continua casando.
                if (Contem(evento.Message ?? string.Empty, regex)) return true;
                if (Contem(evento.Exception ?? string.Empty, regex)) return true;
                if (evento.Properties is null) return false;

                foreach (var propriedade in evento.Properties.Values)
                {
                    // O caminho quente: três em cada quatro valores de um log real são
                    // strings, e ToString() em cada um deles criava um StringWriter, um
                    // StringBuilder e uma string nova — 299 MB por tecla em 200 mil eventos.
                    if (!_renderFiel && propriedade is ScalarValue { Value: string texto })
                    {
                        if (Contem(texto, regex)) return true;
                        continue;
                    }

                    // Estruturas, sequências e escalares não-string: mesmo texto que o
                    // ToString() produzia (ele também só chama Render), mas escrito num
                    // buffer reciclado em vez de numa string nova por valor.
                    var buffer = contexto.Buffer;
                    buffer.Reiniciar();
                    propriedade.Render(buffer);
                    if (Contem(buffer.Texto, regex)) return true;
                }

                return false;
            }

            private bool Contem(ReadOnlySpan<char> texto, Regex? regex)
            {
                if (regex is not null) return regex.IsMatch(texto);
                if (_procura is not null) return texto.ContainsAny(_procura);
                return texto.Contains(_termo, StringComparison.OrdinalIgnoreCase);
            }

            private static Regex NovaRegex(string padrao) =>
                new(padrao, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }

        /// <summary>Estado de rascunho de uma partição (ou da varredura sequencial).</summary>
        private sealed class ContextoDeBusca
        {
            public ContextoDeBusca(Regex? regex) => Regex = regex;

            public Regex? Regex { get; }

            public BufferDeTexto Buffer { get; } = new();
        }

        /// <summary>
        /// <see cref="TextWriter"/> sobre um buffer reciclado: permite chamar
        /// <see cref="LogEventPropertyValue.Render"/> sem alocar a string que o
        /// <c>ToString()</c> alocava para cada valor de cada evento.
        /// </summary>
        private sealed class BufferDeTexto : TextWriter
        {
            private char[] _buffer = new char[512];
            private int _tamanho;

            public ReadOnlySpan<char> Texto => _buffer.AsSpan(0, _tamanho);

            public void Reiniciar() => _tamanho = 0;

            public override Encoding Encoding => Encoding.Unicode;

            public override void Write(char value)
            {
                Garantir(1);
                _buffer[_tamanho++] = value;
            }

            public override void Write(string? value)
            {
                if (value is null) return;
                Garantir(value.Length);
                value.CopyTo(0, _buffer, _tamanho, value.Length);
                _tamanho += value.Length;
            }

            public override void Write(ReadOnlySpan<char> buffer)
            {
                Garantir(buffer.Length);
                buffer.CopyTo(_buffer.AsSpan(_tamanho));
                _tamanho += buffer.Length;
            }

            public override void Write(char[] buffer, int index, int count)
            {
                Garantir(count);
                Array.Copy(buffer, index, _buffer, _tamanho, count);
                _tamanho += count;
            }

            private void Garantir(int adicionais)
            {
                if (_tamanho + adicionais <= _buffer.Length) return;
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _tamanho + adicionais));
            }
        }
    }
}
