using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

namespace ClefExplorer.Helpers
{
    /// <summary>Um item de ranking (nível, mensagem, exceção, origem…) com sua contagem.</summary>
    /// <param name="Key">Valor usado para filtrar ao clicar.</param>
    /// <param name="Label">Texto exibido.</param>
    /// <param name="Count">Quantidade de eventos.</param>
    public sealed record StatEntry(string Key, string Label, int Count);

    /// <summary>Uma fatia do histograma temporal.</summary>
    /// <param name="Start">Início do intervalo.</param>
    /// <param name="Total">Eventos no intervalo.</param>
    /// <param name="Errors">Quantos deles são Error/Fatal — é o que revela os picos.</param>
    public sealed record TimeBucket(DateTimeOffset Start, int Total, int Errors);

    /// <summary>Visão agregada do conjunto filtrado.</summary>
    public sealed class LogStats
    {
        public int Total { get; init; }
        public IReadOnlyList<StatEntry> ByLevel { get; init; } = Array.Empty<StatEntry>();
        public IReadOnlyList<StatEntry> TopMessages { get; init; } = Array.Empty<StatEntry>();
        public IReadOnlyList<StatEntry> TopExceptions { get; init; } = Array.Empty<StatEntry>();
        public IReadOnlyList<StatEntry> TopSources { get; init; } = Array.Empty<StatEntry>();
        public IReadOnlyList<TimeBucket> Timeline { get; init; } = Array.Empty<TimeBucket>();

        /// <summary>Tamanho de cada fatia da timeline, para rotular o eixo.</summary>
        public TimeSpan BucketSize { get; init; }

        public int ErrorCount => ByLevel
            .Where(e => e.Key is "Error" or "Fatal")
            .Sum(e => e.Count);
    }

    /// <summary>
    /// Calcula a visão agregada dos eventos filtrados: o que transforma o app de leitor em
    /// analisador. Puro e sem dependência de UI, para poder ser testado direto.
    /// </summary>
    public static class LogStatistics
    {
        /// <summary>Quantas entradas cada ranking traz.</summary>
        public const int DefaultTopCount = 8;

        /// <summary>Fatias desejadas na timeline. O tamanho de cada uma sai do período coberto.</summary>
        public const int DefaultBuckets = 40;

        /// <summary>Ordem de exibição dos níveis: do mais grave ao mais verboso.</summary>
        private static readonly string[] LevelOrder = LogFilter.AllLevels;

        /// <summary>Acumulador de um ranking: o rótulo da primeira ocorrência e quantas houve.</summary>
        private record struct Acumulado(string Label, int Count);

        /// <summary>
        /// Uma passagem de varredura acumula tudo o que não depende do período; a timeline
        /// exige uma segunda porque o tamanho da fatia só se conhece depois de saber o
        /// primeiro e o último instante. A versão anterior fazia SETE passagens LINQ e ainda
        /// copiava o conjunto inteiro (<c>Where().ToList()</c> na timeline). Medido com 1
        /// milhão de eventos: 414 ms e 144 MB alocados (4 coletas gen0 e 3 gen1) contra
        /// 122 ms e 9 MB — e tudo isso acontecia na thread da UI, a cada filtragem.
        /// </summary>
        public static LogStats Compute(IReadOnlyList<ClefEvent> events, int topCount = DefaultTopCount, int buckets = DefaultBuckets)
        {
            ArgumentNullException.ThrowIfNull(events);
            // buckets = 0 dividiria por zero em MontarTimeline e criaria um vetor vazio,
            // e um topCount não positivo devolveria rankings sempre vazios. Falhar aqui é
            // mais claro do que a exceção que apareceria lá dentro.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);

            if (events.Count == 0) return new LogStats();

            var porNivel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var porMensagem = new Dictionary<string, Acumulado>(StringComparer.OrdinalIgnoreCase);
            var porExcecao = new Dictionary<string, Acumulado>(StringComparer.OrdinalIgnoreCase);
            var porOrigem = new Dictionary<string, Acumulado>(StringComparer.OrdinalIgnoreCase);

            var comHora = 0;
            var primeiro = DateTimeOffset.MaxValue;
            var ultimo = DateTimeOffset.MinValue;

            foreach (var evento in events)
            {
                AcumularNivel(porNivel, evento.Level ?? "Information");
                Acumular(porMensagem, MensagemAgrupavel(evento));
                Acumular(porExcecao, TipoDaExcecao(evento));
                Acumular(porOrigem, OrigemDoEvento(evento));

                if (evento.Timestamp is { } instante)
                {
                    comHora++;
                    if (instante < primeiro) primeiro = instante;
                    if (instante > ultimo) ultimo = instante;
                }
            }

            var (timeline, bucketSize) = MontarTimeline(events, comHora, primeiro, ultimo, buckets);

            return new LogStats
            {
                Total = events.Count,
                ByLevel = OrdenarNiveis(porNivel),
                TopMessages = Ranking(porMensagem, topCount),
                TopExceptions = Ranking(porExcecao, topCount),
                TopSources = Ranking(porOrigem, topCount),
                Timeline = timeline,
                BucketSize = bucketSize,
            };
        }

