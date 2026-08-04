using System.Text.Json;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Regras do aviso de versão nova: quem instalou pelo canal oficial baixa e reinicia; quem
/// veio da Microsoft Store ou roda o executável avulso apenas recebe o aviso.
/// </summary>
public class ServicoAtualizacaoTests
{
    private sealed class AtualizadorFake : IAtualizadorLocal
    {
        public bool PodeAplicar { get; init; }
        public string? VersaoPreparada { get; init; }
        public bool Aplicou { get; private set; }
        public Exception? FalhaAoPreparar { get; init; }

        public Task<string?> PrepararAsync(CancellationToken cancellationToken)
        {
            if (FalhaAoPreparar is not null) throw FalhaAoPreparar;
            return Task.FromResult(VersaoPreparada);
        }

        public void AplicarEReiniciar() => Aplicou = true;
    }

    private sealed class ConsultorFake : IConsultorReleases
    {
        public ReleaseGithub? Release { get; init; }
        public Exception? Falha { get; init; }
        public int Chamadas { get; private set; }

        public Task<ReleaseGithub?> UltimoAsync(CancellationToken cancellationToken)
        {
            Chamadas++;
            if (Falha is not null) throw Falha;
            return Task.FromResult(Release);
        }
    }

    private static ReleaseGithub Release(string versao) =>
        new(Version.Parse(versao), $"https://github.com/afernandes/ClefExplorer/releases/tag/v{versao}");

    [Fact]
    public async Task Quem_instalou_pelo_canal_oficial_baixa_e_pode_reiniciar()
    {
        var atualizador = new AtualizadorFake { PodeAplicar = true, VersaoPreparada = "1.4.0" };
        var servico = new ServicoAtualizacao(atualizador, new ConsultorFake(), new Version(1, 3, 0));

        await servico.VerificarAsync();

        Assert.NotNull(servico.Disponivel);
        Assert.Equal("1.4.0", servico.Disponivel!.Versao);
        Assert.True(servico.Disponivel.PodeReiniciar);
    }

