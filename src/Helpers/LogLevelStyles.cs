using Omni.Blazor.Models;

namespace ClefExplorer.Helpers
{
    /// <summary>
    /// Mapeia o nível do log (Serilog) para a variante visual do <c>OmniBadge</c>.
    /// Centraliza o mapeamento usado por LogList e LogDetails.
    /// </summary>
    public static class LogLevelStyles
    {
        public static BadgeVariant Variant(string? level) => level switch
        {
            "Error" => BadgeVariant.Danger,
            "Fatal" => BadgeVariant.Danger,
            "Warning" => BadgeVariant.Warn,
            "Information" => BadgeVariant.Info,
            "Debug" => BadgeVariant.Plain,
            "Verbose" => BadgeVariant.Default,
            _ => BadgeVariant.Default,
        };
    }
}
