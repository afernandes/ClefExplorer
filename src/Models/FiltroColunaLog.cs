using System.Globalization;
using ClefExplorer.Helpers;

namespace ClefExplorer.Models;

public sealed record FiltroColunaLog(string Campo, string Texto, ColumnValueKind Tipo = ColumnValueKind.Text)
{
    public bool Corresponde(ClefEvent evento)
    {
        object? valor = Campo switch
        {
            "Timestamp" => evento.Timestamp?.ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture),
            "Level" => evento.Level,
            "Message" => string.IsNullOrEmpty(evento.MessageTemplate) ? evento.Message : evento.MessageTemplate,
            "SourceFile" => Path.GetFileName(evento.SourceFile),
            "Exception" => string.IsNullOrEmpty(evento.Exception) ? "" : "sim",
            _ => LogColumnDiscovery.GetSortValue(evento, Campo, Tipo)
        };
        return (Convert.ToString(valor, CultureInfo.InvariantCulture) ?? "")
            .Contains(Texto, StringComparison.OrdinalIgnoreCase);
    }
}
