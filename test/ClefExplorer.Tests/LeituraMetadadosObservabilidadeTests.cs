using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

namespace ClefExplorer.Tests;

public class LeituraMetadadosObservabilidadeTests
{
    private readonly LeituraMetadadosObservabilidade _leitura = new();

    [Fact]
    public void Le_extensoes_nativas_do_Seq_e_atributos_de_recurso()
    {
        var evento = Evento();
        evento.TraceId = "0af7651916cd43dd8448eb211c80319c";
        evento.SpanId = "b7ad6b7169203331";
        evento.SpanStart = evento.Timestamp!.Value - TimeSpan.FromMilliseconds(125);
        evento.MessageTemplate = "GET /pedidos";
        evento.ObservabilidadeClef = new MetadadosClefObservabilidade
        {
            TipoSpan = "Server",
            AtributosRecurso = Estrutura(("service.name", "pedidos-api")),
        };

        var metadados = _leitura.Extrair(evento);

        Assert.True(metadados.EhSpan);
        Assert.Equal(OrigemDuracaoObservabilidade.SeqClef, metadados.OrigemDuracao);
        Assert.Equal("GET /pedidos", metadados.NomeOperacao);
        Assert.Equal("pedidos-api", metadados.NomeServico);
        Assert.Equal("Server", metadados.TipoSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(125), metadados.Duracao);
    }

    [Fact]
    public void Le_span_no_formato_json_do_OTLP_sem_perder_precisao_de_ticks()
    {
        var evento = Evento();
        evento.Properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["traceId"] = new ScalarValue("0af7651916cd43dd8448eb211c80319c"),
            ["spanId"] = new ScalarValue("b7ad6b7169203331"),
            ["parentSpanId"] = new ScalarValue("00f067aa0ba902b7"),
            ["name"] = new ScalarValue("SELECT pedidos"),
            ["kind"] = new ScalarValue(3L),
            ["startTimeUnixNano"] = new ScalarValue("1000000123"),
            ["endTimeUnixNano"] = new ScalarValue("1250000987"),
            ["resource"] = Estrutura(("service.name", "pedidos-db")),
        };

        var metadados = _leitura.Extrair(evento);

        Assert.True(metadados.EhSpan);
        Assert.Equal(OrigemDuracaoObservabilidade.OpenTelemetryOtlp, metadados.OrigemDuracao);
        Assert.Equal("SELECT pedidos", metadados.NomeOperacao);
        Assert.Equal("Client", metadados.TipoSpan);
        Assert.Equal("pedidos-db", metadados.NomeServico);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(10_000_001), metadados.Inicio);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(12_500_009), metadados.Fim);
    }

    [Fact]
    public void Usa_aliases_configurados_para_logs_legados()
    {
        var evento = Evento();
        evento.SpanId = "b7ad6b7169203331";
        evento.Properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["MinhaOperacao"] = new ScalarValue("Processar pedido"),
            ["Aplicacao"] = new ScalarValue("worker-pedidos"),
            ["ClasseSpan"] = new ScalarValue("Consumer"),
            ["TempoTotal"] = new ScalarValue("250 ms"),
        };
        var configuracao = new ConfiguracaoObservabilidade
        {
            CamposNomeOperacao = ["MinhaOperacao"],
            CamposNomeServico = ["Aplicacao"],
            CamposTipoSpan = ["ClasseSpan"],
            CamposDuracao = ["TempoTotal"],
        };

        var metadados = _leitura.Extrair(evento, configuracao);

        Assert.True(metadados.EhSpan);
        Assert.Equal(OrigemDuracaoObservabilidade.CampoConfigurado, metadados.OrigemDuracao);
        Assert.Equal("Processar pedido", metadados.NomeOperacao);
        Assert.Equal("worker-pedidos", metadados.NomeServico);
        Assert.Equal("Consumer", metadados.TipoSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(250), metadados.Duracao);
    }

    [Fact]
    public void Nao_adivinha_a_unidade_de_uma_duracao_numerica()
    {
        var evento = Evento();
        evento.SpanId = "b7ad6b7169203331";
        evento.Properties = new Dictionary<string, LogEventPropertyValue>
        {
            ["OperationName"] = new ScalarValue("Processar pedido"),
            ["Elapsed"] = new ScalarValue(250L),
        };

        var metadados = _leitura.Extrair(evento);

        Assert.False(metadados.EhSpan);
        Assert.Equal(OrigemDuracaoObservabilidade.Nenhuma, metadados.OrigemDuracao);
    }

    private static ClefEvent Evento() => new()
    {
        Timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
        Level = "Information",
        Message = "evento",
    };

    private static StructureValue Estrutura(params (string Nome, string Valor)[] propriedades) =>
        new(propriedades.Select(propriedade =>
            new LogEventProperty(propriedade.Nome, new ScalarValue(propriedade.Valor))));
}
