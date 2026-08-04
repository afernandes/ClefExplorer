namespace ClefExplorer.Services
{
    /// <summary>
    /// Versão publicada mais recente, quando é maior que a que está rodando.
    /// </summary>
    /// <param name="Versao">Versão anunciada, já sem o "v" da tag (ex.: <c>1.4.0</c>).</param>
    /// <param name="Url">Página do release, para quem precisa baixar à mão.</param>
    /// <param name="PodeReiniciar">
    /// <c>true</c> quando o pacote já foi baixado e basta reiniciar para aplicá-lo. É falso
    /// para quem instalou pela Microsoft Store ou roda o executável avulso: nesses casos o
    /// aplicativo não tem como se substituir sozinho e o aviso leva ao release.
    /// </param>
    public sealed record InfoAtualizacao(string Versao, string Url, bool PodeReiniciar);

    /// <summary>Último release publicado no canal oficial.</summary>
    public sealed record ReleaseGithub(Version Versao, string Url);

    /// <summary>Consulta a versão publicada. Implementação real fala com a API do GitHub.</summary>
    public interface IConsultorReleases
    {
        Task<ReleaseGithub?> UltimoAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// A parte que só existe para quem instalou pelo canal oficial: baixar o pacote e
    /// aplicá-lo. Isolada em interface para o <see cref="ServicoAtualizacao"/> ser
    /// testável sem instalar o aplicativo de verdade.
    /// </summary>
    public interface IAtualizadorLocal
    {
        /// <summary>Se esta instalação sabe se atualizar sozinha.</summary>
        bool PodeAplicar { get; }

        /// <summary>
        /// Procura, baixa e deixa o pacote pronto. Devolve a versão preparada, ou
        /// <c>null</c> se já está atualizado.
        /// </summary>
        Task<string?> PrepararAsync(CancellationToken cancellationToken);

        /// <summary>Fecha o aplicativo, aplica o pacote preparado e abre de novo.</summary>
        void AplicarEReiniciar();
    }
}
