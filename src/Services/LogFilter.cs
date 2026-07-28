using System;
using System.Collections.Generic;
using System.Linq;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>Critérios de filtragem da lista de eventos.</summary>
    public sealed class LogFilterCriteria
    {
        /// <summary>Quando definido, só eventos cujo <see cref="ClefEvent.SourceFile"/> estiver no conjunto.</summary>
        public HashSet<string>? VisibleFiles { get; init; }

        /// <summary>Filtro rápido por nível: <see cref="LogFilter.QuickAll"/>, <c>Error</c>, <c>Warning</c> ou <c>Information</c>.</summary>
        public string QuickLevel { get; init; } = LogFilter.QuickAll;

        /// <summary>Data inicial (comparada por dia, inclusiva).</summary>
        public DateTime? From { get; init; }

        /// <summary>Data final (comparada por dia, inclusiva).</summary>
        public DateTime? To { get; init; }

        /// <summary>Busca textual em mensagem, exceção e valores das propriedades.</summary>
        public string? Search { get; init; }
    }

    /// <summary>
    /// Filtragem da lista de eventos. Vive fora do componente Razor para poder ser
    /// testada isoladamente — antes essa lógica estava embutida no
    /// <c>LogViewer.AplicarFiltros</c>, onde só era exercitada pela UI.
    /// </summary>
    public static class LogFilter
    {
        public const string QuickAll = "Todos";
        public const string QuickError = "Error";
        public const string QuickWarning = "Warning";
        public const string QuickInformation = "Information";

        /// <summary>Aplica os critérios e devolve os eventos do mais recente para o mais antigo.</summary>
        public static List<ClefEvent> Apply(IEnumerable<ClefEvent> source, LogFilterCriteria criteria)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(criteria);

            IEnumerable<ClefEvent> query = source;

            if (criteria.VisibleFiles is { } visible)
            {
                query = query.Where(e => e.SourceFile != null && visible.Contains(e.SourceFile));
            }

            query = criteria.QuickLevel switch
            {
                // "Erros" inclui Fatal. Os parênteses importam: sem eles, "&&" ligaria mais
                // forte que "||" e o teste deixaria de cobrir o que se pretendia.
                QuickError => query.Where(e => IsLevel(e, "Error") || IsLevel(e, "Fatal")),
                QuickWarning => query.Where(e => IsLevel(e, "Warning")),
                QuickInformation => query.Where(e => IsLevel(e, "Information")),
                _ => query,
            };

            if (criteria.From is { } from)
            {
                query = query.Where(e => e.Timestamp.HasValue && e.Timestamp.Value.Date >= from.Date);
            }

            if (criteria.To is { } to)
            {
                query = query.Where(e => e.Timestamp.HasValue && e.Timestamp.Value.Date <= to.Date);
            }

            if (!string.IsNullOrWhiteSpace(criteria.Search))
            {
                var term = criteria.Search.Trim();
                query = query.Where(e => Matches(e, term));
            }

            return query.OrderByDescending(e => e.Timestamp).ToList();
        }

        private static bool IsLevel(ClefEvent e, string level) =>
            string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase);

        private static bool Matches(ClefEvent e, string term) =>
            (e.Message ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
            || (e.Exception ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
            || (e.Properties != null && e.Properties.Any(p =>
                   p.Value.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
