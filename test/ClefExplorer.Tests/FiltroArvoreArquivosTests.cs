using ClefExplorer.Helpers;
using ClefExplorer.Models;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="FiltroArvoreArquivos"/> — a busca do campo "Localizar arquivo"
/// da barra lateral, que esconde nós sem reconstruir a árvore.
/// </summary>
public class FiltroArvoreArquivosTests
{
    // Árvore de exemplo, no formato que o LogFileTree monta a partir dos nomes:
    //
    // TOTVS
    // ├── API
    // │   ├── HttpClient   (…API.HttpClient.clef)
    // │   └── RacClient    (…API.RacClient.clef)
    // └── Fiscal
    //     └── Zeus         (…Fiscal.Zeus.clef)
    private static FileTreeNode Arvore()
    {
        var httpClient = Folha("HttpClient", @"C:\logs\TOTVS.API.HttpClient.clef");
        var racClient = Folha("RacClient", @"C:\logs\TOTVS.API.RacClient.clef");
        var zeus = Folha("Zeus", @"C:\logs\TOTVS.Fiscal.Zeus.clef");

        var api = Pasta("API", httpClient, racClient);
        var fiscal = Pasta("Fiscal", zeus);
        return Pasta("TOTVS", api, fiscal);
    }

    private static FileTreeNode Folha(string nome, string caminho) =>
        new() { Name = nome, FullPath = caminho };

    private static FileTreeNode Pasta(string nome, params FileTreeNode[] filhos)
    {
        var no = new FileTreeNode { Name = nome };
        foreach (var filho in filhos)
        {
            filho.Parent = no;
            no.Children.Add(filho);
        }
        return no;
    }

    private static FileTreeNode Buscar(FileTreeNode raiz, string nome)
    {
        if (raiz.Name == nome) return raiz;
        foreach (var filho in raiz.Children)
        {
            var achado = Buscar(filho, nome);
            if (achado is not null) return achado;
        }
        return null!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_term_shows_the_whole_tree(string? termo)
    {
        var raiz = Arvore();

        var visiveis = FiltroArvoreArquivos.Visiveis(new[] { raiz }, termo);

        Assert.Equal(6, visiveis.Count); // TOTVS, API, HttpClient, RacClient, Fiscal, Zeus
    }

    [Fact]
    public void A_matching_leaf_keeps_the_path_down_to_it()
    {
        var raiz = Arvore();

        var visiveis = FiltroArvoreArquivos.Visiveis(new[] { raiz }, "Rac");

        // Sem os ancestrais o nó não teria como ser exibido.
        Assert.Equal(
            new[] { "API", "RacClient", "TOTVS" },
            visiveis.Select(n => n.Name).OrderBy(n => n));
    }

    [Fact]
    public void Matching_a_folder_reveals_everything_under_it()
    {
        var raiz = Arvore();

        var visiveis = FiltroArvoreArquivos.Visiveis(new[] { raiz }, "api");

        Assert.Contains(Buscar(raiz, "HttpClient"), visiveis);
        Assert.Contains(Buscar(raiz, "RacClient"), visiveis);
        Assert.DoesNotContain(Buscar(raiz, "Fiscal"), visiveis);
    }

    [Fact]
    public void The_search_also_looks_at_the_file_name()
    {
        // A árvore quebra o nome em vários níveis, então "Fiscal.Zeus" não bate com nenhum
        // rótulo isolado — só com o nome do arquivo.
        var raiz = Arvore();

        var visiveis = FiltroArvoreArquivos.Visiveis(new[] { raiz }, "Fiscal.Zeus");

        Assert.Contains(Buscar(raiz, "Zeus"), visiveis);
        Assert.DoesNotContain(Buscar(raiz, "API"), visiveis);
    }

    [Fact]
    public void The_search_ignores_case()
    {
        var raiz = Arvore();

        Assert.Equal(
            FiltroArvoreArquivos.Visiveis(new[] { raiz }, "httpclient"),
            FiltroArvoreArquivos.Visiveis(new[] { raiz }, "HttpClient"));
    }

    [Fact]
    public void Nothing_matches_leaves_nothing_visible()
    {
        var raiz = Arvore();

        var visiveis = FiltroArvoreArquivos.Visiveis(new[] { raiz }, "inexistente");

        Assert.Empty(visiveis);
    }

    [Fact]
    public void The_returned_nodes_are_the_same_instances()
    {
        // O OmniTree guarda a marcação por referência: recriar os nós ao filtrar apagaria
        // os checkboxes do usuário.
        var raiz = Arvore();

        var visiveis = FiltroArvoreArquivos.Visiveis(new[] { raiz }, "Zeus");

        Assert.Contains(visiveis, n => ReferenceEquals(n, raiz));
    }
}
