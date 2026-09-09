using ClefExplorer.Helpers;

namespace ClefExplorer.Models;

public sealed record FiltroRanking(TipoFiltroRanking Tipo, string Chave)
{
    public bool Corresponde(ClefEvent evento) => string.Equals(Chave, Tipo switch
    {
        TipoFiltroRanking.Mensagem => LogStatistics.MensagemAgrupavel(evento)?.Item1,
        TipoFiltroRanking.Origem => LogStatistics.OrigemDoEvento(evento)?.Item1,
        TipoFiltroRanking.Excecao => LogStatistics.TipoDaExcecao(evento)?.Item1,
        _ => null
    }, StringComparison.OrdinalIgnoreCase);
}
