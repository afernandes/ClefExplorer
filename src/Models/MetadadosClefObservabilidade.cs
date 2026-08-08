using Serilog.Events;

namespace ClefExplorer.Models
{
    /// <summary>
    /// Extensões CLEF de observabilidade que não são propriedades da aplicação. O objeto
    /// é criado somente quando algum desses campos aparece, evitando três referências
    /// adicionais em cada um dos milhões de logs comuns que o aplicativo pode carregar.
    /// </summary>
    public sealed class MetadadosClefObservabilidade
    {
        /// <summary>Kind do span transportado em <c>@sk</c>.</summary>
        public string? TipoSpan { get; set; }

        /// <summary>Escopo de instrumentação transportado em <c>@sc</c>.</summary>
        public LogEventPropertyValue? EscopoInstrumentacao { get; set; }

        /// <summary>Atributos do recurso transportados em <c>@ra</c>.</summary>
        public LogEventPropertyValue? AtributosRecurso { get; set; }
    }
}
