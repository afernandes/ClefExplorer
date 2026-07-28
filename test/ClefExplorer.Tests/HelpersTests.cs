using ClefExplorer.Helpers;

namespace ClefExplorer.Tests;

/// <summary>
/// <see cref="TextFormatter"/>: desescapa o texto vindo do log e indenta o JSON embutido.
/// </summary>
public class TextFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_returns_empty(string? input)
    {
        Assert.Equal(string.Empty, TextFormatter.Format(input));
    }

    [Fact]
    public void Unescapes_newlines_tabs_and_quotes()
    {
        var result = TextFormatter.Format(@"linha1\r\nlinha2\tfim \""aspas\""");

        Assert.Equal("linha1\nlinha2\tfim \"aspas\"", result);
    }

    [Fact]
    public void Unescapes_unicode_sequences()
    {
        Assert.Equal("Notificação", TextFormatter.Format(@"Notifica\u00e7\u00e3o"));
    }

    [Fact]
    public void Leaves_invalid_unicode_sequences_untouched()
    {
        Assert.Equal(@"\uZZZZ", TextFormatter.Format(@"\uZZZZ"));
    }

    [Fact]
    public void Indents_embedded_json_objects()
    {
        var result = TextFormatter.Format("""Resposta: {"id":1,"nome":"teste"}""");

        Assert.Contains("Resposta: ", result);
        Assert.Contains("\n", result);              // foi indentado
        Assert.Contains("\"nome\": \"teste\"", result);
    }

    [Fact]
    public void Indents_embedded_json_arrays()
    {
        var result = TextFormatter.Format("""Itens: [{"a":1}]""");

        Assert.Contains("\n", result);
    }

    [Fact]
    public void Preserves_text_that_only_looks_like_json()
    {
        var result = TextFormatter.Format("um { que nunca fecha");

        Assert.Equal("um { que nunca fecha", result);
    }

    [Fact]
    public void Does_not_escape_accented_characters_in_json()
    {
        // UnsafeRelaxedJsonEscaping: acentos devem sair legíveis, não como \u00e7.
        var result = TextFormatter.Format("""{"msg":"Operação"}""");

        Assert.Contains("Operação", result);
        Assert.DoesNotContain("\\u00", result);
    }

    [Fact]
    public void Keeps_text_before_and_after_the_json()
    {
        var result = TextFormatter.Format("""antes {"a":1} depois""");

        Assert.StartsWith("antes ", result);
        Assert.EndsWith(" depois", result);
    }
}

/// <summary>
/// <see cref="StackTraceHighlighter"/>: gera HTML com as classes <c>clef-st-*</c>.
/// Como o resultado é injetado como <c>MarkupString</c>, o escape de HTML é a parte
/// crítica — um stack trace pode conter texto vindo de dados não confiáveis.
/// </summary>
public class StackTraceHighlighterTests
{
    private static string Html(string? input) => StackTraceHighlighter.Highlight(input).Value ?? string.Empty;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_returns_empty_markup(string? input)
    {
        Assert.Equal(string.Empty, Html(input));
    }

    [Fact]
    public void Highlights_the_method_of_a_stack_frame()
    {
        var html = Html("   at MinhaApp.Servico.Executar(String arg)");

        Assert.Contains("clef-st-method", html);
        Assert.Contains("Executar", html);
        Assert.Contains("MinhaApp.Servico.", html);
    }

    [Fact]
    public void Shows_only_the_file_name_and_keeps_the_full_path_in_the_title()
    {
        var html = Html(@"   at MinhaApp.Servico.Executar(String arg) in C:\src\MinhaApp\Servico.cs:line 42");

        Assert.Contains("clef-st-file", html);
        Assert.Contains("Servico.cs", html);
        Assert.Contains(@"title=""C:\src\MinhaApp\Servico.cs""", html);
        Assert.Contains("clef-st-line", html);
        Assert.Contains("42", html);
    }

    [Fact]
    public void Marks_framework_frames_so_they_can_be_de_emphasized()
    {
        var html = Html("   at System.Threading.Tasks.Task.Execute()");

        Assert.Contains("clef-st-system", html);
    }

    [Fact]
    public void Application_frames_are_not_marked_as_framework()
    {
        var html = Html("   at MinhaApp.Servico.Executar()");

        Assert.DoesNotContain("clef-st-system", html);
    }

    [Fact]
    public void Marks_the_inner_exception_separator()
    {
        var html = Html("--- End of inner exception stack trace ---");

        Assert.Contains("clef-st-sep", html);
    }

    [Fact]
    public void Escapes_html_coming_from_the_log_text()
    {
        var html = Html("Erro ao processar <script>alert('xss')</script>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Escapes_html_inside_method_arguments()
    {
        var html = Html("   at MinhaApp.Servico.Executar(String <b>arg</b>)");

        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;", html);
    }

    [Fact]
    public void Wraps_every_line_in_its_own_div()
    {
        var html = Html("primeira\nsegunda\nterceira");

        Assert.Equal(3, html.Split("<div").Length - 1);
    }
}
