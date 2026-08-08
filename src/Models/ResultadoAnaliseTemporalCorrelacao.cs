namespace ClefExplorer.Models
{
    /// <summary>Origem semântica de uma medida apresentada na linha do tempo.</summary>
    public enum TipoMedicaoTemporalCorrelacao
    {
        /// <summary>Diferença real entre <c>@st</c> e <c>@t</c> de um span.</summary>
        DuracaoRealDoSpan,

        /// <summary>
        /// Duração publicada num campo configurado e autodescritivo. É posicionada na
        /// régua, mas permanece distinta dos contratos nativos Seq/OTLP.
        /// </summary>
        DuracaoInformadaPeloProdutor,

        /// <summary>
        /// Distância observada entre dois logs consecutivos. Ajuda a localizar lacunas,
        /// mas não afirma que uma operação permaneceu executando durante todo o período.
        /// </summary>
        IntervaloAteProximoEvento,

        /// <summary>Evento pontual sem um próximo evento a partir do qual inferir intervalo.</summary>
        InstanteDoEvento,
    }

    /// <summary>Evento correlacionado posicionado na escala temporal compartilhada.</summary>
    public sealed record ItemAnaliseTemporalCorrelacao(
        EventoCorrelacionado EventoCorrelacionado,
        DateTimeOffset Inicio,
        DateTimeOffset Fim,
        TipoMedicaoTemporalCorrelacao Tipo,
        MetadadosObservabilidadeEvento Metadados)
    {
        public ClefEvent Evento => EventoCorrelacionado.Evento;
        public TimeSpan Intervalo => Fim - Inicio;
    }

    /// <summary>Resultado pronto para visualização da cadeia de eventos correlacionados.</summary>
    public sealed record ResultadoAnaliseTemporalCorrelacao(
        DateTimeOffset? Inicio,
        DateTimeOffset? Fim,
        IReadOnlyList<ItemAnaliseTemporalCorrelacao> Itens,
        IReadOnlyList<NoHierarquiaSpan> Hierarquia)
    {
        public TimeSpan IntervaloTotal => Inicio is null || Fim is null
            ? TimeSpan.Zero
            : Fim.Value - Inicio.Value;

        public bool TemDuracoesReais => Itens.Any(
            item => item.Tipo == TipoMedicaoTemporalCorrelacao.DuracaoRealDoSpan);

        public bool TemDuracoesInformadas => Itens.Any(
            item => item.Tipo == TipoMedicaoTemporalCorrelacao.DuracaoInformadaPeloProdutor);

        public bool TemIntervalosEstimados => Itens.Any(
            item => item.Tipo == TipoMedicaoTemporalCorrelacao.IntervaloAteProximoEvento);
    }
}
