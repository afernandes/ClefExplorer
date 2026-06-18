using Microsoft.AspNetCore.Components;
using System.Text.RegularExpressions;
using System.Net;
using System;

namespace ClefExplorer.Helpers
{
    public static class StackTraceHighlighter
    {
        public static MarkupString Highlight(string? text)
        {
            if (string.IsNullOrEmpty(text)) return new MarkupString(string.Empty);

            // 1. Format (unescape)
            var formatted = TextFormatter.Format(text);

            var lines = formatted.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var indent = line.Substring(0, line.Length - trimmed.Length);
                
                if (trimmed.StartsWith("at "))
                {
                    // Parse: at Namespace.Class.Method(Args) in File:line 123
                    
                    // 1. Extract File Info
                    string filePart = "";
                    string methodPart = trimmed;
                    
                    var fileMatch = Regex.Match(trimmed, @"(\s+in\s+)(.+?)(:line\s+)(\d+)(\s*)$");
                    if (fileMatch.Success)
                    {
                        methodPart = trimmed.Substring(0, fileMatch.Index);
                        
                        var filePrefix = WebUtility.HtmlEncode(fileMatch.Groups[1].Value);
                        var filePath = WebUtility.HtmlEncode(fileMatch.Groups[2].Value);
                        var linePrefix = WebUtility.HtmlEncode(fileMatch.Groups[3].Value);
                        var lineNumber = WebUtility.HtmlEncode(fileMatch.Groups[4].Value);
                        var suffix = WebUtility.HtmlEncode(fileMatch.Groups[5].Value);

                        filePart = $"<span class=\"clef-st-dim\">{filePrefix}</span>" +
                                   $"<span class=\"clef-st-file\" title=\"{filePath}\">{System.IO.Path.GetFileName(fileMatch.Groups[2].Value)}</span>" +
                                   $"<span class=\"clef-st-dim\">{linePrefix}</span>" +
                                   $"<span class=\"clef-st-line\">{lineNumber}</span>" +
                                   suffix;
                    }

                    // 2. Parse Method Signature
                    // Expected: "at Namespace.Class.Method(Args)"
                    var methodMatch = Regex.Match(methodPart, @"^(at\s+)([\w\.<>`\[\]\+]+)(\(.*\))$");
                    string styledMethod;
                    
                    if (methodMatch.Success)
                    {
                        var atPrefix = WebUtility.HtmlEncode(methodMatch.Groups[1].Value);
                        var fullPath = methodMatch.Groups[2].Value;
                        var args = WebUtility.HtmlEncode(methodMatch.Groups[3].Value);

                        var lastDot = fullPath.LastIndexOf('.');
                        string nsClass = "";
                        string method = fullPath;

                        if (lastDot > 0)
                        {
                            nsClass = WebUtility.HtmlEncode(fullPath.Substring(0, lastDot + 1));
                            method = WebUtility.HtmlEncode(fullPath.Substring(lastDot + 1));
                        }
                        else
                        {
                            method = WebUtility.HtmlEncode(fullPath);
                        }

                        styledMethod = $"<span class=\"clef-st-dim\">{atPrefix}</span>" +
                                       $"<span class=\"clef-st-dim\">{nsClass}</span>" +
                                       $"<span class=\"clef-st-method\">{method}</span>" +
                                       $"<span class=\"clef-st-dim\">{args}</span>";
                    }
                    else
                    {
                        styledMethod = WebUtility.HtmlEncode(methodPart);
                    }

                    // 3. Check for System/Microsoft to dim
                    var isSystem = methodPart.Contains("System.") || methodPart.Contains("Microsoft.");
                    var opacityClass = isSystem ? "clef-st-system" : "";

                    lines[i] = $"<div class=\"{opacityClass}\">{WebUtility.HtmlEncode(indent)}{styledMethod}{filePart}</div>";
                }
                else if (trimmed.StartsWith("---") && trimmed.EndsWith("---"))
                {
                    lines[i] = $"<div class=\"clef-st-sep\">{WebUtility.HtmlEncode(line)}</div>";
                }
                else
                {
                    lines[i] = $"<div>{WebUtility.HtmlEncode(line)}</div>";
                }
            }

            return new MarkupString(string.Join("", lines));
        }
    }
}