        // --- Acumulação ---------------------------------------------------------------

        /// <summary>
        /// <c>GetValueRefOrAddDefault</c> em vez de TryGetValue + indexador: são milhões de
        /// atualizações por cálculo, e assim cada evento paga UM hash em vez de dois.
        /// </summary>
        private static void Acumular(Dictionary<string, Acumulado> destino, (string Key, string Label)? item)
        {
            if (item is not { } valor) return;

            ref var entrada = ref CollectionsMarshal.GetValueRefOrAddDefault(destino, valor.Key, out var existia);
            // O rótulo é o da PRIMEIRA ocorrência da chave, como fazia o g.First().Label
            // do GroupBy anterior: chave e rótulo divergem (caminho x nome do arquivo) e
            // trocá-los a cada evento faria o ranking oscilar entre filtragens.
            if (!existia) entrada.Label = valor.Label;
            entrada.Count++;
        }

        private static void AcumularNivel(Dictionary<string, int> destino, string nivel)
        {
            ref var contagem = ref CollectionsMarshal.GetValueRefOrAddDefault(destino, nivel, out _);
            contagem++;
        }

        // --- Níveis ------------------------------------------------------------------

        private static IReadOnlyList<StatEntry> OrdenarNiveis(Dictionary<string, int> contagem)
        {
            // Ordem fixa por gravidade, e não por contagem: a lista fica estável entre
            // filtragens, então o olho encontra "Error" sempre no mesmo lugar.
            var ordenados = new List<StatEntry>(contagem.Count);
            foreach (var nivel in LevelOrder)
            {
                if (contagem.TryGetValue(nivel, out var total))
                {
                    ordenados.Add(new StatEntry(nivel, nivel, total));
                }
            }

            // Níveis fora da lista conhecida (log de terceiro com nome próprio) vão ao fim.
            // OrderByDescending (estável) e não List.Sort: empates precisam manter a ordem
            // de primeira aparição, senão dois níveis com a mesma contagem trocam de lugar
            // a cada recálculo.
            var desconhecidos = contagem
                .Where(kv => !LevelOrder.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value);

            foreach (var kv in desconhecidos)
            {
                ordenados.Add(new StatEntry(kv.Key, kv.Key, kv.Value));
            }

            return ordenados;
        }

        // --- Rankings ----------------------------------------------------------------

        private static IReadOnlyList<StatEntry> Ranking(Dictionary<string, Acumulado> contagem, int topCount)
        {
            return contagem
                .Select(kv => new StatEntry(kv.Key, kv.Value.Label, kv.Value.Count))
                .OrderByDescending(e => e.Count)
                .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .Take(topCount)
                .ToList();
        }

        /// <summary>
        /// Agrupa pelo TEMPLATE, não pela mensagem renderizada: "Pedido 1 processado" e
        /// "Pedido 2 processado" são a mesma ocorrência com parâmetros diferentes, e contá-las
        /// separadamente esconderia justamente o que mais se repete.
        /// </summary>
        private static (string, string)? MensagemAgrupavel(ClefEvent e)
        {
            var chave = !string.IsNullOrWhiteSpace(e.MessageTemplate) ? e.MessageTemplate : e.Message;
            return string.IsNullOrWhiteSpace(chave) ? null : (chave, chave);
        }

