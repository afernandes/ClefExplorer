using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>Arquivos encontrados e falhas ocorridas durante a descoberta dos caminhos.</summary>
    public sealed record ResultadoDescobertaArquivosLog(
        IReadOnlyList<string> Arquivos,
        IReadOnlySet<string> ArquivosExplicitos,
        IReadOnlyList<LoadFailure> Falhas);

    /// <summary>
    /// Normaliza, enumera e deduplica arquivos de log. A enumeração é incremental e ignora
    /// subpastas inacessíveis, para que uma única ACL não invalide toda a pasta escolhida.
    /// </summary>
    public sealed class DescobertaArquivosLog
    {
        private static readonly EnumerationOptions OpcoesEnumeracao = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        public ResultadoDescobertaArquivosLog Descobrir(
            IEnumerable<string> caminhos,
            CancellationToken cancellationToken = default,
            Action<int>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(caminhos);

            var arquivos = new List<string>();
            var conhecidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var explicitos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var falhas = new List<LoadFailure>();

            foreach (var caminhoInformado in caminhos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(caminhoInformado))
                {
                    falhas.Add(new LoadFailure(caminhoInformado ?? string.Empty, "Caminho vazio."));
                    continue;
                }

                string caminho;
                try
                {
                    caminho = Path.GetFullPath(Environment.ExpandEnvironmentVariables(caminhoInformado));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    falhas.Add(new LoadFailure(caminhoInformado, ex.Message));
                    continue;
                }

                if (File.Exists(caminho))
                {
                    if (conhecidos.Add(caminho)) arquivos.Add(caminho);
                    explicitos.Add(caminho);
                    continue;
                }

                if (!Directory.Exists(caminho))
                {
                    falhas.Add(new LoadFailure(caminho, "Caminho não encontrado."));
                    continue;
                }

                try
                {
                    foreach (var arquivo in Directory.EnumerateFiles(caminho, "*", OpcoesEnumeracao))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!EhArquivoLog(arquivo) || !conhecidos.Add(arquivo)) continue;
                        arquivos.Add(arquivo);
                        if (arquivos.Count % 100 == 0) progress?.Invoke(arquivos.Count);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // IgnoreInaccessible protege subpastas; este catch cobre a própria raiz
                    // ou uma falha transitória durante a enumeração.
                    falhas.Add(new LoadFailure(caminho, ex.Message));
                }
            }

            progress?.Invoke(arquivos.Count);
            return new ResultadoDescobertaArquivosLog(arquivos, explicitos, falhas);
        }

        private static bool EhArquivoLog(string caminho) =>
            caminho.EndsWith(".clef", StringComparison.OrdinalIgnoreCase)
            || caminho.EndsWith(".clef.gz", StringComparison.OrdinalIgnoreCase);
    }
}
