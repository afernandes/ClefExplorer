using System;

namespace ClefExplorer.Helpers
{
    /// <summary>
    /// Ajustes entre os campos de período da barra lateral e o <c>LogFilterCriteria</c>.
    /// O filtro em si compara instantes exatos; a granularidade do campo (minuto) e a
    /// conveniência de "escolhi só o dia" moram aqui, fora dos componentes Razor, para
    /// poderem ser testadas isoladamente.
    /// </summary>
    public static class PeriodoFiltro
    {
        /// <summary>
        /// Fim do período informado num campo com granularidade de minuto. Os eventos têm
        /// milissegundos: sem esticar o limite até o fim do minuto, "até 18:00" descartaria
        /// um evento das 18:00:42, que o usuário claramente quis incluir.
        /// </summary>
        public static DateTime? FimDoMinuto(DateTime? fim) =>
            fim?.AddMinutes(1).AddTicks(-1);

        /// <summary>
        /// Fim do dia quando o campo "Até" recebe uma data nova sem hora. Clicar num dia no
        /// calendário devolve 00:00, e "até 16/06 00:00" descartaria o dia 16 inteiro — o
        /// oposto do período inclusivo por dia que o filtro tinha antes da hora entrar em
        /// jogo. O 23:59 assumido fica visível no campo, e como a regra só vale quando a
        /// DATA muda, uma edição posterior para 00:00 no mesmo dia é respeitada.
        /// </summary>
        public static DateTime? FimDoDiaAoTrocarDeData(DateTime? novo, DateTime? anterior) =>
            novo is { TimeOfDay.Ticks: 0 } && novo.Value.Date != anterior?.Date
                ? novo.Value.AddHours(23).AddMinutes(59)
                : novo;
    }
}