        /// <summary>
        /// Primeira linha da exceção — normalmente "Namespace.TipoException: mensagem".
        /// O stack trace inteiro seria único por ocorrência e não agruparia nada.
        /// </summary>
        /// <remarks>
        /// Recorta com Span em vez de <c>Split('\n')</c>: o Split fatiava o stack trace
        /// INTEIRO (array + uma string por linha) só para ficar com a primeira, e um log
        /// com muitas exceções pagava isso por evento a cada recálculo.
        /// </remarks>
        private static (string, string)? TipoDaExcecao(ClefEvent e)
        {
            if (string.IsNullOrWhiteSpace(e.Exception)) return null;

            var texto = e.Exception;

            // Pular as quebras iniciais reproduz o RemoveEmptyEntries do Split: entradas
            // de tamanho zero eram descartadas, entradas só com espaço NÃO.
            var inicio = 0;
            while (inicio < texto.Length && texto[inicio] == '\n') inicio++;
            if (inicio == texto.Length) return null;

            var fim = texto.IndexOf('\n', inicio);
            if (fim < 0) fim = texto.Length;

            var primeiraLinha = texto.AsSpan(inicio, fim - inicio).Trim();
            if (primeiraLinha.IsEmpty) return null;

            var chave = new string(primeiraLinha);
            return (chave, chave);
        }

        /// <summary>
        /// Origem do evento: o <c>SourceContext</c> (a classe que logou) quando existe;
        /// senão o arquivo. É o que responde "de onde vem esse barulho todo".
        /// </summary>
        private static (string, string)? OrigemDoEvento(ClefEvent e)
        {
            if (e.Properties is not null
                && e.Properties.TryGetValue("SourceContext", out var ctx)
                && ctx is ScalarValue { Value: string s }
                && !string.IsNullOrWhiteSpace(s))
            {
                return (s, s);
            }

            if (!string.IsNullOrEmpty(e.SourceFile))
            {
                return (e.SourceFile, System.IO.Path.GetFileName(e.SourceFile));
            }

            return null;
        }

        // --- Timeline -----------------------------------------------------------------

        /// <summary>
        /// Distribui os eventos nas fatias. Recebe o período já apurado pela varredura
        /// principal — antes, este método refazia o trabalho com um <c>Where().ToList()</c>
        /// (uma cópia do conjunto inteiro) seguido de Min e Max em passadas próprias.
        /// </summary>
        private static (IReadOnlyList<TimeBucket> Timeline, TimeSpan BucketSize) MontarTimeline(
            IReadOnlyList<ClefEvent> events,
            int comHora,
            DateTimeOffset inicio,
            DateTimeOffset fim,
            int buckets)
        {
            if (comHora == 0) return (Array.Empty<TimeBucket>(), TimeSpan.Zero);

            var periodo = fim - inicio;

            // Tudo no mesmo instante (ou um evento só): uma fatia basta.
            if (periodo <= TimeSpan.Zero)
            {
                var erros = 0;
                foreach (var e in events)
                {
                    if (e.Timestamp.HasValue && EhErro(e)) erros++;
                }

                return (new[] { new TimeBucket(inicio, comHora, erros) }, TimeSpan.FromSeconds(1));
            }

            var bucketSize = TimeSpan.FromTicks(Math.Max(1, periodo.Ticks / buckets));

            var porFatia = new int[buckets];
            var errosPorFatia = new int[buckets];

            foreach (var e in events)
            {
                if (e.Timestamp is not { } instante) continue;

                var offset = (instante - inicio).Ticks / bucketSize.Ticks;
                // O evento mais recente cairia em `buckets`; encaixa na última fatia.
                var indice = (int)Math.Min(offset, buckets - 1);

                porFatia[indice]++;
                if (EhErro(e)) errosPorFatia[indice]++;
            }

            var timeline = new TimeBucket[buckets];
            for (var i = 0; i < buckets; i++)
            {
                timeline[i] = new TimeBucket(
                    inicio + TimeSpan.FromTicks(bucketSize.Ticks * i),
                    porFatia[i],
                    errosPorFatia[i]);
            }

            return (timeline, bucketSize);
        }

        private static bool EhErro(ClefEvent e) =>
            string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Level, "Fatal", StringComparison.OrdinalIgnoreCase);
    }
}
