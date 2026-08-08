namespace ClefExplorer.Models
{
    /// <summary>Fonte que sustenta a duração apresentada para um span.</summary>
    public enum OrigemDuracaoObservabilidade
    {
        Nenhuma,
        SeqClef,
        OpenTelemetryOtlp,
        CampoConfigurado,
    }

    /// <summary>
    /// Interpretação normalizada dos campos de tracing de um evento, sem alterar as
    /// propriedades originais que continuam disponíveis no painel de detalhes.
    /// </summary>
    public sealed record MetadadosObservabilidadeEvento(
        string? TraceId,
        string? SpanId,
        string? ParentSpanId,
        string NomeOperacao,
        string? NomeServico,
        string? TipoSpan,
        DateTimeOffset? Inicio,
        DateTimeOffset? Fim,
        OrigemDuracaoObservabilidade OrigemDuracao,
        string? CampoDuracao)
    {
        public bool EhSpan => OrigemDuracao != OrigemDuracaoObservabilidade.Nenhuma
            && Inicio is not null
            && Fim is not null
            && Inicio <= Fim;

        public TimeSpan? Duracao => EhSpan ? Fim - Inicio : null;
    }
}
