namespace ClefExplorer.Models
{
    /// <summary>
    /// Nó imutável da árvore do trace. Spans formam os ramos; logs que carregam o mesmo
    /// SpanId ficam associados ao span correspondente como folhas selecionáveis.
    /// </summary>
    public sealed record NoHierarquiaSpan(
        ItemAnaliseTemporalCorrelacao Item,
        IReadOnlyList<NoHierarquiaSpan> Filhos)
    {
        public bool EhSpan => Item.Metadados.EhSpan;
        public string? SpanId => Item.Metadados.SpanId;
    }
}
