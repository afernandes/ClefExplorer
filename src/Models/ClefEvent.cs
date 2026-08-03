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
        /// <summary>
        /// Propriedades estruturadas do evento. A interface (e não <c>Dictionary</c>)
        /// permite ao parser publicar a forma compacta de <see cref="PropriedadesEvento"/>;
        /// testes e integrações continuam atribuindo um <c>Dictionary</c> comum.
        /// </summary>
        public IReadOnlyDictionary<string, LogEventPropertyValue>? Properties { get; set; }
    }
}
