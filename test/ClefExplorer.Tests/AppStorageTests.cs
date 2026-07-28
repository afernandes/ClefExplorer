using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="AppStorage"/> — a camada que passou a gravar em
/// %LOCALAPPDATA% porque a pasta do executável é somente leitura numa instalação MSIX.
/// </summary>
public class AppStorageTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataFolder;
    private readonly string _legacyFolder;

    public AppStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
        _dataFolder = Path.Combine(_root, "data");
        _legacyFolder = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(_dataFolder);
        Directory.CreateDirectory(_legacyFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* limpeza best-effort */ }
    }

    private AppStorage NewStorage() => new(_dataFolder, _legacyFolder);

    [Fact]
    public void ReadText_returns_null_when_the_file_does_not_exist()
    {
        Assert.Null(NewStorage().ReadText("settings.json"));
    }

    [Fact]
    public void WriteText_then_ReadText_roundtrips()
    {
        var storage = NewStorage();

        storage.WriteText("settings.json", """{"a":1}""");

        Assert.Equal("""{"a":1}""", storage.ReadText("settings.json"));
    }

    [Fact]
    public void WriteText_creates_the_data_folder_when_missing()
    {
        var novaPasta = Path.Combine(_root, "ainda-nao-existe");
        var storage = new AppStorage(novaPasta);

        storage.WriteText("groups.json", "[]");

        Assert.True(File.Exists(Path.Combine(novaPasta, "groups.json")));
    }

    [Fact]
    public void WriteText_overwrites_and_leaves_no_temp_file_behind()
    {
        var storage = NewStorage();

        storage.WriteText("settings.json", "primeiro");
        storage.WriteText("settings.json", "segundo");

        Assert.Equal("segundo", storage.ReadText("settings.json"));
        Assert.False(File.Exists(Path.Combine(_dataFolder, "settings.json.tmp")));
    }

    // --- Migração da pasta do executável ---------------------------------------

    [Fact]
    public void Legacy_file_is_migrated_on_first_read()
    {
        File.WriteAllText(Path.Combine(_legacyFolder, "groups.json"), """[{"Name":"Produção"}]""");
        var storage = NewStorage();

        var content = storage.ReadText("groups.json");

        Assert.Contains("Produção", content);
        Assert.True(File.Exists(Path.Combine(_dataFolder, "groups.json")));
    }

    [Fact]
    public void Legacy_file_is_copied_not_moved()
    {
        // A versão anterior do app pode continuar instalada e depender do arquivo original.
        var legacyPath = Path.Combine(_legacyFolder, "settings.json");
        File.WriteAllText(legacyPath, "conteudo");

        NewStorage().ReadText("settings.json");

        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void Existing_data_file_wins_over_the_legacy_one()
    {
        File.WriteAllText(Path.Combine(_legacyFolder, "settings.json"), "antigo");
        File.WriteAllText(Path.Combine(_dataFolder, "settings.json"), "atual");

        Assert.Equal("atual", NewStorage().ReadText("settings.json"));
    }

    [Fact]
    public void Migration_does_not_run_again_after_the_file_is_written()
    {
        File.WriteAllText(Path.Combine(_legacyFolder, "settings.json"), "antigo");
        var storage = NewStorage();

        storage.ReadText("settings.json");          // migra
        storage.WriteText("settings.json", "novo"); // usuário salva

        Assert.Equal("novo", storage.ReadText("settings.json"));
    }

    [Fact]
    public void Works_without_a_legacy_folder()
    {
        var storage = new AppStorage(_dataFolder, legacyFolder: null);

        storage.WriteText("settings.json", "ok");

        Assert.Equal("ok", storage.ReadText("settings.json"));
    }

    // --- Quarentena de arquivo inválido ----------------------------------------

    [Fact]
    public void Quarantine_moves_the_file_aside_and_returns_its_path()
    {
        var storage = NewStorage();
        storage.WriteText("groups.json", "{ json quebrado");

        var corrupt = storage.Quarantine("groups.json");

        Assert.NotNull(corrupt);
        Assert.True(File.Exists(corrupt));
        Assert.False(File.Exists(Path.Combine(_dataFolder, "groups.json")));
        Assert.Equal("{ json quebrado", File.ReadAllText(corrupt!));
    }

    [Fact]
    public void Quarantine_returns_null_when_there_is_nothing_to_quarantine()
    {
        Assert.Null(NewStorage().Quarantine("inexistente.json"));
    }

    [Fact]
    public void Quarantine_twice_overwrites_the_previous_corrupt_file()
    {
        var storage = NewStorage();
        storage.WriteText("groups.json", "primeiro-quebrado");
        storage.Quarantine("groups.json");
        storage.WriteText("groups.json", "segundo-quebrado");

        var corrupt = storage.Quarantine("groups.json");

        Assert.Equal("segundo-quebrado", File.ReadAllText(corrupt!));
    }
}
