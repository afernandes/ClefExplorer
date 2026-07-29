using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Preferências de layout da interface. Ficam em <c>ui.json</c>, e não no
/// <c>settings.json</c>, porque salvar as configurações dispara um recarregamento de todos
/// os arquivos de log — inaceitável para uma preferência puramente visual.
/// </summary>
public class UiPreferencesTests : IDisposable
{
    private readonly string _root;

    public UiPreferencesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* limpeza best-effort */ }
    }

    private UiPreferencesService NewService() => new(new AppStorage(_root, legacyFolder: null));

    [Fact]
    public void Detail_panel_starts_on_the_right()
    {
        Assert.Equal(DetailPanelPosition.Right, NewService().Preferences.DetailPanelPosition);
    }

    [Fact]
    public void Toggling_moves_the_panel_to_the_bottom_and_back()
    {
        var service = NewService();

        Assert.Equal(DetailPanelPosition.Bottom, service.ToggleDetailPanelPosition());
        Assert.Equal(DetailPanelPosition.Right, service.ToggleDetailPanelPosition());
    }

    [Fact]
    public void The_chosen_position_survives_a_restart()
    {
        NewService().ToggleDetailPanelPosition();

        Assert.Equal(DetailPanelPosition.Bottom, NewService().Preferences.DetailPanelPosition);
    }

    [Fact]
    public void The_position_is_written_by_name_not_by_number()
    {
        // O arquivo é editável à mão: "Bottom" diz algo, "1" não.
        NewService().ToggleDetailPanelPosition();

        var json = File.ReadAllText(Path.Combine(_root, "ui.json"));
        Assert.Contains("\"Bottom\"", json);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_the_default_without_throwing()
    {
        File.WriteAllText(Path.Combine(_root, "ui.json"), "não é json");

        Assert.Equal(DetailPanelPosition.Right, NewService().Preferences.DetailPanelPosition);
    }

    [Fact]
    public void An_unknown_position_falls_back_to_the_default()
    {
        File.WriteAllText(Path.Combine(_root, "ui.json"), """{"DetailPanelPosition":"Diagonal"}""");

        Assert.Equal(DetailPanelPosition.Right, NewService().Preferences.DetailPanelPosition);
    }
}
