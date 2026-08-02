using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

public class ConsultaLogsTests
{
    private static ClefEvent Evento(string mensagem) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = "Information",
        Message = mensagem,
    };

    [Fact]
    public async Task Apenas_a_geracao_mais_recente_permanece_atual()
    {
        using var consulta = new ConsultaLogs();
        var criterios = new LogFilterCriteria { InputAlreadySorted = true };

        var primeiro = await consulta.ExecutarAsync(new[] { Evento("primeiro") }, criterios);
        var segundo = await consulta.ExecutarAsync(new[] { Evento("segundo") }, criterios);

        Assert.NotNull(primeiro);
        Assert.NotNull(segundo);
        Assert.False(consulta.EstaAtual(primeiro.Geracao));
        Assert.True(consulta.EstaAtual(segundo.Geracao));
        Assert.Equal("segundo", Assert.Single(segundo.Eventos).Message);
    }

    [Fact]
    public async Task Cancelar_invalida_um_resultado_ja_calculado()
    {
        using var consulta = new ConsultaLogs();
        var resultado = await consulta.ExecutarAsync(
            new[] { Evento("resultado") },
            new LogFilterCriteria { InputAlreadySorted = true });

        consulta.CancelarConsultaAtual();

        Assert.NotNull(resultado);
        Assert.False(consulta.EstaAtual(resultado.Geracao));
    }
}
