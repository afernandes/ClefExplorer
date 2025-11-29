using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClefExplorer.Helpers
{
    public static class TextFormatter
    {
        public static string Format(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var processed = text.Replace("\\r\\n", "\n")
                                .Replace("\\n", "\n")
                                .Replace("\\t", "\t")
                                .Replace("\\\"", "\"");

            processed = Regex.Replace(processed, @"\\u(?<Value>[a-fA-F0-9]{4})", m => {
                try
                {
                    return ((char)int.Parse(m.Groups["Value"].Value, System.Globalization.NumberStyles.HexNumber)).ToString();
                }
                catch
                {
                    return m.Value;
                }
            });

            return FormatJson(processed);
        }

        private static string FormatJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '{' || c == '[')
                {
                    var (endIndex, jsonCandidate) = ExtractJson(text, i);
                    if (jsonCandidate != null && TryFormatJson(jsonCandidate, out var formattedJson))
                    {
                        sb.Append(formattedJson);
                        i = endIndex + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static (int, string?) ExtractJson(string text, int startIndex)
        {
            char startChar = text[startIndex];
            char endChar = startChar == '{' ? '}' : ']';
            int balance = 0;
            bool insideString = false;
            bool escape = false;

            for (int i = startIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (insideString)
                {
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        insideString = false;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        insideString = true;
                    }
                    else if (c == startChar)
                    {
                        balance++;
                    }
                    else if (c == endChar)
                    {
                        balance--;
                        if (balance == 0)
                        {
                            return (i, text.Substring(startIndex, i - startIndex + 1));
                        }
                    }
                }
            }

            return (-1, null);
        }

        private static bool TryFormatJson(string json, out string formatted)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                return true;
            }
            catch
            {
                formatted = string.Empty;
                return false;
            }
        }
    }
}
