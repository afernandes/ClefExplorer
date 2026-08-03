using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="PropriedadesEvento"/> — a forma compacta que substituiu o
/// <c>Dictionary</c> por evento. O que importa: comportamento IDÊNTICO ao dicionário
/// OrdinalIgnoreCase que ficava ali, com dois arrays no lugar de buckets.
/// </summary>
public class PropriedadesEventoTests
{
    private static LogEventProperty Prop(string nome, object? valor) =>
        new(nome, new ScalarValue(valor));

    [Fact]
    public void Lookup_ignores_case_like_the_dictionary_it_replaced()
    {
        var props = new PropriedadesEvento(new[] { Prop("SourceContext", "Api") });

        Assert.True(props.TryGetValue("sourcecontext", out var valor));
        Assert.Equal("Api", Assert.IsType<ScalarValue>(valor).Value);
        Assert.True(props.ContainsKey("SOURCECONTEXT"));
    }

    [Fact]
    public void A_repeated_key_keeps_the_last_value()
    {
        // Semântica do Dictionary[k] = v em laço: o último vence — inclusive quando a
        // repetição só existe ignorando maiúsculas.
        var props = new PropriedadesEvento(new[]
        {
            Prop("Chave", 1),
            Prop("chave", 2),
        });

        Assert.Single(props);
        Assert.Equal(2, Assert.IsType<ScalarValue>(props["Chave"]).Value);
    }

    [Fact]
    public void Enumeration_preserves_the_file_order()
    {
        var props = new PropriedadesEvento(new[] { Prop("B", 1), Prop("A", 2), Prop("C", 3) });

        Assert.Equal(new[] { "B", "A", "C" }, props.Keys);
        Assert.Equal(3, props.Count);
    }

    [Fact]
    public void A_missing_key_behaves_like_the_dictionary()
    {
        var props = new PropriedadesEvento(new[] { Prop("A", 1) });

        Assert.False(props.TryGetValue("Z", out _));
        Assert.Throws<KeyNotFoundException>(() => props["Z"]);
    }

    [Fact]
    public void Vazio_is_a_single_shared_instance()
    {
        Assert.Same(PropriedadesEvento.Vazio, PropriedadesEvento.Vazio);
        Assert.Empty(PropriedadesEvento.Vazio);
    }

    // ── Pool de escalares (CacheDeTemplates) ────────────────────────────────────

    [Fact]
    public void True_false_and_null_are_process_wide_singletons()
    {
        Assert.Same(CacheDeTemplates.EscalarVerdadeiro, CacheDeTemplates.EscalarVerdadeiro);
        Assert.Equal(true, CacheDeTemplates.EscalarVerdadeiro.Value);
        Assert.Equal(false, CacheDeTemplates.EscalarFalso.Value);
        Assert.Null(CacheDeTemplates.EscalarNulo.Value);
    }

    [Fact]
    public void Small_longs_share_one_instance_and_big_ones_do_not()
    {
        Assert.Same(CacheDeTemplates.EscalarDeNumero(20L), CacheDeTemplates.EscalarDeNumero(20L));
        Assert.NotSame(CacheDeTemplates.EscalarDeNumero(1_000_000L), CacheDeTemplates.EscalarDeNumero(1_000_000L));
        // O VALOR continua exato nos dois casos.
        Assert.Equal(1_000_000L, CacheDeTemplates.EscalarDeNumero(1_000_000L).Value);
    }

    [Fact]
    public void Repeated_string_values_share_one_instance_from_the_second_sighting_on()
    {
        // "VAREJO" aparece em toda linha dos logs reais. A promoção é na SEGUNDA vista:
        // a primeira ocorrência fica avulsa de propósito — inserir tudo no pool encheria
        // ele de GUIDs que nunca repetem e criava contenção entre os workers da carga.
        var cache = new CacheDeTemplates(new PoolDeTextos());

        var primeira = cache.EscalarDe("VAREJO");
        var segunda = cache.EscalarDe("VAREJO");
        var terceira = cache.EscalarDe("VAREJO");

        Assert.NotSame(primeira, segunda);
        Assert.Same(segunda, terceira);
        Assert.Equal("VAREJO", terceira.Value);
    }

    [Fact]
    public void A_huge_string_value_is_not_pooled()
    {
        // Stack traces e payloads não entram: o teto de tamanho protege o pool do que
        // quase nunca repete.
        var cache = new CacheDeTemplates(new PoolDeTextos());
        var grande = new string('x', 4_000);

        Assert.NotSame(cache.EscalarDe(grande), cache.EscalarDe(grande));
    }
}
