using ClefExplorer.Helpers;
using ClefExplorer.Models;
using Omni.Blazor.Models;
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
        var propriedades = new Dictionary<string, LogEventPropertyValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in props)
        {
            propriedades[key] = new ScalarValue(value);
        }

        return new ClefEvent
        {
            Level = "Information",
            Timestamp = DateTimeOffset.UtcNow,
            Properties = propriedades,
        };
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

    // --- Campos de message template -----------------------------------------------

    private static ClefEvent EventoComTemplate(string template, params (string Key, object Value)[] props)
    {
        var ev = Event(props);
        ev.MessageTemplate = template;
        return ev;
    }

    [Fact]
    public void A_template_field_becomes_a_column_even_when_it_is_rare()
    {
        // Um log com muitos templates dilui cada campo bem abaixo do corte de frequência —
        // e são justamente eles que dão sentido a agrupar e filtrar pela mensagem.
        var eventos = Enumerable.Range(0, 100)
            .Select(i => i == 0
                ? EventoComTemplate("Intervalo para {ProviderKey}: {Interval}s", ("ProviderKey", "RacClient"), ("Interval", 960), ("Sempre", 1))
                : Event(("Sempre", 1)))
            .ToArray();

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.Contains(colunas, c => c.Key == "ProviderKey" && c.Source == ColumnSource.TemplateField);
        Assert.Contains(colunas, c => c.Key == "Interval" && c.Source == ColumnSource.TemplateField);
    }

    [Fact]
    public void A_template_field_keeps_the_type_inferred_from_its_values()
    {
        // Sem o tipo, o filtro da coluna ofereceria operadores de texto para um número.
        var eventos = new[]
        {
            EventoComTemplate("Intervalo {Interval}s", ("Interval", 960)),
            EventoComTemplate("Intervalo {Interval}s", ("Interval", 78)),
        };

        var coluna = LogColumnDiscovery.Discover(eventos).Single(c => c.Key == "Interval");

        Assert.Equal(ColumnValueKind.Number, coluna.Kind);
    }

    [Fact]
    public void A_frequent_template_field_is_listed_once_as_a_property()
    {
        // Presente em todos os eventos: já passa pelo corte de frequência. Aparecer também
        // na seção de campos daria duas colunas idênticas no menu.
        var eventos = Enumerable.Range(0, 10)
            .Select(_ => EventoComTemplate("Pedido {OrderId}", ("OrderId", 7)))
            .ToArray();

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.Single(colunas, c => c.Key == "OrderId");
        Assert.Equal(ColumnSource.Property, colunas.Single(c => c.Key == "OrderId").Source);
    }

    [Fact]
    public void A_template_field_without_a_matching_property_yields_no_column()
    {
        // O template cita {Ausente}, mas nenhum evento carrega a propriedade: uma coluna
        // sempre vazia só ocuparia espaço.
        var eventos = new[] { EventoComTemplate("Algo sobre {Ausente}", ("Outra", 1)) };

        var colunas = LogColumnDiscovery.Discover(eventos);

        Assert.DoesNotContain(colunas, c => c.Key == "Ausente");
    }

    [Fact]
    public void Template_fields_have_their_own_cap()
    {
        var campos = Enumerable.Range(0, 40).Select(i => ($"Campo{i}", (object)i)).ToArray();
        var template = string.Concat(campos.Select(c => $"{{{c.Item1}}}"));

        // Raros: só o primeiro de 100 eventos os carrega, então nenhum passa pela frequência.
        var eventos = Enumerable.Range(0, 100)
            .Select(i => i == 0 ? EventoComTemplate(template, campos) : Event(("Sempre", 1)))
            .ToArray();

        var colunas = LogColumnDiscovery.Discover(eventos, maxTemplateColumns: 5);

        Assert.Equal(5, colunas.Count(c => c.Source == ColumnSource.TemplateField));
    }

    [Theory]
    // Token simples, e com os prefixos de destructuring — o CLEF grava a propriedade sem eles.
    [InlineData("Pedido {OrderId} criado", "OrderId")]
    [InlineData("Pedido {@Order} criado", "Order")]
    [InlineData("Chave {$Key}", "Key")]
    // Alinhamento e formato não fazem parte do nome.
    [InlineData("Total {Valor:0.00}", "Valor")]
    [InlineData("Nome {Nome,-20}|", "Nome")]
    [InlineData("Data {Quando:dd/MM/yyyy HH:mm}", "Quando")]
    public void The_property_name_is_extracted_from_the_token(string template, string esperado)
    {
        Assert.Equal(new[] { esperado }, LogColumnDiscovery.CamposDoTemplate(template));
    }

    [Fact]
    public void Escaped_braces_are_not_tokens()
    {
        // "{{" e "}}" são chaves literais no template do Serilog.
        Assert.Equal(
            new[] { "Real" },
            LogColumnDiscovery.CamposDoTemplate("{{NaoEhToken}} mas {Real} sim"));
    }

    [Fact]
    public void Positional_tokens_are_ignored()
    {
        Assert.Empty(LogColumnDiscovery.CamposDoTemplate("Valores {0} e {1}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mensagem sem parâmetro")]
    public void A_template_without_tokens_yields_no_fields(string? template)
    {
        Assert.Empty(LogColumnDiscovery.CamposDoTemplate(template));
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

    // --- Tipo da coluna e ordenação ----------------------------------------------

    [Fact]
    public void A_property_holding_numbers_is_typed_as_number()
    {
        var colunas = LogColumnDiscovery.Discover(new[] { Event(("Ms", 78)), Event(("Ms", 591)) });

        Assert.Equal(ColumnValueKind.Number, colunas.Single(c => c.Key == "Ms").Kind);
    }

    [Fact]
    public void A_single_odd_value_downgrades_the_column_to_text()
    {
        // Ordenar compara os valores entre si: um número junto de uma string na mesma
        // coluna quebraria a comparação. Na dúvida, texto.
        var colunas = LogColumnDiscovery.Discover(new[] { Event(("Ms", 78)), Event(("Ms", "n/d")) });

        Assert.Equal(ColumnValueKind.Text, colunas.Single(c => c.Key == "Ms").Kind);
    }

    [Fact]
    public void Dates_and_booleans_are_recognized()
    {
        var colunas = LogColumnDiscovery.Discover(new[]
        {
            Event(("Quando", DateTimeOffset.UtcNow), ("Ativo", true)),
        });

        Assert.Equal(ColumnValueKind.Date, colunas.Single(c => c.Key == "Quando").Kind);
        Assert.Equal(ColumnValueKind.Boolean, colunas.Single(c => c.Key == "Ativo").Kind);
    }

    [Fact]
    public void Numbers_sort_as_numbers_and_not_as_text()
    {
        // Regressão: a coluna entregava o texto formatado também como chave de ordenação,
        // então "591" vinha antes de "78" — comparação de string.
        var eventos = new[] { Event(("Ms", 78)), Event(("Ms", 591)), Event(("Ms", 75)) };

        var ordenado = eventos
            .OrderBy(e => LogColumnDiscovery.GetSortValue(e, "Ms", ColumnValueKind.Number))
            .Select(e => LogColumnDiscovery.FormatValue(e, "Ms"))
            .ToArray();

        Assert.Equal(new[] { "75", "78", "591" }, ordenado);
    }

    [Fact]
    public void A_number_written_as_text_still_sorts_as_a_number()
    {
        Assert.Equal(123d, LogColumnDiscovery.GetSortValue(Event(("Ms", "123")), "Ms", ColumnValueKind.Number));
    }

    [Fact]
    public void A_missing_number_sorts_as_null_and_not_as_text()
    {
        // Devolver "" aqui misturaria string com double e derrubaria a comparação.
        Assert.Null(LogColumnDiscovery.GetSortValue(Event(("Outra", 1)), "Ms", ColumnValueKind.Number));
        Assert.Null(LogColumnDiscovery.GetSortValue(Event(("Ms", "abc")), "Ms", ColumnValueKind.Number));
    }

    [Fact]
    public void A_text_column_keeps_using_the_displayed_text()
    {
        Assert.Equal(
            "Api.Pedido",
            LogColumnDiscovery.GetSortValue(Event(("Ctx", "Api.Pedido")), "Ctx", ColumnValueKind.Text));
    }

    [Fact]
    public void The_two_ways_of_having_no_value_share_one_key()
    {
        // Num log real: 3.216 eventos sem a propriedade CorrelationId e 46 com ela valendo
        // null. Chaves diferentes davam DOIS grupos na tabela, ambos rotulados "(vazio)".
        var ausente = Event(("Outra", 1));
        var nula = new ClefEvent
        {
            Level = "Information",
            Properties = new Dictionary<string, LogEventPropertyValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["CorrelationId"] = new ScalarValue(null),
            },
        };

        var chaveAusente = LogColumnDiscovery.GetSortValue(ausente, "CorrelationId", ColumnValueKind.Text);
        var chaveNula = LogColumnDiscovery.GetSortValue(nula, "CorrelationId", ColumnValueKind.Text);

        Assert.Null(chaveAusente);
        Assert.Null(chaveNula);
        Assert.Equal(chaveAusente, chaveNula);
    }

    [Fact]
    public void An_empty_string_is_no_value_either()
    {
        // "" e ausente também precisam cair no mesmo grupo.
        Assert.Null(LogColumnDiscovery.GetSortValue(Event(("Ctx", "")), "Ctx", ColumnValueKind.Text));
    }

    [Fact]
    public void The_cell_still_shows_an_empty_string()
    {
        // A chave de agrupamento virou null, mas a CÉLULA continua vazia — e não "null".
        Assert.Equal(string.Empty, LogColumnDiscovery.FormatValue(Event(("Outra", 1)), "Ctx"));
    }

    [Fact]
    public void The_filter_matches_the_inferred_kind()
    {
        Assert.Equal(ColumnFilterType.Number, LogColumnDiscovery.FilterTypeFor(ColumnValueKind.Number));
        Assert.Equal(ColumnFilterType.Date, LogColumnDiscovery.FilterTypeFor(ColumnValueKind.Date));
        Assert.Equal(ColumnFilterType.Text, LogColumnDiscovery.FilterTypeFor(ColumnValueKind.Text));
    }
}
