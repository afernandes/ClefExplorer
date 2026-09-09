using Bunit;
using ClefExplorer.Components;

namespace ClefExplorer.Tests;

public sealed class LogColumnOptionTests
{
    [Fact]
    public void Renderizar_ColunaDesmarcada_RefleteEstadoEPermiteAlternar()
    {
        using var contexto = new BunitContext();
        bool? alteracao = null;
        var componente = contexto.Render<LogColumnOption>(p => p
            .Add(c => c.Titulo, "Id")
            .Add(c => c.Marcada, false)
            .Add(c => c.OnToggle, valor => alteracao = valor));
        Assert.False(componente.Find("input").HasAttribute("checked"));
        componente.Find("input").Change(true);
        Assert.True(alteracao);
        componente.Render(p => p.Add(c => c.Marcada, true));
        Assert.True(componente.Find("input").HasAttribute("checked"));
        componente.Find("input").Change(false);
        Assert.False(alteracao);
    }
}
