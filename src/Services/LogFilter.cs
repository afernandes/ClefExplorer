using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClefExplorer.Models;

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

        /// <summary>Data inicial (comparada por dia, inclusiva).</summary>
        public DateTime? From { get; init; }

        /// <summary>Data final (comparada por dia, inclusiva).</summary>
        public DateTime? To { get; init; }

        /// <summary>Busca textual em mensagem, exceção e valores das propriedades.</summary>
        public string? Search { get; init; }

        /// <summary>Interpreta <see cref="Search"/> como expressão regular.</summary>
        public bool UseRegex { get; init; }

        /// <summary>
        /// Quando a entrada já vem do mais recente para o mais antigo, dispensa a ordenação
        /// final — os filtros do LINQ preservam a ordem relativa. O <c>LogStore</c> mantém
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
        /// <summary>Níveis do Serilog, do mais grave ao mais verboso.</summary>
        public static readonly string[] AllLevels =
            { "Fatal", "Error", "Warning", "Information", "Debug", "Verbose" };

        /// <summary>Diz se o texto é uma expressão regular válida (para avisar antes de filtrar).</summary>
        public static bool IsValidRegex(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return true;
            try
            {
                _ = new Regex(pattern);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

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

            if (criteria.Levels is { Count: > 0 } levels)
            {
                query = query.Where(e => e.Level != null && levels.Contains(e.Level));
            }

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
                var matcher = BuildMatcher(criteria);
                // Regex inválida: nenhum resultado, em vez de lançar. A UI avisa que o
                // padrão está incompleto — filtrar enquanto se digita produziria erro a
                // cada tecla.
                query = matcher is null ? Enumerable.Empty<ClefEvent>() : query.Where(matcher);
            }

            return criteria.InputAlreadySorted
                ? query.ToList()
                : query.OrderByDescending(e => e.Timestamp).ToList();
        }

        /// <summary>Devolve o predicado de busca, ou <c>null</c> se a regex for inválida.</summary>
        private static Func<ClefEvent, bool>? BuildMatcher(LogFilterCriteria criteria)
        {
            var term = criteria.Search!.Trim();

            if (!criteria.UseRegex)
            {
                return e => Contains(e, texto => texto.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            try
            {
                var regex = new Regex(term, RegexOptions.IgnoreCase);
                return e => Contains(e, regex.IsMatch);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Roda o predicado sobre mensagem, exceção e valores das propriedades.</summary>
        private static bool Contains(ClefEvent e, Func<string, bool> predicate) =>
            predicate(e.Message ?? string.Empty)
            || predicate(e.Exception ?? string.Empty)
            || (e.Properties != null && e.Properties.Any(p => predicate(p.Value.ToString())));
    }
}
