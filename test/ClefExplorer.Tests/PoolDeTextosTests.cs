using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="PoolDeTextos"/> — a deduplicação das strings que se repetem em
/// todo evento. O que importa é a IDENTIDADE das instâncias: igualdade já existia, o que
/// faltava era compartilhar a mesma referência.
/// </summary>
public class PoolDeTextosTests
{
    [Fact]
    public void The_same_text_always_yields_the_same_instance()
    {
        var pool = new PoolDeTextos();
        // Instâncias distintas com o mesmo conteúdo — como o parser produz a cada linha.
        var primeira = new string("SourceContext".ToCharArray());
        var segunda = new string("SourceContext".ToCharArray());
        Assert.False(ReferenceEquals(primeira, segunda));

        Assert.Same(pool.Compartilhar(primeira), pool.Compartilhar(segunda));
    }

    [Fact]
    public void Different_texts_stay_separate()
    {
        var pool = new PoolDeTextos();

        Assert.NotSame(pool.Compartilhar("Information"), pool.Compartilhar("Warning"));
    }

    [Fact]
    public void The_pool_is_case_sensitive()
    {
        // "Level" e "level" são chaves diferentes num log; uni-las mudaria o dado exibido.
        var pool = new PoolDeTextos();

        Assert.NotSame(pool.Compartilhar("Level"), pool.Compartilhar("level"));
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Null_and_empty_pass_through()
    {
        var pool = new PoolDeTextos();

        Assert.Null(pool.Compartilhar(null));
        Assert.Same(string.Empty, pool.Compartilhar(""));
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Only_distinct_values_are_kept()
    {
        var pool = new PoolDeTextos();

        for (var i = 0; i < 1_000; i++) pool.Compartilhar("Information");

        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task Concurrent_callers_still_converge_on_one_instance()
    {
        // A carga lê vários arquivos em paralelo com o mesmo pool.
        var pool = new PoolDeTextos();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => pool.Compartilhar(new string("Application".ToCharArray())))));

        Assert.Equal(1, pool.Count);
        Assert.All(resultados, r => Assert.Same(resultados[0], r));
    }
}
