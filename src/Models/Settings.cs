using System.Collections.Generic;

namespace ClefExplorer.Models
{
    public class Settings
    {
        public List<string> IgnoredFilePatterns { get; set; } = new();
        public List<string> IgnoredLogLines { get; set; } = new();
    }
}
