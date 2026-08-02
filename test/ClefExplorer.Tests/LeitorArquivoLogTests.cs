using System.IO.Compression;
using System.Text;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato de ARQUIVO do <see cref="LeitorArquivoLog"/>: recorte das linhas nos bytes, BOM,
/// CRLF, arquivo sem quebra final, <c>.gz</c> e isolamento de linha corrompida.
///
/// <para>Estes casos existem porque a leitura deixou de passar por <c>StreamReader</c> (que
/// resolvia BOM e quebra de linha sozinho) e passou a recortar bytes num buffer do
/// <c>ArrayPool</c> — cada uma dessas conveniências virou código nosso.</para>
/// </summary>
public class LeitorArquivoLogTests : IDisposable
{
    private readonly string _pasta;

    public LeitorArquivoLogTests()
    {
        _pasta = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pasta);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pasta, recursive: true); } catch { /* limpeza best-effort */ }
    }

    private static string Linha(string mensagem, string instante = "2026-06-15T12:00:00.0000000Z") =>
        $@"{{""@t"":""{instante}"",""@mt"":""{mensagem}""}}";

    private string Escrever(string nome, string conteudo, bool bom = false)
    {
        var caminho = Path.Combine(_pasta, nome);
        var bytes = Encoding.UTF8.GetBytes(conteudo);
        if (bom) bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
        File.WriteAllBytes(caminho, bytes);
        return caminho;
    }

    private static Task<ResultadoLeituraArquivoLog> Ler(string arquivo, PoolDeTextos? pool = null) =>
        new LeitorArquivoLog().LerAsync(arquivo, Array.Empty<string>(), pool);

    [Fact]
    public async Task Lines_separated_by_lf_are_all_read()
    {
        var arquivo = Escrever("lf.clef", Linha("a") + "\n" + Linha("b") + "\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(new[] { "a", "b" }, leitura.Eventos.Select(e => e.Message));
        Assert.Equal(0, leitura.LinhasInvalidas);
    }

    [Fact]
    public async Task Lines_separated_by_crlf_do_not_keep_the_carriage_return()
    {
        // O \r ficaria colado no '}' final e derrubaria TODAS as linhas do arquivo.
        var arquivo = Escrever("crlf.clef", Linha("a") + "\r\n" + Linha("b") + "\r\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(new[] { "a", "b" }, leitura.Eventos.Select(e => e.Message));
        Assert.Equal(0, leitura.LinhasInvalidas);
    }

    [Fact]
    public async Task The_last_line_is_read_even_without_a_final_line_break()
    {
        var arquivo = Escrever("sem-quebra.clef", Linha("a") + "\n" + Linha("ultima"));

        var leitura = await Ler(arquivo);

        Assert.Equal(new[] { "a", "ultima" }, leitura.Eventos.Select(e => e.Message));
    }

    [Fact]
    public async Task The_last_line_is_read_when_it_ends_with_a_lone_carriage_return()
    {
        var arquivo = Escrever("cr-final.clef", Linha("a") + "\r\n" + Linha("ultima") + "\r");

        var leitura = await Ler(arquivo);

        Assert.Equal(new[] { "a", "ultima" }, leitura.Eventos.Select(e => e.Message));
        Assert.Equal(0, leitura.LinhasInvalidas);
    }

    [Fact]
    public async Task A_utf8_bom_does_not_invalidate_the_first_line()
    {
        // O StreamReader descartava o BOM sozinho; lendo bytes crus ele fica colado no '{'.
        var arquivo = Escrever("bom.clef", Linha("primeira") + "\n" + Linha("segunda") + "\n", bom: true);

        var leitura = await Ler(arquivo);

        Assert.Equal(new[] { "primeira", "segunda" }, leitura.Eventos.Select(e => e.Message));
        Assert.Equal(0, leitura.LinhasInvalidas);
    }

    [Fact]
    public async Task A_file_with_only_a_bom_yields_nothing_and_no_failure()
    {
        var arquivo = Escrever("so-bom.clef", string.Empty, bom: true);

        var leitura = await Ler(arquivo);

        Assert.Empty(leitura.Eventos);
        Assert.Equal(0, leitura.LinhasInvalidas);
    }

    [Fact]
    public async Task Blank_lines_are_skipped_without_counting_as_failures()
    {
        var arquivo = Escrever("brancos.clef", Linha("a") + "\n\n   \n\t\n" + Linha("b") + "\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(2, leitura.Eventos.Count);
        Assert.Equal(0, leitura.LinhasInvalidas);
    }

    [Fact]
    public async Task A_corrupted_line_does_not_take_the_rest_of_the_file_with_it()
    {
        // Contrato do app: a carga de 104 arquivos não pode parar por causa de um registro
        // truncado no meio de um deles.
        var arquivo = Escrever("parcial.clef", Linha("antes") + "\nnao e json\n" + Linha("depois") + "\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(new[] { "antes", "depois" }, leitura.Eventos.Select(e => e.Message));
        Assert.Equal(1, leitura.LinhasInvalidas);
        Assert.False(string.IsNullOrWhiteSpace(leitura.PrimeiroErro));
    }

    [Fact]
    public async Task Only_the_first_error_is_reported()
    {
        // A mensagem que sobra é a da PRIMEIRA linha ruim; o app a mostra junto da contagem.
        var arquivo = Escrever("varias-falhas.clef", "primeira falha\n{\"@t\":null}\nsegunda falha\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(3, leitura.LinhasInvalidas);
        Assert.Contains("'p' is an invalid start of a value", leitura.PrimeiroErro);
    }

    [Fact]
    public async Task A_line_larger_than_the_initial_buffer_is_parsed_whole()
    {
        // Stack trace grande em @x é comum; o buffer começa em 64 KB e precisa crescer.
        var gigante = new string('X', 3_000_000);
        var arquivo = Escrever(
            "gigante.clef",
            Linha("antes") + "\n" +
            $@"{{""@t"":""2026-06-15T12:00:00.0000000Z"",""@mt"":""grande"",""@x"":""{gigante}""}}" + "\n" +
            Linha("depois") + "\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(3, leitura.Eventos.Count);
        Assert.Equal(3_000_000, leitura.Eventos[1].Exception!.Length);
    }

    [Fact]
    public async Task A_gzipped_file_is_decompressed_before_parsing()
    {
        var caminho = Path.Combine(_pasta, "app.clef.gz");
        await using (var fs = File.Create(caminho))
        await using (var gz = new GZipStream(fs, CompressionMode.Compress))
        {
            gz.Write(Encoding.UTF8.GetBytes(Linha("comprimido") + "\n" + Linha("outro") + "\n"));
        }

        var leitura = await Ler(caminho);

        Assert.Equal(new[] { "comprimido", "outro" }, leitura.Eventos.Select(e => e.Message));
        // Sem offset: o tail não sabe retomar de dentro de um arquivo compactado.
        Assert.Null(leitura.OffsetFinal);
    }

    [Fact]
    public async Task The_final_offset_is_the_end_of_the_file()
    {
        // É por esse offset que o modo "ao vivo" retoma a leitura.
        var arquivo = Escrever("offset.clef", Linha("a") + "\n" + Linha("b") + "\n");

        var leitura = await Ler(arquivo);

        Assert.Equal(new FileInfo(arquivo).Length, leitura.OffsetFinal);
    }

    [Fact]
    public async Task Ignored_texts_filter_by_message_and_by_exception()
    {
        var arquivo = Escrever(
            "ignorar.clef",
            Linha("mantida") + "\n" +
            Linha("descartada por RUIDO") + "\n" +
            @"{""@t"":""2026-06-15T12:00:00.0000000Z"",""@mt"":""ok"",""@x"":""falha de RUIDO aqui""}" + "\n");

        var leitura = await new LeitorArquivoLog().LerAsync(arquivo, new[] { "ruido" });

        Assert.Single(leitura.Eventos);
        Assert.Equal("mantida", leitura.Eventos[0].Message);
    }

    [Fact]
    public async Task The_pool_shares_the_template_between_files_of_the_same_load()
    {
        // Um pool por CARGA: o mesmo template aparece em todos os arquivos da aplicação e não
        // pode virar uma string nova por linha.
        var pool = new PoolDeTextos();
        var primeiro = Escrever("um.clef", Linha("igual") + "\n");
        var segundo = Escrever("dois.clef", Linha("igual") + "\n");

        var a = await Ler(primeiro, pool);
        var b = await Ler(segundo, pool);

        Assert.Same(a.Eventos[0].MessageTemplate, b.Eventos[0].MessageTemplate);
        Assert.Same(a.Eventos[0].Level, b.Eventos[0].Level);
    }

    [Fact]
    public async Task Reading_can_be_cancelled()
    {
        var arquivo = Escrever("cancelar.clef", string.Concat(Enumerable.Repeat(Linha("x") + "\n", 5_000)));
        using var cancelamento = new CancellationTokenSource();
        await cancelamento.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LeitorArquivoLog().LerAsync(arquivo, Array.Empty<string>(), null, cancelamento.Token));
    }

    [Fact]
    public async Task Files_are_read_while_the_writer_keeps_the_handle_open()
    {
        // O log é lido com o aplicativo que o escreve ainda rodando (FileShare.ReadWrite).
        var arquivo = Escrever("aberto.clef", Linha("a") + "\n");
        await using var escritor = new FileStream(arquivo, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var leitura = await Ler(arquivo);

        Assert.Single(leitura.Eventos);
    }

    [Fact]
    public async Task Concurrent_loads_of_many_files_produce_the_same_events()
    {
        // A carga real usa Parallel.ForEachAsync com um pool (e portanto um cache de templates)
        // compartilhado: estrutura não concorrente aqui some com eventos de forma intermitente.
        var pool = new PoolDeTextos();
        var arquivos = Enumerable.Range(0, 16)
            .Select(i => Escrever(
                $"par{i}.clef",
                string.Concat(Enumerable.Range(0, 200).Select(j => Linha($"t{j % 7} {i}") + "\n"))))
            .ToArray();

        var totais = new int[arquivos.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, arquivos.Length),
            async (i, token) =>
            {
                var leitura = await new LeitorArquivoLog().LerAsync(arquivos[i], Array.Empty<string>(), pool, token);
                totais[i] = leitura.Eventos.Count;
            });

        Assert.All(totais, t => Assert.Equal(200, t));
    }
}
