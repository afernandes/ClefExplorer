using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

namespace ClefExplorer.Tests;

public class NavegacaoCorrelacaoTests
{
    private readonly NavegacaoCorrelacao _navegacao = new();

    [Fact]
    public void Extrai_trace_span_request_e_aliases_de_correlation_id()
    {
        var contexto = new StructureValue(new[]
        {
            new LogEventProperty("traceid", new ScalarValue("trace-aninhado")),
        });
        var evento = Evento(
            0,
            traceId: "trace-direto",
            spanId: "span-direto",
            ("RequestId", new ScalarValue("req-1")),
            ("X-Correlation-Id", new ScalarValue("corr-1, corr-2")),
            ("CorrelationId", new ScalarValue("corr-3")),
            ("Contexto", contexto));

        var identificadores = _navegacao.ExtrairIdentificadores(evento);

        Assert.Collection(
            identificadores,
            id => Assert.Equal(new IdentificadorCorrelacao("TraceId", "trace-direto"), id),
            id => Assert.Equal(new IdentificadorCorrelacao("SpanId", "span-direto"), id),
            id => Assert.Equal(new IdentificadorCorrelacao("RequestId", "req-1"), id),
            id => Assert.Equal(new IdentificadorCorrelacao("CorrelationId", "corr-1"), id),
            id => Assert.Equal(new IdentificadorCorrelacao("CorrelationId", "corr-2"), id),
            id => Assert.Equal(new IdentificadorCorrelacao("CorrelationId", "corr-3"), id),
            id => Assert.Equal(new IdentificadorCorrelacao("TraceId", "trace-aninhado"), id));
    }

    [Fact]
    public void Localiza_as_quatro_chaves_em_ordem_cronologica()
    {
        var origem = Evento(
            3,
            traceId: "trace-1",
            spanId: "span-1",
            ("RequestId", new ScalarValue("req-1")),
            ("X-Correlation-Id", new ScalarValue("corr-1")));

        var peloTrace = Evento(1, traceId: "TRACE-1");
        var peloSpan = Evento(2, spanId: "SPAN-1");
        var peloRequest = Evento(4, ("requestid", new ScalarValue("REQ-1")));
        var peloCorrelation = Evento(
            5,
            ("Contexto", new StructureValue(new[]
            {
                // Outro serviço usa CorrelationId sem o prefixo HTTP; os dois aliases
                // representam o mesmo identificador lógico.
                new LogEventProperty("correlationid", new ScalarValue("CORR-1")),
            })));
        var mesmoTextoEmOutroCampo = Evento(0, ("RequestId", new ScalarValue("trace-1")));
        var semRelacao = Evento(6, traceId: "trace-2");

        var resultado = _navegacao.Localizar(
            origem,
            new[] { semRelacao, peloCorrelation, origem, peloRequest, mesmoTextoEmOutroCampo, peloSpan, peloTrace });

        Assert.Equal(4, resultado.QuantidadeRelacionada);
        Assert.Equal(
            new[] { peloTrace, peloSpan, origem, peloRequest, peloCorrelation },
            resultado.Eventos.Select(item => item.Evento));
        Assert.DoesNotContain(resultado.Eventos, item => ReferenceEquals(item.Evento, mesmoTextoEmOutroCampo));
    }

    [Fact]
    public void Campo_personalizado_e_equivalente_aos_aliases_padrao()
    {
        var navegacao = new NavegacaoCorrelacao(new ConfiguracaoCorrelacao
        {
            Campos = ["X-Correlation-Id", "CorrelationId", "IdDaOperacao"],
        });
        var origem = Evento(1, ("IdDaOperacao", new ScalarValue("corr-42")));
        var relacionado = Evento(2, ("X-Correlation-Id", new ScalarValue("CORR-42")));

        var resultado = navegacao.Localizar(origem, new[] { origem, relacionado });

        Assert.True(navegacao.EhCampoCorrelacao("iddaoperacao"));
        Assert.Equal(new[] { origem, relacionado }, resultado.Eventos.Select(item => item.Evento));
        Assert.All(
            resultado.Eventos.SelectMany(item => item.Correspondencias),
            identificador => Assert.Equal("CorrelationId", identificador.Campo));
    }

    [Fact]
    public void Apenas_o_cabecalho_x_correlation_id_e_separado_por_virgula()
    {
        var evento = Evento(
            0,
            ("X-Correlation-Id", new ScalarValue("x-1, x-2")),
            ("CorrelationId", new ScalarValue("correlation,com,virgulas")));

        var identificadores = _navegacao.ExtrairIdentificadores(evento);

        Assert.Equal(
            new[] { "x-1", "x-2", "correlation,com,virgulas" },
            identificadores.Select(item => item.Valor));
    }

    [Fact]
    public void Remover_alias_nao_desativa_trace_span_e_request_id()
    {
        var navegacao = new NavegacaoCorrelacao(new ConfiguracaoCorrelacao { Campos = [] });
        var evento = Evento(
            0,
            traceId: "trace-1",
            spanId: "span-1",
            ("RequestId", new ScalarValue("req-1")),
            ("CorrelationId", new ScalarValue("corr-1")));

        var identificadores = navegacao.ExtrairIdentificadores(evento);

        Assert.Equal(3, identificadores.Count);
        Assert.DoesNotContain(identificadores, item => item.Campo == "CorrelationId");
        Assert.False(navegacao.EhCampoCorrelacao("CorrelationId"));
    }

    [Fact]
    public void Mantem_a_origem_quando_a_amostra_nao_a_contem()
    {
        var origem = Evento(2, ("RequestId", new ScalarValue("req-1")));
        var relacionado = Evento(1, ("RequestId", new ScalarValue("req-1")));

        var resultado = _navegacao.Localizar(origem, new[] { relacionado });

        Assert.Equal(new[] { relacionado, origem }, resultado.Eventos.Select(item => item.Evento));
    }

    [Fact]
    public void Evento_sem_identificador_nao_oferece_navegacao()
    {
        var evento = Evento(0, ("SourceContext", new ScalarValue("Api.Pedidos")));

        Assert.False(_navegacao.PodeNavegar(evento));
        Assert.Empty(_navegacao.Localizar(evento, new[] { evento }).Eventos);
    }

    [Fact]
    public void Respeita_cancelamento_durante_a_varredura()
    {
        var origem = Evento(0, traceId: "trace-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _navegacao.Localizar(origem, new[] { origem }, cts.Token));
    }

    private static ClefEvent Evento(
        int minuto,
        params (string Nome, LogEventPropertyValue Valor)[] propriedades) =>
        Evento(minuto, null, null, propriedades);

    private static ClefEvent Evento(
        int minuto,
        string? traceId = null,
        string? spanId = null,
        params (string Nome, LogEventPropertyValue Valor)[] propriedades) => new()
        {
            Timestamp = new DateTimeOffset(2026, 8, 5, 12, minuto, 0, TimeSpan.Zero),
            Level = "Information",
            Message = $"Evento {minuto}",
            TraceId = traceId,
            SpanId = spanId,
            Properties = propriedades.ToDictionary(
                propriedade => propriedade.Nome,
                propriedade => propriedade.Valor,
                StringComparer.OrdinalIgnoreCase),
        };
}
