using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ClefExplorer.Models;
using ClefExplorer.Services;
using ClefExplorer.Helpers;
using Omni.Blazor;
using Omni.Blazor.Models;
using Omni.Blazor.Services;
using Omni.Blazor.Components;

namespace ClefExplorer.Components;

public partial class LogGrid
{
    /// <summary>Filtragem em andamento sem nada ainda para mostrar.</summary>
    [Parameter] public bool IsBusy { get; set; }

    [Parameter] public IReadOnlyList<ClefEvent> Eventos { get; set; } = Array.Empty<ClefEvent>();
    [Parameter] public ClefEvent? SelectedEvent { get; set; }
    [Parameter] public IReadOnlySet<ClefEvent> CorrelatedEvents { get; set; } = new HashSet<ClefEvent>();
    [Parameter] public EventCallback<ClefEvent> OnSelect { get; set; }

    /// <summary>Colunas descobertas no conteúdo dos logs carregados.</summary>
    [Parameter] public IReadOnlyList<DiscoveredColumn> Colunas { get; set; } = Array.Empty<DiscoveredColumn>();

    /// <summary>Chaves das colunas visíveis (fixas e descobertas).</summary>
    [Parameter] public HashSet<string> VisibleColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Parameter] public EventCallback<HashSet<string>> VisibleColumnsChanged { get; set; }

    private bool _colunasOpen;

    /// <summary>Propriedades recorrentes do log (SourceContext, Application…).</summary>
    private IReadOnlyList<DiscoveredColumn> ColunasDePropriedade =>
        Colunas.Where(c => c.Source == ColumnSource.Property).ToList();

    /// <summary>Parâmetros dos message templates ({ProviderKey}, {Interval}…).</summary>
    private IReadOnlyList<DiscoveredColumn> ColunasDeTemplate =>
        Colunas.Where(c => c.Source == ColumnSource.TemplateField).ToList();

    private async Task Alternar(string key, bool visivel)
    {
        var novo = new HashSet<string>(VisibleColumns, StringComparer.OrdinalIgnoreCase);
        if (visivel) novo.Add(key); else novo.Remove(key);

        VisibleColumns = novo;
        await VisibleColumnsChanged.InvokeAsync(novo);
    }

    private static string NomeArquivo(ClefEvent e) =>
        string.IsNullOrEmpty(e.SourceFile) ? string.Empty : Path.GetFileName(e.SourceFile);

    /// <summary>
    /// Identidade da mensagem para agrupar, ordenar e filtrar: o template quando existe,
    /// senão a própria mensagem (logs sem <c>@mt</c>, ou linhas em que o template É a
    /// mensagem por não ter parâmetro).
    /// </summary>
    private static string TemplateOuMensagem(ClefEvent e) =>
        !string.IsNullOrEmpty(e.MessageTemplate) ? e.MessageTemplate : e.Message ?? string.Empty;

    /// <summary>Destaca linhas correlacionadas, a selecionada e as de erro, como na lista.</summary>
    private string LinhaCss(ClefEvent e)
    {
        var classes = new List<string>(3);

        if (CorrelatedEvents.Contains(e)) classes.Add("is-correlated");
        if (ReferenceEquals(e, SelectedEvent)) classes.Add("is-selected");

        if (string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Level, "Fatal", StringComparison.OrdinalIgnoreCase))
        {
            classes.Add("clef-grid-row-error");
        }

        return string.Join(' ', classes);
    }
}
