using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

public sealed class IntegridadeIngestaoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
    public IntegridadeIngestaoTests() => Directory.CreateDirectory(_raiz);
    private static string Linha(string mensagem) => JsonSerializer.Serialize(new Dictionary<string, string>
        { ["@t"] = "2026-09-09T12:00:00Z", ["@m"] = mensagem });
    private string Gravar(string nome, string conteudo)
    {
        var caminho = Path.Combine(_raiz, nome);
        File.WriteAllText(caminho, conteudo, new UTF8Encoding(false));
        return caminho;
    }
    private LogStore Store(ILeitorArquivoLog? leitor = null)
    {
        var settings = new SettingsService(new AppStorage(Path.Combine(_raiz, "config")));
        return new(settings, new DescobertaArquivosLog(), leitor ?? new LeitorArquivoLog(), new FiltroArquivosLogIgnorados(settings));
    }
    private static Task Poll(LogStore store) => (Task)typeof(LogStore)
        .GetMethod("PollTailAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(store, [CancellationToken.None])!;

    [Fact]
    public async Task CancelLoad_ParcialPublicado_TailNaoDuplicaNemAdotaArquivoIncompleto()
    {
        var rapido = Gravar("rapido.clef", Linha("rapido") + "\n");
        var lento = Gravar("lento.clef", Linha("lento") + "\n");
        var leitor = new LeitorBloqueavel();
        using var store = Store(leitor);
        store.PublicacaoParcialMs = 0;
        var parcial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += () => { if (store.IsLoading && store.Count == 1) parcial.TrySetResult(); };
        var carga = store.LoadFromPathsAsync([rapido, lento]);
        await parcial.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await leitor.Iniciado.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.CancelLoad();
        await carga;
        Assert.Equal([rapido], store.LoadedFiles);
        await File.AppendAllTextAsync(rapido, Linha("novo") + "\n");
        await Poll(store);
        Assert.Equal(2, store.Count);
        Assert.Single(store.Snapshot(), e => e.Message == "rapido");
        Assert.DoesNotContain(store.Snapshot(), e => e.Message == "lento");
        leitor.Liberar.TrySetResult();
        await store.UpdateLoadedFiles([rapido, lento]);
        Assert.Equal(3, store.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollTail_ArquivoReescritoComFronteiraAlinhada_LeConteudoSubstituido(bool acrescentar)
    {
        var caminho = Gravar("ativo.clef", Linha("AAAA") + "\n");
        using var store = Store();
        await store.LoadFromFile(caminho);
        await File.WriteAllTextAsync(caminho, Linha("BBBB") + "\n" + (acrescentar ? Linha("CCCC") + "\n" : ""));
        await Poll(store);
        Assert.Single(store.Snapshot(), e => e.Message == "BBBB");
        if (!acrescentar) await File.AppendAllTextAsync(caminho, Linha("CCCC") + "\n");
        await Poll(store);
        Assert.Single(store.Snapshot(), e => e.Message == "BBBB");
        Assert.Single(store.Snapshot(), e => e.Message == "CCCC");
    }

    [Fact]
    public async Task PollTail_ArquivoAdotadoReescrito_LeConteudoSubstituido()
    {
        Gravar("inicial.clef", Linha("inicial") + "\n");
        using var store = Store();
        await store.LoadFromFolderAsync(_raiz);
        var novo = Gravar("novo.clef", Linha("AAAA") + "\n");
        await (Task)typeof(LogStore).GetMethod("RedescobrirArquivosAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, [CancellationToken.None])!;
        Assert.Contains(novo, store.LoadedFiles);
        await File.WriteAllTextAsync(novo, Linha("BBBB") + "\n");
        await Poll(store);
        Assert.Single(store.Snapshot(), e => e.Message == "BBBB");
        Assert.DoesNotContain(store.Snapshot(), e => e.Message == "AAAA");
    }

    [Fact]
    public async Task PollTail_ReescritaNoMeioSemMudarTamanho_DetectaAlteracaoPelaData()
    {
        var borda = Linha(new string('x', 1024)) + "\n";
        var caminho = Gravar("dados.clef", borda + Linha("AAAA") + "\n" + borda);
        var data = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(caminho, data);
        using var store = Store();
        await store.LoadFromFile(caminho);
        await File.WriteAllTextAsync(caminho, borda + Linha("BBBB") + "\n" + borda);
        File.SetLastWriteTimeUtc(caminho, data.AddSeconds(1));
        await Poll(store);
        Assert.Single(store.Snapshot(), e => e.Message == "BBBB");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollTail_EofSemQuebra_NaoDuplicaUltimaLinha(bool paralelo)
    {
        var caminho = Gravar("ativo.clef", Linha("primeiro"));
        using var store = Store(new LeitorArquivoLog(paralelo ? 1 : long.MaxValue, 8));
        await store.LoadFromFile(caminho);
        await File.AppendAllTextAsync(caminho, "\n" + Linha("segundo") + "\n");
        await Poll(store);
        Assert.Equal(2, store.Count);
        Assert.Single(store.Snapshot(), e => e.Message == "primeiro");
        Assert.Single(store.Snapshot(), e => e.Message == "segundo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollTail_JsonIncompletoNaCarga_RecuperaLinhaQuandoProdutorTermina(bool paralelo)
    {
        var linha = Linha("incompleto");
        var caminho = Gravar("ativo.clef", Linha("valido") + "\n" + linha[..^3]);
        using var store = Store(new LeitorArquivoLog(paralelo ? 1 : long.MaxValue, 8));
        await store.LoadFromFile(caminho);
        await File.AppendAllTextAsync(caminho, linha[^3..] + "\n");
        await Poll(store);
        Assert.Equal(2, store.Count);
        Assert.Single(store.Snapshot(), e => e.Message == "incompleto");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LerAsync_LinhaAcimaDoLimite_DescartaUmaLinhaEPreservaAsSeguintes(bool gzip, bool paralelo)
    {
        var conteudo = Linha("antes") + "\n" + new string('x', 2048) + "\n" + Linha("depois") + "\n";
        var caminho = Path.Combine(_raiz, gzip ? "dados.clef.gz" : "dados.clef");
        if (gzip)
        {
            await using var arquivo = File.Create(caminho);
            await using var zip = new GZipStream(arquivo, CompressionMode.Compress);
            await zip.WriteAsync(Encoding.UTF8.GetBytes(conteudo));
        }
        else await File.WriteAllTextAsync(caminho, conteudo);
        var leitura = await new LeitorArquivoLog(paralelo ? 1 : long.MaxValue, 128, 256)
            .LerAsync(caminho, Array.Empty<string>());
        Assert.Equal(1, leitura.LinhasInvalidas);
        Assert.Contains("limite de 256 bytes", leitura.PrimeiroErro);
        Assert.Equal(["antes", "depois"], leitura.Eventos.Select(e => e.Message));
    }

    [Fact]
    public async Task PollTail_LinhaExcedenteInacabada_NaoInterpretaSufixoComoNovoEvento()
    {
        var caminho = Gravar("dados.clef", new string('x', 2048));
        using var store = Store(new LeitorArquivoLog(long.MaxValue, 128, 256));
        await store.LoadFromFile(caminho);
        await File.AppendAllTextAsync(caminho, Linha("sufixo") + "\n" + Linha("novo") + "\n");
        await Poll(store);
        Assert.Equal("novo", Assert.Single(store.Snapshot()).Message);
    }

    [Fact]
    public async Task UpdateLoadedFiles_SelecaoVazia_LiberaPoolDaSessao()
    {
        var caminho = Gravar("dados.clef", string.Join('\n', Enumerable.Range(0, 200).Select(i =>
            $"{{\"@t\":\"2026-09-09T12:00:00Z\",\"@mt\":\"template {i}\"}}")));
        using var store = Store();
        await store.LoadFromFile(caminho);
        var campo = typeof(LogStore).GetField("_poolSessao", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var antigo = campo.GetValue(store);
        await store.UpdateLoadedFiles(Array.Empty<string>());
        Assert.NotSame(antigo, campo.GetValue(store));
        Assert.Empty(store.Snapshot());
    }

    public void Dispose() => Directory.Delete(_raiz, true);
}
