using ClefExplorer.Helpers;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Tests;

/// <summary>
/// Agregações do painel de estatísticas — o que transforma o app de leitor em analisador.
/// </summary>
public class LogStatisticsTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    private static ClefEvent Event(
        string level = "Information",
        string? message = "mensagem",
        string? template = null,
        string? exception = null,
        string? sourceContext = null,
        string? sourceFile = null,
        int minutosDepois = 0)
    {
        var ev = new ClefEvent
        {
            Level = level,
            Message = message,
            MessageTemplate = template,
            Exception = exception,
            SourceFile = sourceFile,
            Timestamp = Base.AddMinutes(minutosDepois),
        };

        if (sourceContext is not null)
        {
            ev.Properties = new Dictionary<string, LogEventPropertyValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["SourceContext"] = new ScalarValue(sourceContext),
            };
        }

        return ev;
    }

    // --- Contagem por nível --------------------------------------------------------

    [Fact]
    public void Counts_events_by_level()
    {
        var stats = LogStatistics.Compute(new[]
        {
            Event("Error"), Event("Error"), Event("Warning"), Event("Information"),
        });

        Assert.Equal(4, stats.Total);
        Assert.Equal(2, stats.ByLevel.Single(e => e.Key == "Error").Count);
        Assert.Equal(1, stats.ByLevel.Single(e => e.Key == "Warning").Count);
    }

    [Fact]
    public void Levels_come_in_severity_order_not_by_count()
    {
        // Ordem estável: o olho procura "Error" sempre no mesmo lugar, mesmo quando
        // Information é muito mais numeroso.
        var eventos = Enumerable.Repeat(Event("Information"), 50)
            .Append(Event("Error"))
            .Append(Event("Warning"))
            .ToArray();

        var stats = LogStatistics.Compute(eventos);

        Assert.Equal(new[] { "Error", "Warning", "Information" }, stats.ByLevel.Select(e => e.Key));
    }

    [Fact]
    public void An_unknown_level_still_appears_at_the_end()
    {
        var stats = LogStatistics.Compute(new[] { Event("Information"), Event("Auditoria") });

        Assert.Equal("Auditoria", stats.ByLevel.Last().Key);
    }

    [Fact]
    public void ErrorCount_covers_both_Error_and_Fatal()
    {
        var stats = LogStatistics.Compute(new[] { Event("Error"), Event("Fatal"), Event("Warning") });

        Assert.Equal(2, stats.ErrorCount);
    }

    // --- Mensagens -----------------------------------------------------------------

    [Fact]
    public void Messages_are_grouped_by_template_not_by_rendered_text()
    {
        // O ponto central do ranking: 3 renderizações do mesmo template são a MESMA
        // ocorrência. Agrupar pelo texto renderizado daria três linhas de contagem 1 e
        // esconderia justamente o que mais se repete.
        var eventos = new[]
        {
            Event(message: "Pedido 1 processado", template: "Pedido {Id} processado"),
            Event(message: "Pedido 2 processado", template: "Pedido {Id} processado"),
            Event(message: "Pedido 3 processado", template: "Pedido {Id} processado"),
        };

        var stats = LogStatistics.Compute(eventos);

        var top = Assert.Single(stats.TopMessages);
        Assert.Equal("Pedido {Id} processado", top.Key);
        Assert.Equal(3, top.Count);
    }

    [Fact]
    public void Without_a_template_the_rendered_message_is_used()
    {
        var stats = LogStatistics.Compute(new[] { Event(message: "sem template"), Event(message: "sem template") });

        Assert.Equal(2, stats.TopMessages.Single().Count);
    }

    [Fact]
    public void Rankings_are_capped()
    {
        var eventos = Enumerable.Range(0, 30).Select(i => Event(message: $"msg {i}")).ToArray();

        var stats = LogStatistics.Compute(eventos, topCount: 5);

        Assert.Equal(5, stats.TopMessages.Count);
    }

    // --- Exceções ------------------------------------------------------------------

    [Fact]
    public void Exceptions_are_grouped_by_their_first_line()
    {
        // O stack trace inteiro é único por ocorrência e não agruparia nada; a primeira
        // linha traz o tipo e a mensagem, que é o que se repete.
        var eventos = new[]
        {
            Event("Error", exception: "System.TimeoutException: tempo esgotado\n   at Foo.Bar()"),
            Event("Error", exception: "System.TimeoutException: tempo esgotado\n   at Outro.Metodo()"),
        };

        var stats = LogStatistics.Compute(eventos);

        var top = Assert.Single(stats.TopExceptions);
        Assert.Equal("System.TimeoutException: tempo esgotado", top.Key);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Events_without_an_exception_are_left_out_of_that_ranking()
    {
        var stats = LogStatistics.Compute(new[] { Event(), Event("Error", exception: "Boom: x") });

        Assert.Single(stats.TopExceptions);
    }

    // --- Origens -------------------------------------------------------------------

    [Fact]
    public void Source_prefers_the_SourceContext_property()
    {
        var stats = LogStatistics.Compute(new[]
        {
            Event(sourceContext: "Api.Pedido", sourceFile: @"C:\logs\a.clef"),
            Event(sourceContext: "Api.Pedido", sourceFile: @"C:\logs\b.clef"),
        });

        var top = Assert.Single(stats.TopSources);
        Assert.Equal("Api.Pedido", top.Key);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Source_falls_back_to_the_file_name()
    {
        var stats = LogStatistics.Compute(new[] { Event(sourceFile: @"C:\logs\pedidos.clef") });

        var top = Assert.Single(stats.TopSources);
        Assert.Equal(@"C:\logs\pedidos.clef", top.Key);   // a chave filtra pelo caminho
        Assert.Equal("pedidos.clef", top.Label);           // o rótulo mostra só o nome
    }

    // --- Timeline ------------------------------------------------------------------

    [Fact]
    public void The_timeline_has_the_requested_number_of_buckets()
    {
        var eventos = Enumerable.Range(0, 100).Select(i => Event(minutosDepois: i)).ToArray();

        var stats = LogStatistics.Compute(eventos, buckets: 10);

        Assert.Equal(10, stats.Timeline.Count);
        Assert.Equal(100, stats.Timeline.Sum(b => b.Total));
    }

    [Fact]
    public void The_timeline_separates_errors_from_the_total()
    {
        // É o que revela os picos: a série de erros sobreposta ao volume.
        var eventos = new[]
        {
            Event("Information", minutosDepois: 0),
            Event("Error", minutosDepois: 0),
            Event("Fatal", minutosDepois: 0),
        };

        var stats = LogStatistics.Compute(eventos, buckets: 1);

        Assert.Equal(3, stats.Timeline[0].Total);
        Assert.Equal(2, stats.Timeline[0].Errors);
    }

    [Fact]
    public void The_newest_event_lands_in_the_last_bucket()
    {
        // Sem o clamp, o evento do limite superior cairia num índice fora do vetor.
        var eventos = new[] { Event(minutosDepois: 0), Event(minutosDepois: 10) };

        var stats = LogStatistics.Compute(eventos, buckets: 4);

        Assert.Equal(2, stats.Timeline.Sum(b => b.Total));
        Assert.Equal(1, stats.Timeline.Last().Total);
    }

    [Fact]
    public void Events_all_at_the_same_instant_yield_a_single_bucket()
    {
        var eventos = new[] { Event(minutosDepois: 0), Event(minutosDepois: 0) };

        var stats = LogStatistics.Compute(eventos);

        Assert.Single(stats.Timeline);
        Assert.Equal(2, stats.Timeline[0].Total);
    }

    [Fact]
    public void The_bucket_size_shrinks_when_the_period_is_shorter()
    {
        // Comportamento observado ao filtrar: o mesmo painel re-fatia para o novo período.
        var largo = LogStatistics.Compute(
            Enumerable.Range(0, 50).Select(i => Event(minutosDepois: i * 60)).ToArray(), buckets: 10);
        var estreito = LogStatistics.Compute(
            Enumerable.Range(0, 50).Select(i => Event(minutosDepois: i)).ToArray(), buckets: 10);

        Assert.True(estreito.BucketSize < largo.BucketSize);
    }

    [Fact]
    public void Events_without_a_timestamp_do_not_break_the_timeline()
    {
        var eventos = new[] { new ClefEvent { Level = "Information" }, Event(minutosDepois: 5) };

        var stats = LogStatistics.Compute(eventos);

        Assert.Equal(2, stats.Total);
        Assert.NotEmpty(stats.Timeline);
    }

    // --- Vazio ---------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_bucket_count_fails_explicitly(int buckets)
    {
        // Sem a validação isto viraria DivideByZeroException lá dentro (ou um índice
        // negativo), bem longe da causa.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogStatistics.Compute(new[] { Event() }, buckets: buckets));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_top_count_fails_explicitly(int topCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogStatistics.Compute(new[] { Event() }, topCount: topCount));
    }

    [Fact]
    public void An_empty_set_yields_empty_stats_without_throwing()
    {
        var stats = LogStatistics.Compute(Array.Empty<ClefEvent>());

        Assert.Equal(0, stats.Total);
        Assert.Empty(stats.ByLevel);
        Assert.Empty(stats.Timeline);
        Assert.Equal(0, stats.ErrorCount);
    }
}
