using ClefExplorer.Helpers;
using ClefExplorer.Models;
using Microsoft.AspNetCore.Components;

namespace ClefExplorer.Components;

public partial class LogColumnFilters
{
    [Parameter] public IReadOnlyList<DiscoveredColumn> Colunas { get; set; } = Array.Empty<DiscoveredColumn>();
    [Parameter] public IReadOnlyList<FiltroColunaLog> Filtros { get; set; } = Array.Empty<FiltroColunaLog>();
    [Parameter] public EventCallback<IReadOnlyList<FiltroColunaLog>> FiltrosChanged { get; set; }
    private bool _aberto;
    private IEnumerable<DiscoveredColumn> Campos => LogGridColumns.Fixed
        .Select(c => new DiscoveredColumn(c.Key, c.Title, 1))
        .Concat(Colunas)
        .Concat(Filtros.Select(f => new DiscoveredColumn(f.Campo, f.Campo, 1, f.Tipo)))
        .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase);

    private string? Valor(string campo) => Filtros.FirstOrDefault(f => f.Campo == campo)?.Texto;
    private Task Alterar(DiscoveredColumn coluna, string? texto)
    {
        var filtros = Filtros.Where(f => f.Campo != coluna.Key).ToList();
        if (!string.IsNullOrWhiteSpace(texto)) filtros.Add(new(coluna.Key, texto, coluna.Kind));
        return FiltrosChanged.InvokeAsync(filtros);
    }
}
