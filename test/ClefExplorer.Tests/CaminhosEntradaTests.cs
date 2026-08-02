using ClefExplorer.Services;

namespace ClefExplorer.Tests;

public class CaminhosEntradaTests
{
    [Fact]
    public void Caminho_relativo_e_resolvido_contra_a_pasta_de_origem()
    {
        var origem = Path.Combine(Path.GetTempPath(), "origem-clef");

        var resultado = CaminhosEntrada.Normalizar(new[] { @"logs\app.clef" }, origem);

        Assert.Equal(Path.GetFullPath(@"logs\app.clef", origem), Assert.Single(resultado));
    }

    [Fact]
    public void Caminho_absoluto_e_preservado_normalizado()
    {
        var absoluto = Path.Combine(Path.GetTempPath(), "logs", "..", "app.clef");

        var resultado = CaminhosEntrada.Normalizar(new[] { absoluto }, Path.GetTempPath());

        Assert.Equal(Path.GetFullPath(absoluto), Assert.Single(resultado));
    }
}
