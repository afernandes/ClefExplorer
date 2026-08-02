namespace ClefExplorer.Services
{
    /// <summary>Normaliza caminhos recebidos pela linha de comando antes de o host trocar de pasta.</summary>
    public static class CaminhosEntrada
    {
        public static string[] Normalizar(
            IEnumerable<string> argumentos,
            string diretorioOrigem)
        {
            ArgumentNullException.ThrowIfNull(argumentos);
            ArgumentException.ThrowIfNullOrWhiteSpace(diretorioOrigem);

            return argumentos
                .Where(argumento => !string.IsNullOrWhiteSpace(argumento))
                .Select(argumento => Normalizar(argumento, diretorioOrigem))
                .ToArray();
        }

        private static string Normalizar(string argumento, string diretorioOrigem)
        {
            try
            {
                var expandido = Environment.ExpandEnvironmentVariables(argumento);
                return Path.IsPathFullyQualified(expandido)
                    ? Path.GetFullPath(expandido)
                    : Path.GetFullPath(expandido, diretorioOrigem);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                AppLog.Warning($"Não foi possível normalizar o caminho recebido: '{argumento}'", ex);
                return argumento;
            }
        }
    }
}
