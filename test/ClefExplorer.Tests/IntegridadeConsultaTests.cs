using System.Numerics;
using System.Text.Json;
using ClefExplorer.Helpers;
using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

namespace ClefExplorer.Tests;

public sealed class IntegridadeConsultaTests
{
    private static ClefEvent Ler(string atributos)
    {
        Assert.True(LeitorClef.TentarLer("{\"@t\":\"2026-09-09T12:00:00Z\"," + atributos + "}",
            "origem.clef", new CacheDeTemplates(), out var evento, out var erro), erro);
        return evento!;
    }

    [Theory]
    [InlineData(TipoFiltroRanking.Mensagem)]
    [InlineData(TipoFiltroRanking.Origem)]
    [InlineData(TipoFiltroRanking.Excecao)]
    public void Apply_FiltroDoRanking_CorrespondeExatamenteAContagem(TipoFiltroRanking tipo)
    {
        var evento = Ler("\"@mt\":\"Pedido {Id} pronto\",\"Id\":123,\"@x\":\"Tipo: erro\\n pilha\"");
        var stats = LogStatistics.Compute([evento]);
        var ranking = tipo switch
        {
            TipoFiltroRanking.Mensagem => stats.TopMessages.Single(),
            TipoFiltroRanking.Origem => stats.TopSources.Single(),
            _ => stats.TopExceptions.Single()
        };
        var encontrados = LogFilter.Apply([evento], new LogFilterCriteria { Ranking = new(tipo, ranking.Key) });
        Assert.Equal(ranking.Count, encontrados.Count);
    }

    [Fact]
    public void Apply_FiltrosDeColuna_ExportacaoUsaMesmoConjunto()
    {
        var a = Ler("\"@mt\":\"Pedido {Id}\",\"Id\":123");
        var b = Ler("\"@mt\":\"Pedido {Id}\",\"Id\":456");
        var consulta = LogFilter.Apply([a, b], new LogFilterCriteria
        {
            FiltrosColuna = [new("Id", "456", ColumnValueKind.Number)]
        });
        Assert.Same(b, Assert.Single(consulta));
        Assert.Single(LogExporter.ToClef(consulta).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(1, LogStatistics.Compute(consulta).Total);
    }

    [Fact]
    public void GetSortValue_InteirosLongosDiferentes_PreservaOrdemEGrupos()
    {
        ClefEvent Evento(object numero) => new() { Properties = new Dictionary<string, LogEventPropertyValue> { ["Id"] = new ScalarValue(numero) } };
        var a = LogColumnDiscovery.GetSortValue(Evento(9007199254740992L), "Id", ColumnValueKind.Number);
        var b = LogColumnDiscovery.GetSortValue(Evento(9007199254740993L), "Id", ColumnValueKind.Number);
        Assert.NotEqual(a, b);
        Assert.True(((IComparable)a!).CompareTo(b) < 0);
        Assert.Equal(2, new[] { a, b }.Distinct().Count());
        Assert.True(NumeroLog.Criar(BigInteger.Pow(10, 40))!.CompareTo(NumeroLog.Criar(decimal.MaxValue)) > 0);
        Assert.Equal(NumeroLog.Criar(12.5m), NumeroLog.Criar("1.25e1"));
    }

    [Theory]
    [InlineData("\"@i\":\"reservado\"", "reservado", null)]
    [InlineData("\"@@i\":\"usuario\"", null, "usuario")]
    [InlineData("\"@i\":\"reservado\",\"@@i\":\"usuario\"", "reservado", "usuario")]
    public void ToClef_IdentificadorReservadoEPropriedadeEscapada_PreservaAmbos(string entrada, string? reservado, string? usuario)
    {
        var evento = Ler(entrada);
        using var json = JsonDocument.Parse(LogExporter.ToClef([evento]));
        Assert.Equal(reservado is not null, json.RootElement.TryGetProperty("@i", out var id));
        if (reservado is not null) Assert.Equal(reservado, id.GetString());
        Assert.Equal(usuario is not null, json.RootElement.TryGetProperty("@@i", out var propriedade));
        if (usuario is not null) Assert.Equal(usuario, propriedade.GetString());
    }

    [Fact]
    public void TentarLer_MensagensProntasUnicas_NaoRetemTemplatesCompilados()
    {
        var pool = new PoolDeTextos();
        var cache = CacheDeTemplates.Para(pool);
        for (var i = 0; i < 5000; i++)
            Assert.True(LeitorClef.TentarLer($"{{\"@t\":\"2026-09-09T12:00:00Z\",\"@m\":\"mensagem {i}\"}}",
                "dados.clef", cache, out _, out _));
        Assert.Equal(0, cache.QuantidadeTemplates);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void TentarLer_TemplatesUnicos_LimitaCacheSemPerderEventos()
    {
        var cache = new CacheDeTemplates();
        for (var i = 0; i < 5000; i++)
        {
            Assert.True(LeitorClef.TentarLer($"{{\"@t\":\"2026-09-09T12:00:00Z\",\"@mt\":\"mensagem {i}\"}}",
                "dados.clef", cache, out var evento, out _));
            Assert.Equal($"mensagem {i}", evento!.Message);
        }
        Assert.Equal(4096, cache.QuantidadeTemplates);
    }
}
