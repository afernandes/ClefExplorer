using System.Reflection;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Avisa quando existe versão nova no canal oficial (Releases do GitHub) e, para quem
    /// instalou por ele, deixa o pacote pronto para ser aplicado no reinício.
    /// </summary>
    /// <remarks>
    /// O canal oficial é único de propósito: o pacote da Microsoft Store passa por análise e
    /// costuma sair depois, então quem instalou pela Store também é avisado daqui — só que
    /// para esse público (e para quem roda o executável avulso) o aviso leva à página do
    /// release, porque um pacote MSIX não pode ser substituído pelo próprio aplicativo.
    /// </remarks>
    public sealed class ServicoAtualizacao
    {
        private readonly IAtualizadorLocal _atualizador;
        private readonly IConsultorReleases _consultor;
        private readonly Version _versaoAtual;

        public ServicoAtualizacao(
            IAtualizadorLocal atualizador,
            IConsultorReleases consultor,
            Version? versaoAtual = null)
        {
            _atualizador = atualizador;
            _consultor = consultor;
            _versaoAtual = Normalizar(
                versaoAtual ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
        }

        /// <summary>Versão nova encontrada, ou <c>null</c> enquanto não houver.</summary>
        public InfoAtualizacao? Disponivel { get; private set; }

        /// <summary>Disparado quando <see cref="Disponivel"/> passa a ter valor.</summary>
        public event Action? Changed;

        /// <summary>
        /// Verifica o canal oficial. Nunca lança: uma falha de rede (ou uma máquina sem
        /// internet) não pode atrapalhar a abertura do aplicativo — o aviso simplesmente
        /// não aparece.
        /// </summary>
        public async Task VerificarAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var info = _atualizador.PodeAplicar
                    ? await PrepararInstalacaoAsync(cancellationToken).ConfigureAwait(false)
                    : await ApenasAvisarAsync(cancellationToken).ConfigureAwait(false);

                if (info is null) return;

                Disponivel = info;
                Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Fechar o aplicativo durante a verificação é rotina, não é falha.
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falha ao verificar se existe uma versão nova", ex);
            }
        }

        /// <summary>
        /// Aplica o pacote já baixado e reabre o aplicativo. Só faz sentido quando
        /// <see cref="InfoAtualizacao.PodeReiniciar"/> é verdadeiro.
        /// </summary>
        public void AplicarEReiniciar()
        {
            if (Disponivel is not { PodeReiniciar: true }) return;

            try
            {
                _atualizador.AplicarEReiniciar();
            }
            catch (Exception ex)
            {
                // O aplicativo continua utilizável na versão atual; o usuário ainda pode
                // baixar o pacote pela página do release.
                AppLog.Warning("Falha ao aplicar a atualização", ex);
            }
        }

        private async Task<InfoAtualizacao?> PrepararInstalacaoAsync(CancellationToken cancellationToken)
        {
            var versao = await _atualizador.PrepararAsync(cancellationToken).ConfigureAwait(false);
            if (versao is null) return null;

            AppLog.Info($"Versão {versao} baixada e pronta para ser aplicada no reinício.");
            return new InfoAtualizacao(versao, UrlDosReleases, PodeReiniciar: true);
        }

        private async Task<InfoAtualizacao?> ApenasAvisarAsync(CancellationToken cancellationToken)
        {
            var release = await _consultor.UltimoAsync(cancellationToken).ConfigureAwait(false);
            if (release is null || Normalizar(release.Versao) <= _versaoAtual) return null;

            AppLog.Info($"Versão {release.Versao} publicada no canal oficial (esta instalação atualiza manualmente).");
            return new InfoAtualizacao(release.Versao.ToString(), release.Url, PodeReiniciar: false);
        }

        /// <summary>
        /// Compara sempre com três partes. O assembly carrega quatro (1.3.0.0) e a tag do
        /// release, três (v1.3.0) — e <see cref="Version"/> considera 1.3.0 MENOR que
        /// 1.3.0.0, o que anunciaria uma versão nova a cada abertura.
        /// </summary>
        private static Version Normalizar(Version versao) =>
            new(versao.Major, versao.Minor, Math.Max(versao.Build, 0));

        public const string UrlDosReleases = "https://github.com/afernandes/ClefExplorer/releases/latest";
    }
}
