using Velopack;
using Velopack.Sources;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Atualização automática para quem instalou pelo canal oficial (o Setup publicado nos
    /// Releases do GitHub).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PodeAplicar"/> é falso em duas situações legítimas, e nas duas o
    /// <see cref="ServicoAtualizacao"/> cai no caminho de apenas avisar:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// pacote da Microsoft Store — fica em <c>Program Files\WindowsApps</c>, somente
    /// leitura, e a identidade de um MSIX não pode ser substituída pelo próprio
    /// aplicativo; quem atualiza é a Store;
    /// </item>
    /// <item>
    /// executável avulso — não existe estrutura de instalação para receber o pacote.
    /// </item>
    /// </list>
    /// </remarks>
    public sealed class AtualizadorVelopack : IAtualizadorLocal
    {
        private const string RepositorioOficial = "https://github.com/afernandes/ClefExplorer";

        private readonly UpdateManager? _gerenciador;
        private UpdateInfo? _preparada;

        public AtualizadorVelopack()
        {
            try
            {
                _gerenciador = new UpdateManager(new GithubSource(RepositorioOficial, null, prerelease: false));
            }
            catch (Exception ex)
            {
                // Sem gerenciador o aplicativo segue no modo "apenas avisa".
                AppLog.Warning("Não foi possível iniciar o atualizador automático", ex);
                _gerenciador = null;
            }
        }

        public bool PodeAplicar => _gerenciador?.IsInstalled ?? false;

        public async Task<string?> PrepararAsync(CancellationToken cancellationToken)
        {
            if (_gerenciador is null) return null;

            var novidade = await _gerenciador.CheckForUpdatesAsync().ConfigureAwait(false);
            if (novidade is null) return null;

            await _gerenciador.DownloadUpdatesAsync(novidade, cancelToken: cancellationToken).ConfigureAwait(false);
            _preparada = novidade;
            return novidade.TargetFullRelease.Version.ToString();
        }

        public void AplicarEReiniciar()
        {
            if (_gerenciador is null || _preparada is null) return;

            _gerenciador.ApplyUpdatesAndRestart(_preparada.TargetFullRelease);
        }
    }
}
