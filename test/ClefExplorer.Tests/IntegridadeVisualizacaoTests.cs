using Bunit;
using ClefExplorer.Components;
using ClefExplorer.Helpers;
using ClefExplorer.Models;
using ClefExplorer.Services;
using Microsoft.Extensions.DependencyInjection;
using Omni.Blazor;
using System.Reflection;

namespace ClefExplorer.Tests;

public sealed class IntegridadeVisualizacaoTests : IAsyncLifetime
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
    private readonly BunitContext _context = new();
    private readonly LogStore _store;
    private readonly LeitorBloqueavel _leitor = new();
    private readonly string _exportacao;

    public IntegridadeVisualizacaoTests()
    {
        Directory.CreateDirectory(_raiz);
        _exportacao = Path.Combine(_raiz, "exportacao.clef");
        var storage = new AppStorage(Path.Combine(_raiz, "config"));
        var settings = new SettingsService(storage);
        _store = new(settings, new DescobertaArquivosLog(), _leitor, new FiltroArquivosLogIgnorados(settings)) { PublicacaoParcialMs = 0 };
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.Services.AddOmniComponents();
        _context.Services.AddSingleton(storage);
        _context.Services.AddSingleton(settings);
        _context.Services.AddSingleton(_store);
        _context.Services.AddSingleton<LogGroupService>();
        _context.Services.AddSingleton<IFilePickerService>(new FilePickerTeste(_exportacao));
        _context.Services.AddSingleton<FileAssociationService>();
        _context.Services.AddSingleton<UiPreferencesService>();
        _context.Services.AddSingleton<ConsultaLogs>();
        _context.Services.AddSingleton<NavegacaoCorrelacao>();
        _context.Services.AddSingleton<AnaliseTemporalCorrelacao>();
        _context.Services.AddSingleton<LeituraMetadadosObservabilidade>();
        _context.Services.AddSingleton<ExploradorArquivos>();
        var atualizacao = new AtualizacaoTeste();
        _context.Services.AddSingleton(new ServicoAtualizacao(atualizacao, atualizacao));
    }

    private string Arquivo(string nome, string conteudo)
    {
        var caminho = Path.Combine(_raiz, nome);
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }
    private const string Linha = "{\"@t\":\"2026-09-09T12:00:00Z\",\"@mt\":\"Pedido {Id}\",\"Id\":123,\"CorrelationId\":\"c1\"}\n";

    [Fact]
    public async Task OnStoreChanged_CargaAindaEmAndamento_ExibeLotePublicado()
    {
        var rapido = Arquivo("rapido.clef", Linha);
        var lento = Arquivo("lento.clef", Linha);
        var tela = _context.Render<LogViewer>();
        var carga = _store.LoadFromPathsAsync([rapido, lento]);
        try
        {
            await _leitor.Iniciado.Task.WaitAsync(TimeSpan.FromSeconds(10));
            tela.WaitForAssertion(() => Assert.Single(Eventos(tela.Instance)));
            Assert.True(_store.IsLoading);
        }
        finally
        {
            _leitor.Liberar.TrySetResult();
            await carga;
        }
        tela.WaitForAssertion(() => Assert.Equal(2, Eventos(tela.Instance).Count()));
    }

    [Fact]
    public async Task OnPathsLoaded_CorrelacaoAberta_LimpaEventosDaSessaoAnterior()
    {
        await _store.LoadFromFile(Arquivo("antigo.clef", Linha + Linha));
        var tela = _context.Render<LogViewer>();
        tela.WaitForAssertion(() => Assert.Equal(2, Eventos(tela.Instance).Count()));
        await tela.InvokeAsync(() => Acionar(tela.Instance, "Select", _store.Snapshot()[0]));
        await tela.InvokeAsync(() => Acionar(tela.Instance, "AbrirNavegacaoCorrelacao"));
        tela.WaitForAssertion(() => Assert.NotNull(Campo(tela.Instance, "_resultadoCorrelacao")));
        await _store.LoadFromFile(Arquivo("novo.clef", Linha.Replace("123", "456")));
        tela.WaitForAssertion(() =>
        {
            Assert.Null(Campo(tela.Instance, "_resultadoCorrelacao"));
            Assert.Null(Campo(tela.Instance, "_selected"));
            Assert.Equal("Pedido 456", Assert.Single(Eventos(tela.Instance)).Message);
        });
    }

    [Fact]
    public async Task UpdateLoadedFiles_TodosDesmarcados_LimpaSelecaoECorrelacao()
    {
        await _store.LoadFromFile(Arquivo("dados.clef", Linha + Linha));
        var tela = _context.Render<LogViewer>();
        tela.WaitForAssertion(() => Assert.Equal(2, Eventos(tela.Instance).Count));
        await tela.InvokeAsync(() => Acionar(tela.Instance, "Select", _store.Snapshot()[0]));
        await tela.InvokeAsync(() => Acionar(tela.Instance, "AbrirNavegacaoCorrelacao"));
        Assert.NotNull(Campo(tela.Instance, "_resultadoCorrelacao"));
        await _store.UpdateLoadedFiles(Array.Empty<string>());
        tela.WaitForAssertion(() =>
        {
            Assert.Empty(Eventos(tela.Instance));
            Assert.Null(Campo(tela.Instance, "_selected"));
            Assert.Null(Campo(tela.Instance, "_resultadoCorrelacao"));
        });
    }

    [Fact]
    public async Task ExportarFiltrados_FiltroDeColunaAtivo_ExportaMesmoConjuntoDaTabela()
    {
        await _store.LoadFromFile(Arquivo("dados.clef", Linha + Linha.Replace("123", "456")));
        _context.Services.GetRequiredService<UiPreferencesService>().SetViewMode(LogViewMode.Grid);
        var tela = _context.Render<LogViewer>();
        tela.WaitForAssertion(() => Assert.Equal(2, Eventos(tela.Instance).Count));
        await tela.InvokeAsync(() => Acionar(tela.Instance, "AlterarFiltrosColuna", (object)new FiltroColunaLog[] { new("Id", "456", ColumnValueKind.Number) }));
        tela.WaitForAssertion(() => Assert.Single(Eventos(tela.Instance)));
        await tela.InvokeAsync(() => tela.Instance.OnShortcut("export"));
        var exportado = await new LeitorArquivoLog().LerAsync(_exportacao, Array.Empty<string>());
        Assert.Equal("Pedido 456", Assert.Single(exportado.Eventos).Message);
        await tela.InvokeAsync(() => Acionar(tela.Instance, "DefinirModoVisualizacao", LogViewMode.Stats));
        tela.WaitForAssertion(() => Assert.Equal(1, ((LogStats)Campo(tela.Instance, "_stats")!).Total));
    }

    private static object? Campo(LogViewer tela, string nome) => typeof(LogViewer)
        .GetField(nome, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tela);

    private static List<ClefEvent> Eventos(LogViewer tela) => (List<ClefEvent>)Campo(tela, "_todosEventos")!;

    private static Task Acionar(LogViewer tela, string nome, params object[] argumentos) =>
        typeof(LogViewer).GetMethod(nome, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tela, argumentos) as Task ?? Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _leitor.Liberar.TrySetResult();
        await _context.DisposeAsync();
        _store.Dispose();
        Directory.Delete(_raiz, true);
    }
}

