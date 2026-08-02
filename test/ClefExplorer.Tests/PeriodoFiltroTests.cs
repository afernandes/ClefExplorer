using ClefExplorer.Helpers;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="PeriodoFiltro"/> — a ponte entre os campos "De"/"Até" da barra
/// lateral, que trabalham em minutos, e o <c>LogFilter</c>, que compara instantes exatos.
/// </summary>
public class PeriodoFiltroTests
{
    // --- Fim do minuto ----------------------------------------------------------

    [Fact]
    public void FimDoMinuto_covers_the_whole_minute_typed_in_the_field()
    {
        var fim = PeriodoFiltro.FimDoMinuto(new DateTime(2026, 6, 15, 18, 0, 0));

        // Um evento das 18:00:42.123 precisa continuar dentro de "até 18:00".
        Assert.Equal(new DateTime(2026, 6, 15, 18, 0, 59, 999).AddTicks(9999), fim);
    }

    [Fact]
    public void FimDoMinuto_keeps_null_as_no_upper_bound()
    {
        Assert.Null(PeriodoFiltro.FimDoMinuto(null));
    }

    // --- Fim do dia -------------------------------------------------------------

    [Fact]
    public void A_date_picked_without_time_assumes_the_end_of_the_day()
    {
        // Sem isto, escolher 16/06 no calendário filtraria "até 16/06 00:00" e o dia
        // inteiro sumiria do resultado.
        var ajustado = PeriodoFiltro.FimDoDiaAoTrocarDeData(new DateTime(2026, 6, 16), anterior: null);

        Assert.Equal(new DateTime(2026, 6, 16, 23, 59, 0), ajustado);
    }

    [Fact]
    public void A_time_typed_by_the_user_is_preserved()
    {
        var informado = new DateTime(2026, 6, 16, 18, 30, 0);

        var ajustado = PeriodoFiltro.FimDoDiaAoTrocarDeData(informado, anterior: null);

        Assert.Equal(informado, ajustado);
    }

    [Fact]
    public void Midnight_is_respected_when_only_the_time_changes()
    {
        // A regra vale só na troca de DATA: quem voltar o horário para 00:00 no mesmo dia
        // está pedindo a meia-noite, e não o fim do dia de novo.
        var ajustado = PeriodoFiltro.FimDoDiaAoTrocarDeData(
            new DateTime(2026, 6, 16, 0, 0, 0),
            anterior: new DateTime(2026, 6, 16, 23, 59, 0));

        Assert.Equal(new DateTime(2026, 6, 16, 0, 0, 0), ajustado);
    }

    [Fact]
    public void Clearing_the_field_stays_cleared()
    {
        Assert.Null(PeriodoFiltro.FimDoDiaAoTrocarDeData(null, anterior: new DateTime(2026, 6, 16, 23, 59, 0)));
    }
}
