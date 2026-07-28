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

    private static LogFilterCriteria Criteria(
        string quick = LogFilter.QuickAll,
        HashSet<string>? files = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null) => new()
        {
            QuickLevel = quick,
            VisibleFiles = files,
            From = from,
            To = to,
            Search = search,
        };

    // --- Filtro rápido por nível ------------------------------------------------

    [Fact]
    public void Quick_error_includes_both_Error_and_Fatal()
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

        var result = LogFilter.Apply(eventos, Criteria(LogFilter.QuickError));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Level == "Error");
        Assert.Contains(result, e => e.Level == "Fatal");
    }

    [Theory]
    [InlineData(LogFilter.QuickWarning, "Warning")]
    [InlineData(LogFilter.QuickInformation, "Information")]
    public void Quick_level_filters_to_that_level_only(string quick, string expected)
    {
        var eventos = new[] { Event("Error"), Event("Warning"), Event("Information"), Event("Debug") };

        var result = LogFilter.Apply(eventos, Criteria(quick));

        Assert.All(result, e => Assert.Equal(expected, e.Level));
        Assert.Single(result);
    }

    [Fact]
    public void Quick_all_keeps_every_level()
    {
        var eventos = new[] { Event("Error"), Event("Warning"), Event("Information"), Event("Debug"), Event("Verbose") };

        var result = LogFilter.Apply(eventos, Criteria());

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Level_comparison_is_case_insensitive()
    {
        var eventos = new[] { Event("error"), Event("FATAL") };

        var result = LogFilter.Apply(eventos, Criteria(LogFilter.QuickError));

        Assert.Equal(2, result.Count);
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
            quick: LogFilter.QuickError,
            files: new HashSet<string> { @"C:\logs\a.clef" },
            from: new DateTime(2026, 6, 15),
            search: "falha de rede"));

        Assert.Single(result);
        Assert.Same(alvo, result[0]);
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        var result = LogFilter.Apply(Array.Empty<ClefEvent>(), Criteria(LogFilter.QuickError));

        Assert.Empty(result);
    }
}
