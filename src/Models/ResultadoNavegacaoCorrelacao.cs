namespace ClefExplorer.Models
{
    /// <summary>Um campo e valor capazes de relacionar eventos de log.</summary>
    public sealed record IdentificadorCorrelacao(string Campo, string Valor);

    /// <summary>
    /// Evento encontrado e os identificadores da origem que justificam sua presença na
    /// sequência de correlação.
    /// </summary>
    public sealed record EventoCorrelacionado(
        ClefEvent Evento,
        IReadOnlyList<IdentificadorCorrelacao> Correspondencias);

    /// <summary>Sequência cronológica relacionada diretamente ao evento de origem.</summary>
    public sealed record ResultadoNavegacaoCorrelacao(
        ClefEvent Origem,
        IReadOnlyList<IdentificadorCorrelacao> Identificadores,
        IReadOnlyList<EventoCorrelacionado> Eventos)
    {
        public int QuantidadeRelacionada => Math.Max(0, Eventos.Count - 1);
    }
}
