using System.Globalization;
using ClefExplorer.Helpers;
using Microsoft.AspNetCore.Components;

namespace ClefExplorer.Components;

public partial class LogRanking
{
    [Parameter] public IReadOnlyList<StatEntry> Entradas { get; set; } = Array.Empty<StatEntry>();
    [Parameter] public EventCallback<string> OnSelect { get; set; }
    private string Largura(int quantidade) => $"width: {(100m * quantidade / Math.Max(1, Entradas.Max(e => e.Count))).ToString("0.##", CultureInfo.InvariantCulture)}%";
}
