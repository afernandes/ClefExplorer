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

public partial class LogStatsPanel
{
    [Parameter] public LogStats Stats { get; set; } = new();

    /// <summary>Clique num nível aplica o filtro rápido correspondente.</summary>
    [Parameter] public EventCallback<string> OnFilterLevel { get; set; }

    /// <summary>Clique numa origem/mensagem/exceção joga o texto na busca.</summary>
    [Parameter] public EventCallback<FiltroRanking> OnFilterRanking { get; set; }

    private int MaiorNivel => Stats.ByLevel.Count == 0 ? 1 : Stats.ByLevel.Max(e => e.Count);

    private string PercentualErro =>
        Stats.Total == 0 ? "0%" : (Stats.ErrorCount / (double)Stats.Total).ToString("P1");

    private static string Largura(int valor, int maximo) =>
        $"width: {(maximo <= 0 ? 0 : valor * 100.0 / maximo).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%";

    /// <summary>Duas séries: o volume total e, sobreposto, quanto dele é erro.</summary>
    private IEnumerable<ChartSeries> SerieTimeline => new[]
    {
        new ChartSeries
        {
            Title = "Eventos",
            Type = ChartSeriesType.Column,
            Points = Stats.Timeline.Select(b => new ChartDataPoint
            {
                Category = b.Start.ToString(FormatoDaFatia),
                Value = b.Total,
            }).ToList(),
        },
        new ChartSeries
        {
            Title = "Erros",
            Type = ChartSeriesType.Column,
            Points = Stats.Timeline.Select(b => new ChartDataPoint
            {
                Category = b.Start.ToString(FormatoDaFatia),
                Value = b.Errors,
            }).ToList(),
        },
    };

    /// <summary>O eixo mostra só o que distingue as fatias: hora para períodos curtos, data para longos.</summary>
    private string FormatoDaFatia => Stats.BucketSize >= TimeSpan.FromDays(1) ? "dd/MM"
        : Stats.BucketSize >= TimeSpan.FromHours(1) ? "dd/MM HH'h'"
        : "HH:mm";

    private string RotuloFatia
    {
        get
        {
            var b = Stats.BucketSize;
            if (b <= TimeSpan.Zero) return "—";
            if (b >= TimeSpan.FromDays(1)) return $"por {b.TotalDays:0.#} dia(s)";
            if (b >= TimeSpan.FromHours(1)) return $"por {b.TotalHours:0.#} hora(s)";
            if (b >= TimeSpan.FromMinutes(1)) return $"por {b.TotalMinutes:0.#} minuto(s)";
            return $"por {Math.Max(1, b.TotalSeconds):0.#} segundo(s)";
        }
    }
}
