using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>Formatos aceitos pela exportação.</summary>
    public enum ExportFormat
    {
        Csv,
        Clef,
        Text,
    }

    /// <summary>
    /// Serializa os eventos <b>filtrados</b> para compartilhar fora do aplicativo.
    /// Antes só era possível copiar o stack trace de um evento por vez.
    /// </summary>
    public static class LogExporter
    {
        /// <summary>Filtro do diálogo "salvar como", na ordem de <see cref="ExportFormat"/>.</summary>
        public const string DialogFilter =
            "CSV (*.csv)|*.csv|CLEF (*.clef)|*.clef|Texto (*.txt)|*.txt";

        public static string Extension(ExportFormat format) => format switch
        {
            ExportFormat.Csv => ".csv",
            ExportFormat.Clef => ".clef",
            _ => ".txt",
        };

        /// <summary>Deduz o formato pela extensão escolhida no diálogo.</summary>
        public static ExportFormat FormatFromPath(string path)
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Csv;
            if (path.EndsWith(".clef", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Clef;
            return ExportFormat.Text;
        }

        public static string Export(IEnumerable<ClefEvent> events, ExportFormat format) => format switch
        {
            ExportFormat.Csv => ToCsv(events),
            ExportFormat.Clef => ToClef(events),
            _ => ToText(events),
        };

        // --- CSV --------------------------------------------------------------------

        public static string ToCsv(IEnumerable<ClefEvent> events)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Level,Message,Exception,SourceFile");

            foreach (var e in events)
            {
                sb.Append(CsvField(FormatTimestamp(e.Timestamp))).Append(',')
                  .Append(CsvField(e.Level)).Append(',')
                  .Append(CsvField(e.Message)).Append(',')
                  .Append(CsvField(e.Exception)).Append(',')
                  .Append(CsvField(e.SourceFile))
                  .AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escapa conforme RFC 4180: aspas duplicadas e o campo entre aspas quando contém
        /// vírgula, aspas ou quebra de linha — que é o caso comum de mensagens e stack traces.
        /// </summary>
        private static string CsvField(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var precisaAspas = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            var escapado = value.Replace("\"", "\"\"");
            return precisaAspas ? $"\"{escapado}\"" : escapado;
        }

        // --- CLEF -------------------------------------------------------------------

        /// <summary>
        /// Exporta em CLEF (um JSON por linha), de modo que o resultado possa ser reaberto
        /// pelo próprio ClefExplorer.
        /// </summary>
        public static string ToClef(IEnumerable<ClefEvent> events)
        {
            var sb = new StringBuilder();

            foreach (var e in events)
            {
                var registro = new Dictionary<string, object?>
                {
                    ["@t"] = e.Timestamp?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                };

                // No CLEF, "@l" é omitido para Information (é o nível padrão).
                if (!string.IsNullOrEmpty(e.Level) && !string.Equals(e.Level, "Information", StringComparison.OrdinalIgnoreCase))
                {
                    registro["@l"] = e.Level;
                }

                if (!string.IsNullOrEmpty(e.MessageTemplate)) registro["@mt"] = e.MessageTemplate;
                else if (!string.IsNullOrEmpty(e.Message)) registro["@m"] = e.Message;

                if (!string.IsNullOrEmpty(e.Exception)) registro["@x"] = e.Exception;
                if (!string.IsNullOrEmpty(e.SourceFile)) registro["SourceFile"] = e.SourceFile;

                if (e.Properties is not null)
                {
                    foreach (var p in e.Properties)
                    {
                        // Nomes começando com @ são reservados pelo formato.
                        if (p.Key.StartsWith('@')) continue;
                        registro[p.Key] = p.Value.ToString();
                    }
                }

                sb.AppendLine(JsonSerializer.Serialize(registro, JsonOptions));
            }

            return sb.ToString();
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // --- Texto ------------------------------------------------------------------

        public static string ToText(IEnumerable<ClefEvent> events)
        {
            var sb = new StringBuilder();

            foreach (var e in events)
            {
                sb.Append('[').Append(FormatTimestamp(e.Timestamp)).Append("] ")
                  .Append(e.Level ?? "Information").Append(": ")
                  .AppendLine(e.Message);

                if (!string.IsNullOrEmpty(e.Exception))
                {
                    sb.AppendLine(e.Exception);
                }
            }

            return sb.ToString();
        }

        private static string FormatTimestamp(DateTimeOffset? timestamp) =>
            timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
