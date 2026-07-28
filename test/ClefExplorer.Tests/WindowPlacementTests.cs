using System.Drawing;
using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Validação da posição salva da janela contra os monitores atuais — sem ela, desconectar
/// um monitor secundário faria a janela reabrir fora da área visível.
/// </summary>
public class WindowPlacementTests : IDisposable
{
    private readonly string _root;

    public WindowPlacementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* limpeza best-effort */ }
    }

    private static readonly Rectangle Primary = new(0, 0, 1920, 1080);
    private static readonly Rectangle Secondary = new(1920, 0, 1920, 1080);

    private static WindowPlacement At(int x, int y, int w = 1200, int h = 800) =>
        new() { X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void A_window_inside_the_primary_screen_is_visible()
    {
        Assert.True(WindowPlacementService.IsVisibleOnAnyScreen(At(100, 100), new[] { Primary }));
    }

    [Fact]
    public void A_window_on_a_disconnected_second_screen_is_not_visible()
    {
        var placement = At(2200, 300); // estava no monitor secundário

        Assert.False(WindowPlacementService.IsVisibleOnAnyScreen(placement, new[] { Primary }));
    }

    [Fact]
    public void The_same_window_is_visible_again_once_that_screen_is_back()
    {
        var placement = At(2200, 300);

        Assert.True(WindowPlacementService.IsVisibleOnAnyScreen(placement, new[] { Primary, Secondary }));
    }

    [Fact]
    public void A_sliver_of_overlap_does_not_count_as_visible()
    {
        // Só 30px dentro da tela: não dá para o usuário alcançar a barra de título.
        var placement = At(1890, 100);

        Assert.False(WindowPlacementService.IsVisibleOnAnyScreen(placement, new[] { Primary }));
    }

    [Fact]
    public void A_partially_offscreen_but_reachable_window_is_visible()
    {
        var placement = At(1400, 100);

        Assert.True(WindowPlacementService.IsVisibleOnAnyScreen(placement, new[] { Primary }));
    }

    [Fact]
    public void A_negative_position_beyond_the_screen_is_not_visible()
    {
        Assert.False(WindowPlacementService.IsVisibleOnAnyScreen(At(-1500, 100), new[] { Primary }));
    }

    // --- Persistência ------------------------------------------------------------

    private WindowPlacementService NewService() => new(new AppStorage(_root, legacyFolder: null));

    [Fact]
    public void Load_returns_null_when_nothing_was_saved()
    {
        Assert.Null(NewService().Load());
    }

    [Fact]
    public void Placement_roundtrips()
    {
        NewService().Save(new WindowPlacement { X = 260, Y = 140, Width = 900, Height = 560, Maximized = true });

        var loaded = NewService().Load();

        Assert.NotNull(loaded);
        Assert.Equal(260, loaded!.X);
        Assert.Equal(140, loaded.Y);
        Assert.Equal(900, loaded.Width);
        Assert.Equal(560, loaded.Height);
        Assert.True(loaded.Maximized);
    }

    [Fact]
    public void An_implausibly_small_placement_is_discarded()
    {
        NewService().Save(new WindowPlacement { X = 0, Y = 0, Width = 10, Height = 10 });

        Assert.Null(NewService().Load());
    }

    [Fact]
    public void A_corrupt_file_does_not_throw()
    {
        File.WriteAllText(Path.Combine(_root, "window.json"), "não é json");

        Assert.Null(NewService().Load());
    }
}
