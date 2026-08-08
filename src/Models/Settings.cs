using System.Collections.Generic;

namespace ClefExplorer.Models
{
    public class Settings
    {
        public List<string> IgnoredFilePatterns { get; set; } = new();
        public List<string> IgnoredLogLines { get; set; } = new();
        public ConfiguracaoCorrelacao Correlacao { get; set; } = new();
        public ConfiguracaoObservabilidade Observabilidade { get; set; } = new();

        public void Normalizar()
        {
            IgnoredFilePatterns ??= new();
            IgnoredLogLines ??= new();
            Correlacao ??= new();
            Observabilidade ??= new();
            Correlacao.Normalizar();
            Observabilidade.Normalizar();
        }
    }

    /// <summary>
    /// Nomes de propriedades que representam o mesmo identificador lógico de correlação.
    /// TraceId, SpanId e RequestId são contratos próprios e continuam reconhecidos
    /// independentemente desta lista.
    /// </summary>
    public sealed class ConfiguracaoCorrelacao
    {
        public List<string> Campos { get; set; } =
        [
            "X-Correlation-Id",
            "CorrelationId",
        ];

        public void Normalizar() => Campos = NormalizarCampos(Campos);

        private static List<string> NormalizarCampos(List<string>? campos) => (campos ?? new())
            .Where(campo => !string.IsNullOrWhiteSpace(campo))
            .Select(campo => campo.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Aliases usados para interpretar propriedades estruturadas de tracing sem impor
    /// nomes específicos da aplicação aos arquivos do usuário.
    /// </summary>
    public sealed class ConfiguracaoObservabilidade
    {
        public List<string> CamposNomeOperacao { get; set; } =
        [
            "OperationName",
            "SpanName",
            "ActivityName",
            "otel.span.name",
        ];

        public List<string> CamposNomeServico { get; set; } =
        [
            "ServiceName",
            "Application",
            "service.name",
            "otel.service.name",
            "Resource.service.name",
            "@Resource.service.name",
        ];

        public List<string> CamposTipoSpan { get; set; } =
        [
            "ActivityKind",
            "SpanKind",
            "otel.span.kind",
        ];

        public List<string> CamposDuracao { get; set; } =
        [
            "Duration",
            "Elapsed",
        ];

        public void Normalizar()
        {
            CamposNomeOperacao = Normalizar(CamposNomeOperacao);
            CamposNomeServico = Normalizar(CamposNomeServico);
            CamposTipoSpan = Normalizar(CamposTipoSpan);
            CamposDuracao = Normalizar(CamposDuracao);
        }

        private static List<string> Normalizar(List<string>? campos) => (campos ?? new())
            .Where(campo => !string.IsNullOrWhiteSpace(campo))
            .Select(campo => campo.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
