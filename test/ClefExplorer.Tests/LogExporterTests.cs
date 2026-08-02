using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;
using System.Text.Json;

namespace ClefExplorer.Tests;

/// <summary>Contrato do <see cref="LogExporter"/> — CSV, CLEF e texto.</summary>
public class LogExporterTests
{
    private sealed class ProgressoCapturado : IProgress<int>
    {
        public int Valor { get; private set; }
        public void Report(int value) => Valor = value;
    }

    /// <summary>
    /// Guarda TODOS os avisos. É um <c>IProgress</c> cru (e não <c>Progress&lt;T&gt;</c>)
    /// de propósito: o Report é chamado na própria thread da gravação, então a contagem
    /// é determinística.
    /// </summary>
    private sealed class ProgressoContado : IProgress<int>
    {
        public List<int> Valores { get; } = new();
        public void Report(int value) => Valores.Add(value);
    }

    private static ClefEvent Event(
        string level = "Information",
        string? message = "mensagem",
        string? exception = null,
        string? template = null,
        string? sourceFile = null) => new()
        {
            Timestamp = new DateTimeOffset(2026, 6, 15, 12, 30, 45, 123, TimeSpan.Zero),
            Level = level,
            Message = message,
            MessageTemplate = template,
            Exception = exception,
            SourceFile = sourceFile,
        };

    // --- CSV --------------------------------------------------------------------

    [Fact]
    public void Csv_starts_with_a_header()
    {
        var csv = LogExporter.ToCsv(new[] { Event() });

        Assert.StartsWith("Timestamp,Level,Message,Exception,SourceFile", csv);
    }

