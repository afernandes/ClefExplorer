using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="ExploradorArquivos"/>. Só a linha de comando é exercitada — o
/// disparo do processo é do sistema operacional, não nosso.
/// </summary>
public class ExploradorArquivosTests
{
    [Fact]
    public void The_file_path_is_quoted_when_selecting()
    {
        // Sem as aspas o Explorer parte o caminho na vírgula e no espaço, e acaba abrindo
        // a pasta errada.
        var argumentos = ExploradorArquivos.ArgumentosSelecionar(@"C:\Meus Logs\api,v2\log.clef");

        Assert.Equal("/select,\"C:\\Meus Logs\\api,v2\\log.clef\"", argumentos);
    }

    [Fact]
    public void The_folder_path_is_quoted_too()
    {
        var argumentos = ExploradorArquivos.ArgumentosAbrirPasta(@"C:\Meus Logs");

        Assert.Equal("\"C:\\Meus Logs\"", argumentos);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_reveals_nothing(string? caminho)
    {
        Assert.False(new ExploradorArquivos().Revelar(caminho));
    }

    [Fact]
    public void A_path_that_no_longer_exists_reveals_nothing()
    {
        // Log rotacionado, unidade de rede fora do ar: quem chama precisa saber para avisar
        // o usuário, em vez de abrir uma janela do Explorer no lugar errado.
        Assert.False(new ExploradorArquivos().Revelar(@"Z:\pasta-que-nao-existe\log.clef"));
    }
}