    [Fact]
    public async Task Instalacao_da_Store_apenas_avisa_e_aponta_para_o_release()
    {
        // PodeAplicar falso é o que a Store (e o executável avulso) produz: o pacote não
        // pode se substituir sozinho.
        var consultor = new ConsultorFake { Release = Release("1.4.0") };
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = false }, consultor, new Version(1, 3, 0));

        await servico.VerificarAsync();

        Assert.NotNull(servico.Disponivel);
        Assert.Equal("1.4.0", servico.Disponivel!.Versao);
        Assert.False(servico.Disponivel.PodeReiniciar);
        Assert.Contains("releases/tag/v1.4.0", servico.Disponivel.Url);
    }

    [Fact]
    public async Task A_versao_publicada_igual_a_atual_nao_vira_aviso()
    {
        // Regressão: o assembly tem QUATRO partes (1.3.0.0) e a tag do release, três
        // (v1.3.0). Sem normalizar, Version considera 1.3.0 < 1.3.0.0 e o aplicativo
        // anunciaria "versão nova" a cada abertura, para sempre.
        var consultor = new ConsultorFake { Release = Release("1.3.0") };
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = false }, consultor, new Version(1, 3, 0, 0));

        await servico.VerificarAsync();

        Assert.Null(servico.Disponivel);
    }

    [Fact]
    public async Task Uma_versao_publicada_mais_antiga_nao_vira_aviso()
    {
        var consultor = new ConsultorFake { Release = Release("1.2.0") };
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = false }, consultor, new Version(1, 3, 0));

        await servico.VerificarAsync();

        Assert.Null(servico.Disponivel);
    }

    [Fact]
    public async Task Falha_de_rede_nao_derruba_a_abertura_do_aplicativo()
    {
        var consultor = new ConsultorFake { Falha = new HttpRequestException("sem rede") };
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = false }, consultor, new Version(1, 3, 0));

        await servico.VerificarAsync();

        Assert.Null(servico.Disponivel);
    }

    [Fact]
    public async Task Falha_ao_preparar_o_pacote_nao_derruba_a_abertura()
    {
        var atualizador = new AtualizadorFake
        {
            PodeAplicar = true,
            FalhaAoPreparar = new IOException("disco cheio"),
        };
        var servico = new ServicoAtualizacao(atualizador, new ConsultorFake(), new Version(1, 3, 0));

        await servico.VerificarAsync();

        Assert.Null(servico.Disponivel);
    }

    [Fact]
    public async Task Sem_versao_nova_o_evento_nao_dispara()
    {
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = true, VersaoPreparada = null },
            new ConsultorFake(),
            new Version(1, 3, 0));

        var disparos = 0;
        servico.Changed += () => disparos++;

        await servico.VerificarAsync();

        Assert.Equal(0, disparos);
        Assert.Null(servico.Disponivel);
    }

    [Fact]
    public async Task O_evento_avisa_a_interface_quando_encontra_versao_nova()
    {
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = true, VersaoPreparada = "2.0.0" },
            new ConsultorFake(),
            new Version(1, 3, 0));

        var disparos = 0;
        servico.Changed += () => disparos++;

        await servico.VerificarAsync();

        Assert.Equal(1, disparos);
    }

    [Fact]
    public async Task Quem_pode_aplicar_nao_consulta_a_api_de_releases()
    {
        // O gerenciador do canal oficial já resolve a consulta; bater na API do GitHub de
        // novo só gastaria a cota anônima (60 chamadas por hora).
        var consultor = new ConsultorFake { Release = Release("9.9.9") };
        var servico = new ServicoAtualizacao(
            new AtualizadorFake { PodeAplicar = true, VersaoPreparada = "1.4.0" },
            consultor,
            new Version(1, 3, 0));

        await servico.VerificarAsync();

        Assert.Equal(0, consultor.Chamadas);
    }

    [Fact]
    public async Task Reiniciar_so_age_quando_o_pacote_esta_preparado()
    {
        var atualizador = new AtualizadorFake { PodeAplicar = false };
        var servico = new ServicoAtualizacao(
            atualizador, new ConsultorFake { Release = Release("1.4.0") }, new Version(1, 3, 0));

        await servico.VerificarAsync();
        servico.AplicarEReiniciar();

        // O aviso da Store não tem pacote baixado: mandar aplicar aqui reiniciaria o
        // aplicativo na mesma versão.
        Assert.False(atualizador.Aplicou);
    }

    [Fact]
    public async Task Reiniciar_aplica_o_pacote_do_canal_oficial()
    {
        var atualizador = new AtualizadorFake { PodeAplicar = true, VersaoPreparada = "1.4.0" };
        var servico = new ServicoAtualizacao(atualizador, new ConsultorFake(), new Version(1, 3, 0));

        await servico.VerificarAsync();
        servico.AplicarEReiniciar();

        Assert.True(atualizador.Aplicou);
    }

    // --- Leitura da resposta do GitHub -------------------------------------------

    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement;

    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.4.0", "1.4.0")]
    [InlineData("V1.4.0", "1.4.0")]
    [InlineData("v1.4.0.0", "1.4.0.0")]
    public void A_tag_do_release_vira_versao(string tag, string esperada)
    {
        var release = ConsultorReleasesGithub.Interpretar(
            Json($$"""{"tag_name":"{{tag}}","html_url":"https://github.com/x/y/releases/tag/{{tag}}"}"""));

        Assert.NotNull(release);
        Assert.Equal(Version.Parse(esperada), release!.Versao);
    }

    [Theory]
    [InlineData("""{"html_url":"https://github.com"}""")]
    [InlineData("""{"tag_name":"nightly"}""")]
    [InlineData("""{"tag_name":123}""")]
    public void Uma_tag_que_nao_e_versao_e_ignorada(string json)
    {
        Assert.Null(ConsultorReleasesGithub.Interpretar(Json(json)));
    }

    [Fact]
    public void Sem_html_url_o_aviso_cai_na_pagina_de_releases()
    {
        var release = ConsultorReleasesGithub.Interpretar(Json("""{"tag_name":"v1.4.0"}"""));

        Assert.NotNull(release);
        Assert.Equal(ServicoAtualizacao.UrlDosReleases, release!.Url);
    }

    [Theory]
    [InlineData("https://github.com/afernandes/ClefExplorer/releases", true)]
    [InlineData("http://exemplo.com", true)]
    [InlineData(@"C:\Windows\System32\calc.exe", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void So_endereco_web_chega_ao_shell(string? url, bool aceito)
    {
        // O endereço vem de uma resposta da API: sem esta barreira, UseShellExecute
        // executaria um caminho local como programa.
        Assert.Equal(aceito, ExploradorArquivos.EhEnderecoWeb(url));
    }
}
