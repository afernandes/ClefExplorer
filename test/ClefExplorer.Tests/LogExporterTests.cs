using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>Contrato do <see cref="LogExporter"/> — CSV, CLEF e texto.</summary>
public class LogExporterTests
{
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
