using System.Text.RegularExpressions;
using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

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
        DateTimeOffset? timestamp = null,
        Dictionary<string, LogEventPropertyValue>? properties = null) => new()
        {
            Level = level,
            Message = message,
            Exception = exception,
            SourceFile = sourceFile,
            Timestamp = timestamp ?? new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
            Properties = properties,
        };

    private static Dictionary<string, LogEventPropertyValue> Props(
        params (string Chave, LogEventPropertyValue Valor)[] pares)
    {
        var dicionario = new Dictionary<string, LogEventPropertyValue>(
            pares.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (chave, valor) in pares) dicionario[chave] = valor;
        return dicionario;
    }

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
    public void Uma_regex_patologica_e_interrompida_por_timeout()
    {
        var texto = new string('a', 100_000) + "!";
        var eventos = new[] { Event(message: texto) };

        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() =>
            LogFilter.Apply(eventos, Criteria(search: "^(a+)+$", useRegex: true)));
    }

    [Fact]
    public void O_cancelamento_interrompe_a_varredura()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LogFilter.Apply(new[] { Event(message: "evento") }, Criteria(), cts.Token));
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

    // --- Filtro por data/hora ---------------------------------------------------

    [Fact]
    public void Period_is_inclusive_on_both_ends()
    {
        var eventos = new[]
        {
            Event(timestamp: new DateTimeOffset(2026, 6, 14, 23, 59, 0, TimeSpan.Zero)),
            Event(timestamp: new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)), // exatamente no início
            Event(timestamp: new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero)),
            Event(timestamp: new DateTimeOffset(2026, 6, 16, 23, 59, 0, TimeSpan.Zero)), // exatamente no fim
            Event(timestamp: new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero)),
        };

        var result = LogFilter.Apply(eventos, Criteria(
            from: new DateTime(2026, 6, 15, 0, 0, 0),
            to: new DateTime(2026, 6, 16, 23, 59, 0)));

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void From_takes_the_time_into_account_within_the_same_day()
    {
        var eventos = new[]
        {
            Event(message: "antes", timestamp: new DateTimeOffset(2026, 6, 15, 8, 29, 59, TimeSpan.Zero)),
            Event(message: "no limite", timestamp: new DateTimeOffset(2026, 6, 15, 8, 30, 0, TimeSpan.Zero)),
            Event(message: "depois", timestamp: new DateTimeOffset(2026, 6, 15, 8, 30, 1, TimeSpan.Zero)),
        };

        var result = LogFilter.Apply(eventos, Criteria(from: new DateTime(2026, 6, 15, 8, 30, 0)));

        Assert.Equal(new[] { "depois", "no limite" }, result.Select(e => e.Message));
    }

    [Fact]
    public void To_takes_the_time_into_account_within_the_same_day()
    {
        var eventos = new[]
        {
            Event(message: "antes", timestamp: new DateTimeOffset(2026, 6, 15, 17, 59, 59, TimeSpan.Zero)),
            Event(message: "no limite", timestamp: new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero)),
            Event(message: "depois", timestamp: new DateTimeOffset(2026, 6, 15, 18, 0, 1, TimeSpan.Zero)),
        };

        var result = LogFilter.Apply(eventos, Criteria(to: new DateTime(2026, 6, 15, 18, 0, 0)));

        Assert.Equal(new[] { "no limite", "antes" }, result.Select(e => e.Message));
    }

    [Fact]
    public void Period_compares_the_time_shown_on_screen_not_the_UTC_instant()
    {
        // A grade e o detalhe formatam o DateTimeOffset como ele foi gravado, sem converter
        // fuso. Filtrar pelo instante UTC descartaria um evento cujo horário na tela está
        // dentro do período pedido — o usuário veria a linha sumir sem explicação.
        var eventos = new[]
        {
            Event(timestamp: new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3))),
        };

        var result = LogFilter.Apply(eventos, Criteria(
            from: new DateTime(2026, 6, 15, 9, 0, 0),
            to: new DateTime(2026, 6, 15, 11, 0, 0)));

        Assert.Single(result);
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

    // --- Busca dentro das propriedades ------------------------------------------
    //
    // A busca deixou de materializar `propriedade.ToString()` para cada valor de cada
    // evento (299 MB alocados por tecla em 200 mil eventos): valores string agora são
    // comparados direto e o resto é renderizado num buffer reciclado. Os testes abaixo
    // travam o que essa otimização pode quebrar sem ninguém perceber.

    [Fact]
    public void Search_matches_a_term_that_only_exists_in_a_string_property()
    {
        var eventos = new[]
        {
            Event(message: "processado", properties: Props(("Area", new ScalarValue("VAREJO")))),
            Event(message: "processado", properties: Props(("Area", new ScalarValue("ATACADO")))),
        };

        Assert.Single(LogFilter.Apply(eventos, Criteria(search: "varejo")));
    }

    [Theory]
    [InlineData("42")]      // ScalarValue de número
    [InlineData("True")]    // ScalarValue de booleano
    [InlineData("AVELL")]   // string dentro de StructureValue
    [InlineData("ProcessorCount")] // nome do campo de dentro da estrutura
    [InlineData("beta")]    // item de SequenceValue
    public void Search_still_reaches_values_that_are_not_plain_strings(string termo)
    {
        // Esses são exatamente os casos que o atalho "se for string, compara direto"
        // poderia deixar para trás: se ele passasse a valer para tudo, o termo sumiria.
        var alvo = Event(message: "sem pista no texto", properties: Props(
            ("Tentativas", new ScalarValue(42)),
            ("Reprocessado", new ScalarValue(true)),
            ("Tags", new SequenceValue(new[] { new ScalarValue("alfa"), new ScalarValue("beta") })),
            ("DeviceInfo", new StructureValue(
                new[]
                {
                    new LogEventProperty("MachineName", new ScalarValue("AVELL-AFN")),
                    new LogEventProperty("ProcessorCount", new ScalarValue(20)),
                },
                "LogDeviceDataEnricherModel"))));

        var eventos = new[] { alvo, Event(message: "outro evento") };

        var result = LogFilter.Apply(eventos, Criteria(search: termo));

        Assert.Single(result);
        Assert.Same(alvo, result[0]);
    }

    [Fact]
    public void A_search_term_with_quotes_keeps_matching_the_serilog_rendering()
    {
        // DECISÃO REGISTRADA: o Serilog renderiza um valor string entre aspas
        // ("RacClient", não RacClient), e a busca sempre correu sobre esse texto
        // renderizado. Trocar por comparação direta com a string subjacente mudaria o
        // resultado justamente de quem digita as aspas, então um termo com aspas (ou com
        // barra invertida, o outro caractere que a renderização introduz) continua indo
        // pelo caminho antigo. O comportamento é o mesmo de antes da otimização.
        var comPropriedade = Event(message: "sem pista no texto",
            properties: Props(("ProviderKey", new ScalarValue("RacClient"))));
        var comMensagem = Event(message: "provedor RacClient sem aspas na mensagem");

        var eventos = new[] { comPropriedade, comMensagem };

        // As aspas existem no texto renderizado da propriedade...
        var comAspas = LogFilter.Apply(eventos, Criteria(search: "\"RacClient\""));
        Assert.Single(comAspas);
        Assert.Same(comPropriedade, comAspas[0]);

        // ...e não existem na mensagem, que nunca foi renderizada.
        Assert.Equal(2, LogFilter.Apply(eventos, Criteria(search: "RacClient")).Count);
    }

    [Fact]
    public void A_search_term_with_a_backslash_still_matches_the_property_value()
    {
        // A renderização do Serilog escapa aspas, mas NÃO escapa barra invertida — então
        // procurar por um caminho do Windows dentro de uma propriedade continua casando.
        var alvo = Event(message: "gravando", properties: Props(
            ("Arquivo", new ScalarValue(@"C:\TOTVSPDV\Logs\omnishop.clef"))));

        var eventos = new[] { alvo, Event(message: "gravando") };

        var result = LogFilter.Apply(eventos, Criteria(search: @"C:\TOTVSPDV"));

        Assert.Single(result);
        Assert.Same(alvo, result[0]);
    }

    [Fact]
    public void Regex_search_also_covers_the_property_values()
    {
        var alvo = Event(message: "sem pista no texto", properties: Props(
            ("Erro", new ScalarValue("System.TimeoutException"))));

        var eventos = new[] { alvo, Event(message: "tudo certo") };

        var result = LogFilter.Apply(eventos, Criteria(search: @"\w+Exception", useRegex: true));

        Assert.Single(result);
        Assert.Same(alvo, result[0]);
    }

    [Fact]
    public void A_regex_that_matches_empty_text_still_finds_an_event_without_message()
    {
        // O predicado antigo recebia string.Empty no lugar de uma mensagem nula; trocar
        // isso por "pula o campo" faria "^$" deixar de casar eventos sem mensagem.
        var eventos = new[] { new ClefEvent { Level = "Information" } };

        Assert.Single(LogFilter.Apply(eventos, Criteria(search: "^$", useRegex: true)));
    }

    // --- Varredura paralela -----------------------------------------------------

    /// <summary>
    /// Acima de alguns milhares de eventos a filtragem é dividida em partições que
    /// rodam em paralelo. O corpus precisa ser folgadamente maior que esse limite,
    /// senão os testes desta seção passam a exercitar só o caminho sequencial.
    /// </summary>
    private static ClefEvent[] CorpusGrande(int quantidade = 10_000)
    {
        var eventos = new ClefEvent[quantidade];
        var inicio = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < quantidade; i++)
        {
            eventos[i] = new ClefEvent
            {
                // Decrescente, como o LogStore publica.
                Timestamp = inicio.AddSeconds(-i),
                Level = LogFilter.AllLevels[i % LogFilter.AllLevels.Length],
                Message = $"evento {i} processado pelo servico",
                Exception = i % 50 == 0 ? "System.TimeoutException: tempo esgotado" : null,
                SourceFile = i % 2 == 0 ? @"C:\logs\a.clef" : @"C:\logs\b.clef",
                Properties = Props(
                    ("Area", new ScalarValue(i % 3 == 0 ? "VAREJO" : "ATACADO")),
                    ("Tentativas", new ScalarValue(i % 7)),
                    ("DeviceInfo", new StructureValue(
                        new[]
                        {
                            new LogEventProperty("MachineName", new ScalarValue("AVELL-AFN")),
                            new LogEventProperty("ProcessorCount", new ScalarValue(20)),
                        },
                        "LogDeviceDataEnricherModel"))),
            };
        }

        return eventos;
    }

    /// <summary>
    /// Esconde a lista por trás de um iterador: sem acesso por índice, a filtragem só
    /// pode seguir pelo caminho sequencial. É o comparativo dos testes abaixo.
    /// </summary>
    private static IEnumerable<ClefEvent> ApenasEnumeravel(IEnumerable<ClefEvent> eventos)
    {
        foreach (var evento in eventos) yield return evento;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("VAREJO")]           // casa um terço, só dentro de propriedade string
    [InlineData("evento 7777 ")]     // casa um único evento, na mensagem
    [InlineData("AVELL")]            // casa todos, dentro da estrutura aninhada
    [InlineData("\"VAREJO\"")]       // com aspas: caminho fiel à renderização
    [InlineData("zzqx-inexistente")] // não casa nada
    public void The_parallel_scan_returns_the_very_same_events_in_the_very_same_order(string? termo)
    {
        // A paralelização escreve numa máscara e recolhe em ordem depois; qualquer troca
        // por um acumulador concorrente embaralharia o resultado sem quebrar contagem
        // nenhuma. Por isso a comparação é por referência, posição a posição.
        var eventos = CorpusGrande();
        var criterios = new LogFilterCriteria
        {
            Levels = Levels("Error", "Warning", "Information"),
            Search = termo,
            InputAlreadySorted = true,
        };

        var paralelo = LogFilter.Apply(eventos, criterios);
        var sequencial = LogFilter.Apply(ApenasEnumeravel(eventos), criterios);

        Assert.Equal(sequencial.Count, paralelo.Count);
        for (var i = 0; i < sequencial.Count; i++)
        {
            Assert.Same(sequencial[i], paralelo[i]);
        }
    }

    [Fact]
    public void The_parallel_scan_also_sorts_when_the_input_is_not_already_sorted()
    {
        var eventos = CorpusGrande();
        Array.Reverse(eventos); // entra do mais antigo para o mais recente

        var result = LogFilter.Apply(eventos, Criteria());

        Assert.Equal(eventos.Length, result.Count);
        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].Timestamp >= result[i].Timestamp);
        }
    }

    [Fact]
    public void O_cancelamento_interrompe_tambem_a_varredura_paralela()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            LogFilter.Apply(CorpusGrande(), Criteria(search: "VAREJO"), cts.Token));
    }

    [Fact]
    public void Uma_regex_patologica_chega_como_timeout_e_nao_como_AggregateException()
    {
        // O LogViewer escolhe o aviso "expressão regular muito complexa" olhando
        // ex.InnerException; se o AggregateException do Parallel vazasse, o usuário
        // passaria a ver um erro genérico no lugar do aviso.
        var eventos = CorpusGrande();
        eventos[7_777].Message = new string('a', 100_000) + "!";

        Assert.Throws<RegexMatchTimeoutException>(() =>
            LogFilter.Apply(eventos, Criteria(search: "^(a+)+$", useRegex: true)));
    }

    [Fact]
    public void An_invalid_regex_yields_no_results_on_a_large_set_too()
    {
        Assert.Empty(LogFilter.Apply(CorpusGrande(), Criteria(search: "(sem fechar", useRegex: true)));
    }
}
