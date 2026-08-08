using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

public class AnaliseTemporalCorrelacaoTests
{
    private readonly AnaliseTemporalCorrelacao _analise = new();

    [Fact]
    public void Usa_inicio_e_fim_reais_quando_o_evento_e_um_span()
    {
        var span = Evento(3, inicioDoSpan: 1);

        var resultado = _analise.Analisar(Resultado(span));

        var item = Assert.Single(resultado.Itens);
        Assert.Equal(TipoMedicaoTemporalCorrelacao.DuracaoRealDoSpan, item.Tipo);
        Assert.Equal(TimeSpan.FromSeconds(2), item.Intervalo);
        Assert.Equal(span.SpanStart, resultado.Inicio);
        Assert.Equal(span.Timestamp, resultado.Fim);
        Assert.True(resultado.TemDuracoesReais);
        Assert.False(resultado.TemIntervalosEstimados);
    }

    [Fact]
    public void Log_comum_mede_apenas_o_intervalo_ate_o_proximo_evento()
    {
        var primeiro = Evento(1);
        var segundo = Evento(4);

        var resultado = _analise.Analisar(Resultado(primeiro, segundo));

        Assert.Equal(TipoMedicaoTemporalCorrelacao.IntervaloAteProximoEvento, resultado.Itens[0].Tipo);
        Assert.Equal(TimeSpan.FromSeconds(3), resultado.Itens[0].Intervalo);
        Assert.Equal(TipoMedicaoTemporalCorrelacao.InstanteDoEvento, resultado.Itens[1].Tipo);
        Assert.Equal(TimeSpan.Zero, resultado.Itens[1].Intervalo);
        Assert.True(resultado.TemIntervalosEstimados);
        Assert.False(resultado.TemDuracoesReais);
    }

    [Fact]
    public void Ordena_eventos_e_ignora_os_que_nao_tem_instante()
    {
        var ultimo = Evento(5);
        var semInstante = Evento(3);
        semInstante.Timestamp = null;
        var primeiro = Evento(1);

        var resultado = _analise.Analisar(Resultado(ultimo, semInstante, primeiro));

        Assert.Equal(new[] { primeiro, ultimo }, resultado.Itens.Select(item => item.Evento));
        Assert.Equal(TimeSpan.FromSeconds(4), resultado.IntervaloTotal);
    }

    [Fact]
    public void Inicio_de_span_invalido_nao_e_apresentado_como_duracao_real()
    {
        var invalido = Evento(1, inicioDoSpan: 2);
        var proximo = Evento(3);

        var resultado = _analise.Analisar(Resultado(invalido, proximo));

        Assert.Equal(TipoMedicaoTemporalCorrelacao.IntervaloAteProximoEvento, resultado.Itens[0].Tipo);
        Assert.Equal(TimeSpan.FromSeconds(2), resultado.Itens[0].Intervalo);
    }

    [Fact]
    public void Mantem_ordem_original_quando_os_instantes_sao_iguais()
    {
        var primeiro = Evento(1);
        var segundo = Evento(1);

        var resultado = _analise.Analisar(Resultado(primeiro, segundo));

        Assert.Equal(new[] { primeiro, segundo }, resultado.Itens.Select(item => item.Evento));
    }

    [Fact]
    public void Monta_arvore_de_spans_e_anexa_logs_ao_span_atual()
    {
        var raiz = Evento(5, inicioDoSpan: 0);
        raiz.MessageTemplate = "POST /pedidos";
        raiz.SpanId = "1111111111111111";

        var filho = Evento(3, inicioDoSpan: 1);
        filho.MessageTemplate = "INSERT pedidos";
        filho.SpanId = "2222222222222222";
        filho.ParentSpanId = raiz.SpanId;

        var logDoFilho = Evento(2);
        logDoFilho.Message = "Executando comando";
        logDoFilho.SpanId = filho.SpanId;

        var resultado = _analise.Analisar(Resultado(raiz, filho, logDoFilho));

        var noRaiz = Assert.Single(resultado.Hierarquia);
        Assert.Same(raiz, noRaiz.Item.Evento);
        var noFilho = Assert.Single(noRaiz.Filhos);
        Assert.Same(filho, noFilho.Item.Evento);
        Assert.Same(logDoFilho, Assert.Single(noFilho.Filhos).Item.Evento);
    }

    [Fact]
    public void Mantem_spans_orfaos_como_raizes()
    {
        var orfao = Evento(3, inicioDoSpan: 1);
        orfao.SpanId = "2222222222222222";
        orfao.ParentSpanId = "9999999999999999";

        var resultado = _analise.Analisar(Resultado(orfao));

        Assert.Same(orfao, Assert.Single(resultado.Hierarquia).Item.Evento);
    }

    [Fact]
    public void Ciclo_em_parent_span_id_nao_remove_eventos_nem_recursa_indefinidamente()
    {
        var primeiro = Evento(3, inicioDoSpan: 1);
        primeiro.SpanId = "1111111111111111";
        primeiro.ParentSpanId = "2222222222222222";
        var segundo = Evento(4, inicioDoSpan: 2);
        segundo.SpanId = "2222222222222222";
        segundo.ParentSpanId = "1111111111111111";

        var resultado = _analise.Analisar(Resultado(primeiro, segundo));

        static int Contar(IEnumerable<NoHierarquiaSpan> nos) =>
            nos.Sum(no => 1 + Contar(no.Filhos));
        Assert.Equal(2, Contar(resultado.Hierarquia));
    }

    private static ResultadoNavegacaoCorrelacao Resultado(params ClefEvent[] eventos)
    {
        var correlacionados = eventos
            .Select(evento => new EventoCorrelacionado(
                evento,
                new[] { new IdentificadorCorrelacao("TraceId", "trace") }))
            .ToArray();

        return new ResultadoNavegacaoCorrelacao(
            eventos[0],
            new[] { new IdentificadorCorrelacao("TraceId", "trace") },
            correlacionados);
    }

    private static ClefEvent Evento(int segundo, int? inicioDoSpan = null) => new()
    {
        Timestamp = Instante(segundo),
        SpanStart = inicioDoSpan is null ? null : Instante(inicioDoSpan.Value),
        Level = "Information",
        Message = $"evento {segundo}",
    };

    private static DateTimeOffset Instante(int segundo) =>
        new(2026, 8, 5, 10, 0, segundo, TimeSpan.Zero);
}
