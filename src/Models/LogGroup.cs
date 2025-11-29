using System.Collections.Generic;

namespace ClefExplorer.Models
{
    public class LogGroup
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public List<string> Paths { get; set; } = new();
    }
}
