using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="LogFilter"/> — a filtragem que antes vivia dentro do
/// componente <c>LogViewer</c> e só era exercitada pela UI.
/// </summary>
public class LogFilterTests
{
    private static ClefEvent Event(
        string level = "Information",
        string? message = null,
        string? exception = null,
        string? sourceFile = null,
        DateTimeOffset? timestamp = null) => new()
        {
            Level = level,
            Message = message,
            Exception = exception,
            SourceFile = sourceFile,
            Timestamp = timestamp ?? new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
        };

    private static HashSet<string> Levels(params string[] levels) =>
        new(levels, StringComparer.OrdinalIgnoreCase);

    private static LogFilterCriteria Criteria(
        HashSet<string>? levels = null,
        HashSet<string>? files = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null,
        bool useRegex = false) => new()
        {
            Levels = levels,
            VisibleFiles = files,
            From = from,
            To = to,
            Search = search,
            UseRegex = useRegex,
        };

    // --- Filtro por nível -------------------------------------------------------

    [Fact]
    public void The_error_group_includes_both_Error_and_Fatal()
    {
        // Regressão: a expressão original era "e != null && Error || Fatal", que por
        // precedência de operador vira "(e != null && Error) || Fatal".
        var eventos = new[]
        {
            Event("Error", "boom"),
            Event("Fatal", "catástrofe"),
            Event("Warning", "cuidado"),
            Event("Information", "ok"),
        };

        var result = LogFilter.Apply(eventos, Criteria(Levels("Error", "Fatal")));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Level == "Error");
        Assert.Contains(result, e => e.Level == "Fatal");
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("Information")]
    [InlineData("Debug")]    // antes não tinha como ser selecionado
    [InlineData("Verbose")]  // idem
    public void A_single_level_filters_to_that_level_only(string level)
    {
        var eventos = new[] { Event("Error"), Event("Warning"), Event("Information"), Event("Debug"), Event("Verbose") };

        var result = LogFilter.Apply(eventos, Criteria(Levels(level)));

        Assert.Single(result);
        Assert.Equal(level, result[0].Level);
    }

    [Fact]
    public void Levels_can_be_combined()
    {
        // O grande ganho da seleção múltipla: ver erros e avisos juntos.
        var eventos = new[] { Event("Error"), Event("Warning"), Event("Information"), Event("Debug") };

        var result = LogFilter.Apply(eventos, Criteria(Levels("Error", "Warning")));

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Level == "Information");
    }

    [Fact]
    public void No_level_selected_keeps_every_level()
    {
        var eventos = new[] { Event("Error"), Event("Warning"), Event("Information"), Event("Debug"), Event("Verbose") };

        Assert.Equal(5, LogFilter.Apply(eventos, Criteria()).Count);
        Assert.Equal(5, LogFilter.Apply(eventos, Criteria(Levels())).Count);
    }

    [Fact]
    public void Level_comparison_is_case_insensitive()
    {
        var eventos = new[] { Event("error"), Event("FATAL") };

        var result = LogFilter.Apply(eventos, Criteria(Levels("Error", "Fatal")));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void AllLevels_lists_every_serilog_level()
    {
        Assert.Equal(
            new[] { "Fatal", "Error", "Warning", "Information", "Debug", "Verbose" },
            LogFilter.AllLevels);
    }

    // --- Busca com expressão regular ---------------------------------------------

    [Fact]
    public void Regex_search_matches_by_pattern()
    {
        var eventos = new[]
        {
            Event(message: "pedido 12345 aprovado"),
            Event(message: "pedido ABC rejeitado"),
        };

        var result = LogFilter.Apply(eventos, Criteria(search: @"pedido \d+", useRegex: true));

        Assert.Single(result);
        Assert.Contains("12345", result[0].Message);
    }

    [Fact]
    public void Regex_search_is_case_insensitive()
    {
        var eventos = new[] { Event(message: "TIMEOUT na chamada") };

        Assert.Single(LogFilter.Apply(eventos, Criteria(search: "^timeout", useRegex: true)));
    }

    [Fact]
    public void Regex_search_also_covers_the_exception()
    {
        var eventos = new[] { Event(message: "falhou", exception: "System.TimeoutException: ...") };

        Assert.Single(LogFilter.Apply(eventos, Criteria(search: @"\w+Exception", useRegex: true)));
    }

    [Fact]
    public void An_invalid_regex_yields_no_results_instead_of_throwing()
    {
        // Enquanto se digita, o padrão passa por estados inválidos ("(", "[a-"). Lançar
        // a cada tecla seria inutilizável.
        var eventos = new[] { Event(message: "qualquer coisa") };

        var result = LogFilter.Apply(eventos, Criteria(search: "(sem fechar", useRegex: true));

        Assert.Empty(result);
    }

    [Fact]
    public void Without_the_regex_flag_the_term_is_literal()
    {
        var eventos = new[] { Event(message: "custo: R$ 5.00"), Event(message: "custo: R$ 5X00") };

        // O '.' literal não pode casar com o 'X'.
        var result = LogFilter.Apply(eventos, Criteria(search: "5.00"));

        Assert.Single(result);
        Assert.Contains("5.00", result[0].Message);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(@"\d+", true)]
    [InlineData("(", false)]
    [InlineData("[a-", false)]
    public void IsValidRegex_reports_whether_the_pattern_compiles(string? pattern, bool expected)
    {
        Assert.Equal(expected, LogFilter.IsValidRegex(pattern));
    }

    // --- Filtro por arquivo -----------------------------------------------------

    [Fact]
    public void Visible_files_restricts_to_the_given_sources()
    {
        var eventos = new[]
        {
            Event(sourceFile: @"C:\logs\a.clef"),
            Event(sourceFile: @"C:\logs\b.clef"),
            Event(sourceFile: null),
        };

        var result = LogFilter.Apply(eventos, Criteria(files: new HashSet<string> { @"C:\logs\a.clef" }));

        Assert.Single(result);
        Assert.Equal(@"C:\logs\a.clef", result[0].SourceFile);
    }

    [Fact]
    public void Null_visible_files_keeps_everything()
    {
        var eventos = new[] { Event(sourceFile: @"C:\logs\a.clef"), Event(sourceFile: null) };

        var result = LogFilter.Apply(eventos, Criteria(files: null));

        Assert.Equal(2, result.Count);
    }

    // --- Filtro por data --------------------------------------------------------

    [Fact]
    public void Date_range_is_inclusive_on_both_ends_and_compares_by_day()
    {
        var eventos = new[]
        {
            Event(timestamp: new DateTimeOffset(2026, 6, 14, 23, 59, 0, TimeSpan.Zero)),
            Event(timestamp: new DateTimeOffset(2026, 6, 15, 0, 1, 0, TimeSpan.Zero)),
            Event(timestamp: new DateTimeOffset(2026, 6, 16, 23, 0, 0, TimeSpan.Zero)),
            Event(timestamp: new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero)),
        };

        var result = LogFilter.Apply(eventos, Criteria(
            from: new DateTime(2026, 6, 15),
            to: new DateTime(2026, 6, 16)));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Events_without_timestamp_are_excluded_when_a_date_filter_is_set()
    {
        var eventos = new[] { new ClefEvent { Level = "Information", Timestamp = null } };

        var result = LogFilter.Apply(eventos, Criteria(from: new DateTime(2026, 1, 1)));

        Assert.Empty(result);
    }

    // --- Busca textual ----------------------------------------------------------

    [Theory]
    [InlineData("timeout")]
    [InlineData("TIMEOUT")]
    [InlineData("  timeout  ")] // termo é aparado antes da busca
    public void Search_matches_message_case_insensitively_and_trims(string term)
    {
        var eventos = new[] { Event(message: "Request Timeout ao chamar a API"), Event(message: "tudo certo") };

        var result = LogFilter.Apply(eventos, Criteria(search: term));

        Assert.Single(result);
    }

    [Fact]
    public void Search_also_matches_the_exception_text()
    {
        var eventos = new[] { Event(message: "falhou", exception: "System.NullReferenceException: ..."), Event(message: "ok") };

        var result = LogFilter.Apply(eventos, Criteria(search: "NullReference"));

        Assert.Single(result);
    }

    [Fact]
    public void Blank_search_does_not_filter()
    {
        var eventos = new[] { Event(message: "a"), Event(message: "b") };

        var result = LogFilter.Apply(eventos, Criteria(search: "   "));

        Assert.Equal(2, result.Count);
    }

    // --- Ordenação e composição -------------------------------------------------

    [Fact]
    public void Result_is_ordered_from_newest_to_oldest()
    {
        var older = Event(message: "antigo", timestamp: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Event(message: "novo", timestamp: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));
        var middle = Event(message: "meio", timestamp: new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));

        var result = LogFilter.Apply(new[] { older, newer, middle }, Criteria());

        Assert.Equal(new[] { "novo", "meio", "antigo" }, result.Select(e => e.Message));
    }

    [Fact]
    public void Criteria_are_combined_with_AND()
    {
        var alvo = Event("Error", "falha de rede", sourceFile: @"C:\logs\a.clef",
            timestamp: new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        var eventos = new[]
        {
            alvo,
            Event("Warning", "falha de rede", sourceFile: @"C:\logs\a.clef"),     // nível não bate
            Event("Error", "outra coisa", sourceFile: @"C:\logs\a.clef"),          // busca não bate
            Event("Error", "falha de rede", sourceFile: @"C:\logs\b.clef"),        // arquivo não bate
            Event("Error", "falha de rede", sourceFile: @"C:\logs\a.clef",
                timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), // data não bate
        };

        var result = LogFilter.Apply(eventos, Criteria(
            levels: Levels("Error", "Fatal"),
            files: new HashSet<string> { @"C:\logs\a.clef" },
            from: new DateTime(2026, 6, 15),
            search: "falha de rede"));

        Assert.Single(result);
        Assert.Same(alvo, result[0]);
    }

    [Fact]
    public void InputAlreadySorted_preserves_the_input_order()
    {
        // Otimização: os filtros do LINQ preservam a ordem relativa, então quando a
        // entrada já vem ordenada (como a do LogStore) a reordenação é desperdício.
        var a = Event(message: "primeiro", timestamp: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));
        var b = Event(message: "segundo", timestamp: new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
        var c = Event(message: "terceiro", timestamp: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var result = LogFilter.Apply(new[] { a, b, c }, new LogFilterCriteria { InputAlreadySorted = true });

        Assert.Equal(new[] { "primeiro", "segundo", "terceiro" }, result.Select(e => e.Message));
    }

    [Fact]
    public void InputAlreadySorted_still_applies_every_filter()
    {
        var eventos = new[] { Event("Error", "manter"), Event("Information", "descartar") };

        var result = LogFilter.Apply(eventos, new LogFilterCriteria
        {
            Levels = Levels("Error", "Fatal"),
            InputAlreadySorted = true,
        });

        Assert.Single(result);
        Assert.Equal("manter", result[0].Message);
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        var result = LogFilter.Apply(Array.Empty<ClefEvent>(), Criteria(Levels("Error", "Fatal")));

        Assert.Empty(result);
    }
}
