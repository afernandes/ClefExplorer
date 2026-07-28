using ClefExplorer.Helpers;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Tests;

/// <summary>
/// Descoberta de colunas a partir do conteúdo dos logs. Cada aplicação emite um conjunto
/// próprio de propriedades estruturadas, então as colunas não podem ser fixadas na mão.
/// </summary>
public class LogColumnDiscoveryTests
{
    private static ClefEvent Event(params (string Key, object Value)[] props)
    {
        var ev = new ClefEvent
        {
            Level = "Information",
            Timestamp = DateTimeOffset.UtcNow,
            Properties = new Dictionary<string, LogEventPropertyValue>(StringComparer.OrdinalIgnoreCase),
        };

        foreach (var (key, value) in props)
        {
            ev.Properties![key] = new ScalarValue(value);
        }

        return ev;
    }

    // --- Descoberta --------------------------------------------------------------

    [Fact]
    public void Discovers_properties_present_in_the_events()
    {
        var eventos = new[]
        {
            Event(("SourceContext", "Api.Pedido"), ("RequestId", "req-1")),
            Event(("SourceContext", "Api.Pagamento"), ("RequestId", "req-2")),
        };

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.Contains(colunas, c => c.Key == "SourceContext");
        Assert.Contains(colunas, c => c.Key == "RequestId");
    }

    [Fact]
    public void Orders_by_frequency_so_the_most_useful_come_first()
    {
        var eventos = new[]
        {
            Event(("Comum", 1), ("Raro", 1)),
            Event(("Comum", 2)),
            Event(("Comum", 3)),
        };

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.Equal("Comum", colunas[0].Key);
    }

    [Fact]
    public void Ignores_properties_that_are_too_rare_to_be_worth_a_column()
    {
        // 1 evento em 100 com a propriedade: abaixo do mínimo de 5%.
        var eventos = Enumerable.Range(0, 100)
            .Select(i => i == 0 ? Event(("QuaseNunca", 1), ("Sempre", 1)) : Event(("Sempre", 1)))
            .ToArray();

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.Contains(colunas, c => c.Key == "Sempre");
        Assert.DoesNotContain(colunas, c => c.Key == "QuaseNunca");
    }

    [Fact]
    public void Skips_properties_that_already_have_a_fixed_column()
    {
        var eventos = new[] { Event(("SourceFile", @"C:\logs\a.clef"), ("Level", "Error"), ("Util", 1)) };

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.DoesNotContain(colunas, c => c.Key == "SourceFile");
        Assert.DoesNotContain(colunas, c => c.Key == "Level");
        Assert.Contains(colunas, c => c.Key == "Util");
    }

    [Fact]
    public void Caps_the_number_of_columns_so_the_menu_stays_usable()
    {
        var muitas = Enumerable.Range(0, 50).Select(i => ($"Prop{i}", (object)i)).ToArray();

        var colunas = LogColumnDiscovery.Discover(new[] { Event(muitas) }, maxColumns: 5);

        Assert.Equal(5, colunas.Count);
    }

    [Fact]
    public void The_order_is_stable_between_runs()
    {
        // Sem desempate estável, as colunas dançariam a cada abertura do arquivo.
        var eventos = new[] { Event(("Bbb", 1), ("Aaa", 1), ("Ccc", 1)) };

        var primeira = LogColumnDiscovery.Discover(eventos).Select(c => c.Key);
        var segunda = LogColumnDiscovery.Discover(eventos).Select(c => c.Key);

        Assert.Equal(primeira, segunda);
    }

    [Fact]
    public void Only_the_sample_is_inspected()
    {
        // A propriedade só existe além da amostra: não deve virar coluna.
        var eventos = Enumerable.Range(0, 10)
            .Select(i => i < 5 ? Event(("Cedo", 1)) : Event(("Tarde", 1)))
            .ToArray();

        var colunas = LogColumnDiscovery.Discover(eventos, sampleSize: 5);

        Assert.Contains(colunas, c => c.Key == "Cedo");
        Assert.DoesNotContain(colunas, c => c.Key == "Tarde");
    }

    [Fact]
    public void An_empty_set_yields_no_columns()
    {
        Assert.Empty(LogColumnDiscovery.Discover(Array.Empty<ClefEvent>()));
    }

    [Fact]
    public void Events_without_properties_do_not_break_discovery()
    {
        var eventos = new[] { new ClefEvent { Level = "Information" }, Event(("Util", 1)) };

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.Contains(colunas, c => c.Key == "Util");
    }

    // --- Rótulo -------------------------------------------------------------------

    [Theory]
    [InlineData("RequestId", "Request Id")]
    [InlineData("SourceContext", "Source Context")]
    [InlineData("MachineName", "Machine Name")]
    [InlineData("Id", "Id")]
    [InlineData("HTTPRequest", "HTTP Request")]   // sigla seguida de palavra
    [InlineData("threadId", "thread Id")]          // já começa minúsculo
    [InlineData("", "")]
    public void Property_names_become_readable_headers(string key, string expected)
    {
        Assert.Equal(expected, LogColumnDiscovery.Humanize(key));
    }

    // --- Valor da célula ----------------------------------------------------------

    [Fact]
    public void Scalar_strings_are_shown_without_the_quotes_serilog_adds()
    {
        var ev = Event(("SourceContext", "Api.Pedido"));

        Assert.Equal("Api.Pedido", LogColumnDiscovery.FormatValue(ev, "SourceContext"));
    }

    [Fact]
    public void Numbers_are_shown_as_is()
    {
        Assert.Equal("42", LogColumnDiscovery.FormatValue(Event(("PedidoId", 42)), "PedidoId"));
    }

    [Fact]
    public void A_missing_property_yields_an_empty_cell()
    {
        Assert.Equal(string.Empty, LogColumnDiscovery.FormatValue(Event(("Outra", 1)), "Inexistente"));
    }

    [Fact]
    public void An_event_without_properties_yields_an_empty_cell()
    {
        Assert.Equal(string.Empty, LogColumnDiscovery.FormatValue(new ClefEvent(), "Qualquer"));
    }
}
