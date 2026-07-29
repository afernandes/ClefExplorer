using System;
using System.Collections.Generic;
using System.Linq;
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

        public static LogStats Compute(IReadOnlyList<ClefEvent> events, int topCount = DefaultTopCount, int buckets = DefaultBuckets)
        {
            ArgumentNullException.ThrowIfNull(events);
            // buckets = 0 dividiria por zero em MontarTimeline e criaria um vetor vazio,
            // e um topCount não positivo devolveria rankings sempre vazios. Falhar aqui é
            // mais claro do que a exceção que apareceria lá dentro.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);

            if (events.Count == 0) return new LogStats();

            var (timeline, bucketSize) = MontarTimeline(events, buckets);

            return new LogStats
            {
                Total = events.Count,
                ByLevel = ContarPorNivel(events),
                TopMessages = TopPor(events, MensagemAgrupavel, topCount),
                TopExceptions = TopPor(events.Where(e => !string.IsNullOrEmpty(e.Exception)).ToList(), TipoDaExcecao, topCount),
                TopSources = TopPor(events, OrigemDoEvento, topCount),
                Timeline = timeline,
                BucketSize = bucketSize,
            };
        }

        // --- Níveis ------------------------------------------------------------------

        private static IReadOnlyList<StatEntry> ContarPorNivel(IReadOnlyList<ClefEvent> events)
        {
            var contagem = events
                .GroupBy(e => e.Level ?? "Information", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // Ordem fixa por gravidade, e não por contagem: a lista fica estável entre
            // filtragens, então o olho encontra "Error" sempre no mesmo lugar.
            var ordenados = LevelOrder
                .Where(contagem.ContainsKey)
                .Select(nivel => new StatEntry(nivel, nivel, contagem[nivel]))
                .ToList();

            // Níveis fora da lista conhecida (log de terceiro com nome próprio) vão ao fim.
            ordenados.AddRange(contagem
                .Where(kv => !LevelOrder.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new StatEntry(kv.Key, kv.Key, kv.Value)));

            return ordenados;
        }

        // --- Rankings ----------------------------------------------------------------

        private static IReadOnlyList<StatEntry> TopPor(
            IReadOnlyList<ClefEvent> events,
            Func<ClefEvent, (string Key, string Label)?> seletor,
            int topCount)
        {
            return events
                .Select(seletor)
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new StatEntry(g.Key, g.First().Label, g.Count()))
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
        private static (string, string)? TipoDaExcecao(ClefEvent e)
        {
            if (string.IsNullOrWhiteSpace(e.Exception)) return null;

            var primeiraLinha = e.Exception
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();

            return string.IsNullOrWhiteSpace(primeiraLinha) ? null : (primeiraLinha, primeiraLinha);
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

        private static (IReadOnlyList<TimeBucket> Timeline, TimeSpan BucketSize) MontarTimeline(
            IReadOnlyList<ClefEvent> events, int buckets)
        {
            var comHora = events.Where(e => e.Timestamp.HasValue).ToList();
            if (comHora.Count == 0) return (Array.Empty<TimeBucket>(), TimeSpan.Zero);

            var inicio = comHora.Min(e => e.Timestamp!.Value);
            var fim = comHora.Max(e => e.Timestamp!.Value);
            var periodo = fim - inicio;

            // Tudo no mesmo instante (ou um evento só): uma fatia basta.
            if (periodo <= TimeSpan.Zero)
            {
                return (new[] { new TimeBucket(inicio, comHora.Count, ContarErros(comHora)) }, TimeSpan.FromSeconds(1));
            }

            var bucketSize = TimeSpan.FromTicks(Math.Max(1, periodo.Ticks / buckets));

            var porFatia = new int[buckets];
            var errosPorFatia = new int[buckets];

            foreach (var e in comHora)
            {
                var offset = (e.Timestamp!.Value - inicio).Ticks / bucketSize.Ticks;
                // O evento mais recente cairia em `buckets`; encaixa na última fatia.
                var indice = (int)Math.Min(offset, buckets - 1);

                porFatia[indice]++;
                if (EhErro(e)) errosPorFatia[indice]++;
            }

            var timeline = Enumerable.Range(0, buckets)
                .Select(i => new TimeBucket(inicio + TimeSpan.FromTicks(bucketSize.Ticks * i), porFatia[i], errosPorFatia[i]))
                .ToList();

            return (timeline, bucketSize);
        }

        private static int ContarErros(IEnumerable<ClefEvent> events) => events.Count(EhErro);

        private static bool EhErro(ClefEvent e) =>
            string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Level, "Fatal", StringComparison.OrdinalIgnoreCase);
    }
}
