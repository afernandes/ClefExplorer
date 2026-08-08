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
        /// Identificador W3C do trace. No CLEF ele é transportado pelo campo reservado
        /// <c>@tr</c>; fica fora de <see cref="Properties"/> para não virar uma coluna
        /// dinâmica comum.
        /// </summary>
        public string? TraceId { get; set; }

        /// <summary>
        /// Identificador W3C do span. No CLEF ele é transportado pelo campo reservado
        /// <c>@sp</c>; fica fora de <see cref="Properties"/> pelo mesmo motivo do trace.
        /// </summary>
        public string? SpanId { get; set; }

        /// <summary>
        /// Identificador W3C do span pai. A extensão de tracing do CLEF usa <c>@ps</c>
        /// para preservar a hierarquia sem misturá-la às propriedades da aplicação.
        /// </summary>
        public string? ParentSpanId { get; set; }

        /// <summary>
        /// Início real do span, quando o produtor publica o campo <c>@st</c>. Nesse caso,
        /// <see cref="Timestamp"/> representa o fim do span e a diferença entre ambos é
        /// uma duração observada, não uma estimativa entre logs.
        /// </summary>
        public DateTimeOffset? SpanStart { get; set; }

        /// <summary>
        /// Campos CLEF adicionais de OpenTelemetry/Seq (<c>@sk</c>, <c>@sc</c> e
        /// <c>@ra</c>). Permanece nulo para eventos comuns.
        /// </summary>
        public MetadadosClefObservabilidade? ObservabilidadeClef { get; set; }

        /// <summary>
        /// Propriedades estruturadas do evento. A interface (e não <c>Dictionary</c>)
        /// permite ao parser publicar a forma compacta de <see cref="PropriedadesEvento"/>;
        /// testes e integrações continuam atribuindo um <c>Dictionary</c> comum.
        /// </summary>
        public IReadOnlyDictionary<string, LogEventPropertyValue>? Properties { get; set; }
    }
}