    [Fact]
    public void Csv_writes_one_line_per_event()
    {
        var csv = LogExporter.ToCsv(new[] { Event(message: "um"), Event(message: "dois") });

        var linhas = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, linhas.Length); // cabeçalho + 2
    }

    [Fact]
    public void Csv_quotes_fields_containing_a_comma()
    {
        var csv = LogExporter.ToCsv(new[] { Event(message: "erro, com vírgula") });

        Assert.Contains("\"erro, com vírgula\"", csv);
    }

    [Fact]
    public void Csv_doubles_embedded_quotes()
    {
        var csv = LogExporter.ToCsv(new[] { Event(message: "disse \"olá\"") });

        Assert.Contains("\"disse \"\"olá\"\"\"", csv);
    }

    [Fact]
    public void Csv_quotes_fields_containing_newlines()
    {
        // Caso comum: stack traces multi-linha no campo Exception.
        var csv = LogExporter.ToCsv(new[] { Event(exception: "linha1\nlinha2") });

        Assert.Contains("\"linha1\nlinha2\"", csv);
    }

    [Fact]
    public void Csv_leaves_simple_fields_unquoted()
    {
        var csv = LogExporter.ToCsv(new[] { Event(message: "simples") });

        Assert.Contains(",simples,", csv);
    }

    [Fact]
    public void Csv_of_an_empty_set_has_only_the_header()
    {
        var csv = LogExporter.ToCsv(Array.Empty<ClefEvent>());

        Assert.Single(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("=2+2")]
    [InlineData("+SUM(A1:A2)")]
    [InlineData("-1+2")]
    [InlineData("@cmd")]
    [InlineData("  =2+2")]
    public void Csv_neutraliza_campos_interpretaveis_como_formula(string valor)
    {
        var csv = LogExporter.ToCsv(new[] { Event(message: valor) });

        Assert.Contains("'" + valor, csv);
    }

    // --- CLEF -------------------------------------------------------------------

    [Fact]
    public void Clef_writes_one_json_object_per_line()
    {
        var clef = LogExporter.ToClef(new[] { Event(message: "um"), Event(message: "dois") });

        var linhas = clef.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, linhas.Length);
        Assert.All(linhas, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public void Clef_always_writes_the_timestamp()
    {
        var clef = LogExporter.ToClef(new[] { Event() });

        Assert.Contains("\"@t\"", clef);
    }

    [Fact]
    public void Clef_omits_the_level_for_Information()
    {
        // No formato CLEF, Information é o nível padrão e "@l" é omitido.
        var clef = LogExporter.ToClef(new[] { Event("Information") });

        Assert.DoesNotContain("\"@l\"", clef);
    }

    [Fact]
    public void Clef_writes_the_level_for_other_levels()
    {
        var clef = LogExporter.ToClef(new[] { Event("Error") });

        Assert.Contains("\"@l\":\"Error\"", clef);
    }

    [Fact]
    public void Clef_prefers_the_message_template_over_the_rendered_message()
    {
        var clef = LogExporter.ToClef(new[] { Event(message: "Pedido 42 falhou", template: "Pedido {Id} falhou") });

        Assert.Contains("\"@mt\":\"Pedido {Id} falhou\"", clef);
        Assert.DoesNotContain("\"@m\"", clef);
    }

    [Fact]
    public void Clef_falls_back_to_the_rendered_message_without_a_template()
    {
        var clef = LogExporter.ToClef(new[] { Event(message: "sem template") });

        Assert.Contains("\"@m\":\"sem template\"", clef);
    }

    [Fact]
    public void Clef_includes_the_exception()
    {
        var clef = LogExporter.ToClef(new[] { Event(exception: "System.Exception: boom") });

        Assert.Contains("\"@x\"", clef);
    }

    [Fact]
    public void Clef_keeps_accented_text_readable()
    {
        var clef = LogExporter.ToClef(new[] { Event(message: "Operação não concluída") });

        Assert.Contains("Operação não concluída", clef);
        Assert.DoesNotContain("\\u00", clef);
    }

    [Fact]
    public void Clef_preserva_tipos_e_estruturas_das_propriedades()
    {
        var evento = Event(sourceFile: @"C:\logs\origem.clef");
        evento.Properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["Tentativas"] = new ScalarValue(3),
            ["Sucesso"] = new ScalarValue(true),
            ["Itens"] = new SequenceValue(new LogEventPropertyValue[]
            {
                new ScalarValue(10),
                new ScalarValue("dois"),
            }),
            ["Pedido"] = new StructureValue(new[]
            {
                new LogEventProperty("Id", new ScalarValue(42)),
            }, "Pedido"),
        };

        using var json = JsonDocument.Parse(LogExporter.ToClef(new[] { evento }).Trim());
        var root = json.RootElement;

        Assert.Equal(JsonValueKind.Number, root.GetProperty("Tentativas").ValueKind);
        Assert.Equal(3, root.GetProperty("Tentativas").GetInt32());
        Assert.True(root.GetProperty("Sucesso").GetBoolean());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("Itens").ValueKind);
        Assert.Equal(42, root.GetProperty("Pedido").GetProperty("Id").GetInt32());
        // "$type" é o nome que o CLEF reserva para o tipo da estrutura; com o "_typeTag"
        // padrão do JsonValueFormatter o tipo voltava a ser lido como um campo comum.
        Assert.Equal("Pedido", root.GetProperty("Pedido").GetProperty("$type").GetString());
        Assert.False(root.TryGetProperty("SourceFile", out _));
    }

    [Fact]
    public async Task Exportacao_para_arquivo_grava_sem_montar_um_conteudo_global()
    {
        var destino = Path.Combine(Path.GetTempPath(), $"clef-export-{Guid.NewGuid():N}.clef");
        try
        {
            var eventos = new[] { Event("Error", "um"), Event(message: "dois") };

            var progresso = new ProgressoCapturado();
            await LogExporter.ExportToFileAsync(
                eventos,
                destino,
                ExportFormat.Clef,
                progress: progresso);

            Assert.Equal(LogExporter.ToClef(eventos), await File.ReadAllTextAsync(destino));
            Assert.Equal(eventos.Length, progresso.Valor);
        }
        finally
        {
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    [Fact]
    public async Task Clef_exportado_pode_ser_reaberto_com_tipos_preservados()
    {
        var destino = Path.Combine(Path.GetTempPath(), $"clef-roundtrip-{Guid.NewGuid():N}.clef");
        var evento = Event(template: "Pedido {Id}");
        evento.Properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["Id"] = new ScalarValue(42),
            ["Tags"] = new SequenceValue(new LogEventPropertyValue[]
            {
                new ScalarValue("urgente"),
                new ScalarValue("api"),
            }),
        };

        try
        {
            await LogExporter.ExportToFileAsync(new[] { evento }, destino, ExportFormat.Clef);
            var leitura = await new LeitorArquivoLog().LerAsync(destino, Array.Empty<string>());
            var reaberto = Assert.Single(leitura.Eventos);

            Assert.Equal(42L, Convert.ToInt64(Assert.IsType<ScalarValue>(reaberto.Properties!["Id"]).Value));
            Assert.Equal(2, Assert.IsType<SequenceValue>(reaberto.Properties["Tags"]).Elements.Count);
        }
        finally
        {
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    // --- CLEF: ida e volta -------------------------------------------------------

    /// <summary>
    /// Abre a linha num arquivo, exporta o evento lido para .clef e reabre o resultado —
    /// o caminho que o usuário percorre ao salvar o conjunto filtrado e carregá-lo de novo.
    /// </summary>
    private static async Task<(ClefEvent Original, ClefEvent Reaberto, string LinhaExportada)> IdaEVolta(
        string linhaOriginal)
    {
        var origem = Path.Combine(Path.GetTempPath(), $"clef-ida-{Guid.NewGuid():N}.clef");
        var destino = Path.Combine(Path.GetTempPath(), $"clef-volta-{Guid.NewGuid():N}.clef");

        try
        {
            await File.WriteAllTextAsync(origem, linhaOriginal + Environment.NewLine);
            var leitor = new LeitorArquivoLog();
            var original = Assert.Single((await leitor.LerAsync(origem, Array.Empty<string>())).Eventos);

            await LogExporter.ExportToFileAsync(new[] { original }, destino, ExportFormat.Clef);

            var reaberto = Assert.Single((await leitor.LerAsync(destino, Array.Empty<string>())).Eventos);
            return (original, reaberto, (await File.ReadAllTextAsync(destino)).Trim());
        }
        finally
        {
            if (File.Exists(origem)) File.Delete(origem);
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    [Fact]
    public async Task Clef_preserva_a_mensagem_de_evento_com_token_formatado()
    {
        // Caso real: em 314.973 eventos de produção, 38 mudavam de mensagem ao exportar e
        // reabrir. Sem o @r regravado, {Now:O} volta a ser lido como texto cru e o Serilog
        // renderiza a string ENTRE ASPAS.
        const string linha = """
            {"@t":"2026-07-30T18:36:43.9078632Z","@mt":"[Heartbeat] '{Name}' atualizado em {Now:O}.","@r":["2026-07-30T18:36:43.9078632Z"],"Name":"AVELL-AFN","Now":"2026-07-30T18:36:43.9078632Z"}
            """;

        var (original, reaberto, _) = await IdaEVolta(linha);

        Assert.Equal(
            "[Heartbeat] '\"AVELL-AFN\"' atualizado em 2026-07-30T18:36:43.9078632Z.",
            original.Message);
        Assert.Equal(original.Message, reaberto.Message);
    }

    [Fact]
    public async Task Clef_regrava_o_texto_original_do_r_em_vez_de_reformatar_o_valor()
    {
        // O @r guarda o texto que o PRODUTOR do log renderizou — "R$ 10,50" saiu de uma
        // máquina pt-BR. Recalcular o formato "C" na exportação devolveria o símbolo da
        // cultura invariante e mudaria a mensagem.
        const string linha = """
            {"@t":"2026-06-15T12:30:45.1230000Z","@mt":"Total {Preco:C}","@r":["R$ 10,50"],"Preco":10.5}
            """;

        var (original, reaberto, exportada) = await IdaEVolta(linha);

        Assert.Equal("Total R$ 10,50", original.Message);
        Assert.Equal(original.Message, reaberto.Message);

        using var json = JsonDocument.Parse(exportada);
        Assert.Equal("R$ 10,50", json.RootElement.GetProperty("@r")[0].GetString());
    }

    [Fact]
    public async Task Clef_grava_um_r_por_token_formatado_na_ordem_do_template()
    {
        // Alinhamento sem formato ({Item,8}) fica FORA do @r: o pareamento na leitura é
        // posicional entre os tokens que têm formato e os elementos do array.
        const string linha = """
            {"@t":"2026-06-15T12:30:45.0000000Z","@mt":"{Quando:O} {Item,8} {Tentativas:000}","@r":["2026-06-15T12:30:45.0000000Z","007"],"Quando":"2026-06-15T12:30:45.0000000Z","Item":"caixa","Tentativas":7}
            """;

        var (original, reaberto, exportada) = await IdaEVolta(linha);

        using var json = JsonDocument.Parse(exportada);
        var renderizacoes = json.RootElement.GetProperty("@r");
        Assert.Equal(2, renderizacoes.GetArrayLength());
        Assert.Equal("2026-06-15T12:30:45.0000000Z", renderizacoes[0].GetString());
        Assert.Equal("007", renderizacoes[1].GetString());
        Assert.Equal(original.Message, reaberto.Message);
    }

    [Fact]
    public async Task Clef_reaberto_preserva_nivel_timestamp_excecao_e_propriedades()
    {
        const string linha = """
            {"@t":"2026-06-15T12:30:45.1234567+03:00","@l":"Error","@mt":"Falha em {Etapa} às {Quando:O} após {Tentativas:000}","@r":["2026-06-15T09:30:45.1234567Z","007"],"@x":"System.Exception: boom\n   em Origem()","Etapa":"envio","Quando":"2026-06-15T09:30:45.1234567Z","Tentativas":7,"Ativo":true,"Itens":[1,"dois"]}
            """;

        var (original, reaberto, _) = await IdaEVolta(linha);

        Assert.Equal(original.Message, reaberto.Message);
        Assert.Equal(original.Level, reaberto.Level);
        Assert.Equal(original.Timestamp, reaberto.Timestamp);
        Assert.Equal(original.Exception, reaberto.Exception);
        Assert.Equal(original.MessageTemplate, reaberto.MessageTemplate);
        Assert.Equal(original.Properties!.Count, reaberto.Properties!.Count);
        foreach (var (nome, valor) in original.Properties)
        {
            // ToString() do LogEventPropertyValue compara valor E tipo: 7 (número) e "7"
            // (texto) não dão o mesmo texto.
            Assert.Equal(valor.ToString(), reaberto.Properties[nome].ToString());
        }
    }

    [Fact]
    public async Task Clef_grava_r_mesmo_com_chave_literal_antes_do_token()
    {
        // A exportação só compila o template quando ele PODE ter token formatado, e "{{" é
        // chave literal: parar nela deixaria o {Preco:C} seguinte sem @r.
        const string linha = """
            {"@t":"2026-06-15T12:30:45.0000000Z","@mt":"{{total}} {Preco:C}","@r":["R$ 10,50"],"Preco":10.5}
            """;

        var (original, reaberto, exportada) = await IdaEVolta(linha);

        Assert.Equal("{total} R$ 10,50", original.Message);
        Assert.Equal(original.Message, reaberto.Message);

        using var json = JsonDocument.Parse(exportada);
        Assert.Equal("R$ 10,50", json.RootElement.GetProperty("@r")[0].GetString());
    }

    [Fact]
    public async Task Clef_nao_grava_r_para_evento_que_veio_de_m()
    {
        // Evento sem @mt: a leitura usa a MENSAGEM inteira escapada como template. Ela não tem
        // token nenhum — os dois-pontos do texto não podem ser confundidos com formato.
        const string linha = """
            {"@t":"2026-06-15T12:30:45.0000000Z","@m":"20:36:59.667: MostrarMensagem: CONECTANDO PINPAD"}
            """;

        var (original, reaberto, exportada) = await IdaEVolta(linha);

        Assert.Equal("20:36:59.667: MostrarMensagem: CONECTANDO PINPAD", original.Message);
        Assert.Equal(original.Message, reaberto.Message);
        Assert.DoesNotContain("\"@r\"", exportada);
    }

    [Fact]
    public async Task Clef_reaberto_preserva_o_tipo_de_uma_estrutura()
    {
        // Todo evento dos logs reais tem uma estrutura com tipo (o enricher de máquina). Com o
        // "_typeTag" padrão do JsonValueFormatter, reabrir o export perdia o tipo e ganhava um
        // campo "_typeTag" dentro da estrutura.
        const string linha = """
            {"@t":"2026-06-15T12:30:45.0000000Z","@mt":"Pedido {@Pedido}","Pedido":{"Id":42,"$type":"Pedido"}}
            """;

        var (original, reaberto, _) = await IdaEVolta(linha);

        var estrutura = Assert.IsType<StructureValue>(reaberto.Properties!["Pedido"]);
        Assert.Equal("Pedido", estrutura.TypeTag);
        Assert.Equal(original.Properties!["Pedido"].ToString(), estrutura.ToString());
        Assert.Equal(original.Message, reaberto.Message);
    }

    [Fact]
    public async Task Clef_reaberto_preserva_propriedades_com_arroba_no_nome()
    {
        // O @i (id do evento) chega à leitura como uma propriedade chamada "@i". Descartá-la
        // na exportação perdia o id; o formato manda DOBRAR o '@', e é isso que o leitor desfaz.
        const string linha = """
            {"@t":"2026-06-15T12:30:45.0000000Z","@mt":"olá","@i":"3f9a1c","@@meu":7}
            """;

        var (original, reaberto, _) = await IdaEVolta(linha);

        Assert.Equal(original.Properties!.Count, reaberto.Properties!.Count);
        Assert.Equal("\"3f9a1c\"", reaberto.Properties["@i"].ToString());
        Assert.Equal("7", reaberto.Properties["@meu"].ToString());
    }

    [Fact]
    public void Clef_nao_grava_r_para_template_sem_token_formatado()
    {
        var evento = Event(message: "Pedido 42 falhou", template: "Pedido {Id} falhou");
        evento.Properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["Id"] = new ScalarValue(42),
        };

        Assert.DoesNotContain("\"@r\"", LogExporter.ToClef(new[] { evento }));
    }

    [Fact]
    public void Clef_com_token_formatado_e_sem_propriedades_grava_o_texto_cru_do_token()
    {
        // Sem a propriedade, o Serilog escreve o próprio token — é o que o formatador
        // oficial produz. O que não pode acontecer é a exportação estourar.
        var evento = Event(message: "Agora ?", template: "Agora {Now:O}");

        using var json = JsonDocument.Parse(LogExporter.ToClef(new[] { evento }).Trim());

        Assert.Equal("{Now:O}", json.RootElement.GetProperty("@r")[0].GetString());
    }

    // --- Ritmo do progresso ------------------------------------------------------

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Clef)]
    [InlineData(ExportFormat.Text)]
    public async Task Progress_is_throttled_instead_of_one_report_per_event(ExportFormat formato)
    {
        // Era um Report POR EVENTO, e cada um virava um StateHasChanged na janela:
        // exportar 200 mil eventos gastava 3.839 ms só despachando callbacks no message
        // pump (13,3 s com 1 milhão), com a UI congelada o tempo todo. O ritmo agora é de
        // 1% do total (ou 250 ms), e gravar as mesmas linhas custa o mesmo.
        var eventos = Enumerable.Range(0, 5_000).Select(i => Event(message: $"linha {i}")).ToArray();
        var destino = Path.Combine(Path.GetTempPath(), $"clef-progress-{Guid.NewGuid():N}{LogExporter.Extension(formato)}");
        var progresso = new ProgressoContado();

        try
        {
            await LogExporter.ExportToFileAsync(eventos, destino, formato, progress: progresso);

            // 1% de 5.000 = um aviso a cada 50 eventos; a régua de tempo pode acrescentar
            // alguns num disco lento, mas nunca chega perto de um por evento.
            Assert.InRange(progresso.Valores.Count, 1, 150);
        }
        finally
        {
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    [Theory]
    [InlineData(ExportFormat.Csv)]
    [InlineData(ExportFormat.Clef)]
    [InlineData(ExportFormat.Text)]
    public async Task The_last_progress_report_is_the_final_total(ExportFormat formato)
    {
        // 251 não é múltiplo do passo (251/100 = 2): o laço termina ENTRE dois avisos, e
        // sem o aviso de conclusão a barra ficaria parada em 250 com o arquivo já gravado.
        var eventos = Enumerable.Range(0, 251).Select(i => Event(message: $"linha {i}")).ToArray();
        var destino = Path.Combine(Path.GetTempPath(), $"clef-progress-{Guid.NewGuid():N}{LogExporter.Extension(formato)}");
        var progresso = new ProgressoContado();

        try
        {
            await LogExporter.ExportToFileAsync(eventos, destino, formato, progress: progresso);

            Assert.Equal(251, progresso.Valores[^1]);
            // Nenhum aviso pode retroceder nem repetir: a barra andaria para trás.
            Assert.Equal(progresso.Valores.OrderBy(v => v).Distinct(), progresso.Valores);
        }
        finally
        {
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    [Fact]
    public async Task An_unknown_total_still_reports_the_final_count()
    {
        // TryGetNonEnumeratedCount falha num iterador preguiçoso — e contar antes de
        // gravar consumiria a sequência. Sem total não há como fatiar em 1%, sobra a régua
        // de tempo; ainda assim o aviso final tem de sair com o número exato.
        static IEnumerable<ClefEvent> Preguicoso()
        {
            for (var i = 0; i < 300; i++) yield return Event(message: $"linha {i}");
        }

        var destino = Path.Combine(Path.GetTempPath(), $"clef-progress-{Guid.NewGuid():N}.csv");
        var progresso = new ProgressoContado();

        try
        {
            await LogExporter.ExportToFileAsync(Preguicoso(), destino, ExportFormat.Csv, progress: progresso);

            Assert.Equal(300, progresso.Valores[^1]);
        }
        finally
        {
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    [Fact]
    public async Task Cancelamento_preserva_um_destino_existente()
    {
        var destino = Path.Combine(Path.GetTempPath(), $"clef-export-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(destino, "conteudo anterior");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                LogExporter.ExportToFileAsync(new[] { Event() }, destino, ExportFormat.Csv, cts.Token));

            Assert.Equal("conteudo anterior", await File.ReadAllTextAsync(destino));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(destino)!,
                $".{Path.GetFileName(destino)}.*.tmp"));
        }
        finally
        {
            if (File.Exists(destino)) File.Delete(destino);
        }
    }

    // --- Texto ------------------------------------------------------------------

    [Fact]
    public void Text_uses_a_readable_line_per_event()
    {
        var texto = LogExporter.ToText(new[] { Event("Error", "algo falhou") });

        Assert.Contains("[2026-06-15 12:30:45.123] Error: algo falhou", texto);
    }

    [Fact]
    public void Text_appends_the_exception_below_the_message()
    {
        var texto = LogExporter.ToText(new[] { Event("Error", "falhou", exception: "System.Exception: boom") });

        Assert.Contains("System.Exception: boom", texto);
    }

    // --- Seleção de formato ------------------------------------------------------

    [Theory]
    [InlineData(@"C:\tmp\saida.csv", ExportFormat.Csv)]
    [InlineData(@"C:\tmp\saida.CSV", ExportFormat.Csv)]
    [InlineData(@"C:\tmp\saida.clef", ExportFormat.Clef)]
    [InlineData(@"C:\tmp\saida.txt", ExportFormat.Text)]
    [InlineData(@"C:\tmp\saida.qualquer", ExportFormat.Text)]
    public void Format_is_derived_from_the_chosen_extension(string path, ExportFormat expected)
    {
        Assert.Equal(expected, LogExporter.FormatFromPath(path));
    }

    [Fact]
    public void Export_dispatches_to_the_right_serializer()
    {
        var eventos = new[] { Event("Error", "x") };

        Assert.Equal(LogExporter.ToCsv(eventos), LogExporter.Export(eventos, ExportFormat.Csv));
        Assert.Equal(LogExporter.ToClef(eventos), LogExporter.Export(eventos, ExportFormat.Clef));
        Assert.Equal(LogExporter.ToText(eventos), LogExporter.Export(eventos, ExportFormat.Text));
    }
}
