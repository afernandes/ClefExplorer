using System;
using System.Collections.Generic;
using Serilog.Events;

namespace ClefExplorer.Models
{
    public class ClefEvent
    {
        public DateTimeOffset? Timestamp { get; set; }
        public string? Level { get; set; }
        public string? Message { get; set; }
        public string? MessageTemplate { get; set; }
        public string? Exception { get; set; }
        public string? SourceFile { get; set; }
        public Dictionary<string, LogEventPropertyValue>? Properties { get; set; }
    }
}
