using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// <see cref="SettingsService"/> e <see cref="LogGroupService"/> sobre um
/// <see cref="AppStorage"/> isolado: persistência, migração e o tratamento de arquivo
/// inválido (que antes apagava os dados do usuário sem aviso).
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataFolder;
    private readonly string _legacyFolder;

    public PersistenceTests()
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

    // --- Grupos -----------------------------------------------------------------

    [Fact]
    public void Groups_survive_a_restart()
    {
        var storage = NewStorage();
        var service = new LogGroupService(storage);
        service.AddGroup(new LogGroup { Name = "Produção", Paths = { @"C:\logs\prod" } });

        var reaberto = new LogGroupService(NewStorage());

        Assert.Single(reaberto.Groups);
        Assert.Equal("Produção", reaberto.Groups[0].Name);
        Assert.Null(reaberto.LastError);
    }

    [Fact]
    public void Update_and_delete_are_persisted()
    {
        var service = new LogGroupService(NewStorage());
        var grupo = new LogGroup { Name = "Antigo" };
        service.AddGroup(grupo);

        grupo.Name = "Novo";
        service.UpdateGroup(grupo);
        Assert.Equal("Novo", new LogGroupService(NewStorage()).Groups[0].Name);

        service.DeleteGroup(grupo.Id);
        Assert.Empty(new LogGroupService(NewStorage()).Groups);
    }

    [Fact]
    public void Corrupt_groups_file_is_quarantined_instead_of_silently_discarded()
    {
        // Regressão: um groups.json inválido zerava a lista em memória e a gravação
        // seguinte apagava os grupos do usuário, sem nenhum aviso.
        File.WriteAllText(Path.Combine(_dataFolder, "groups.json"), "{ isto não é json válido");

        var service = new LogGroupService(NewStorage());

        Assert.Empty(service.Groups);
        Assert.NotNull(service.LastError);
        Assert.True(File.Exists(Path.Combine(_dataFolder, "groups.json.corrupt")));
        Assert.False(File.Exists(Path.Combine(_dataFolder, "groups.json")));
    }

    [Fact]
    public void Groups_are_migrated_from_the_executable_folder()
    {
        File.WriteAllText(Path.Combine(_legacyFolder, "groups.json"),
            """[{"Id":"1","Name":"Herdado","Paths":["C:\\logs"]}]""");

        var service = new LogGroupService(NewStorage());

        Assert.Single(service.Groups);
        Assert.Equal("Herdado", service.Groups[0].Name);
    }

    // --- Configurações ----------------------------------------------------------

    [Fact]
    public void Settings_survive_a_restart()
    {
        var service = new SettingsService(NewStorage());
        service.Settings.IgnoredFilePatterns.Add("*.tmp.clef");
        service.Settings.IgnoredLogLines.Add("health check");
        service.Save();

        var reaberto = new SettingsService(NewStorage());

        Assert.Contains("*.tmp.clef", reaberto.Settings.IgnoredFilePatterns);
        Assert.Contains("health check", reaberto.Settings.IgnoredLogLines);
        Assert.Null(reaberto.LastError);
    }

    [Fact]
    public void Save_notifies_listeners()
    {
        var service = new SettingsService(NewStorage());
        var notified = false;
        service.Changed += () => notified = true;

        service.Save();

        Assert.True(notified);
    }

    [Fact]
    public void Corrupt_settings_file_is_quarantined_and_defaults_are_used()
    {
        File.WriteAllText(Path.Combine(_dataFolder, "settings.json"), "não é json");

        var service = new SettingsService(NewStorage());

        Assert.Empty(service.Settings.IgnoredFilePatterns);
        Assert.NotNull(service.LastError);
        Assert.True(File.Exists(Path.Combine(_dataFolder, "settings.json.corrupt")));
    }

    [Fact]
    public void Settings_are_migrated_from_the_executable_folder()
    {
        File.WriteAllText(Path.Combine(_legacyFolder, "settings.json"),
            """{"IgnoredFilePatterns":["*herdado*"],"IgnoredLogLines":[]}""");

        var service = new SettingsService(NewStorage());

        Assert.Contains("*herdado*", service.Settings.IgnoredFilePatterns);
    }

    [Fact]
    public void A_read_only_data_folder_reports_the_error_instead_of_failing_silently()
    {
        // Equivalente ao caso da Store: pasta de dados não gravável.
        // Um caminho inválido produz o mesmo efeito de forma portátil.
        var invalido = Path.Combine(_dataFolder, "arquivo-que-nao-e-pasta");
        File.WriteAllText(invalido, "sou um arquivo");
        var service = new SettingsService(new AppStorage(invalido));

        service.Save();

        Assert.NotNull(service.LastError);
    }
}
