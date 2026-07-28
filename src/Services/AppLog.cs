using System;
using System.IO;
using System.Text;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Log de diagnóstico do próprio aplicativo, gravado em
    /// <c>%LOCALAPPDATA%\ClefExplorer\logs\clefexplorer-AAAA-MM-DD.log</c>.
    ///
    /// <para>Existe para que as falhas deixem rastro: antes, exceções eram engolidas por
    /// <c>catch { }</c> e não havia como saber, depois do fato, por que um arquivo não
    /// tinha sido carregado ou por que as configurações não salvaram.</para>
    ///
    /// <para>É estático de propósito — precisa funcionar em pontos onde não há injeção de
    /// dependência (construtores de serviço, handlers do WinForms). Nunca lança: um erro
    /// ao registrar um erro não pode derrubar o aplicativo.</para>
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new();
        private static string? _folderOverride;
        private static string? _filePath;
        private static bool _resolved;

        /// <summary>Caminho do arquivo de log, ou <c>null</c> se não foi possível criá-lo.</summary>
        public static string? FilePath
        {
            get
            {
                lock (Gate)
                {
                    if (!_resolved)
                    {
                        _filePath = CreateLogFilePath();
                        _resolved = true;
                    }
                    return _filePath;
                }
            }
        }

        /// <summary>
        /// Redireciona o log para outra pasta. Existe para os testes não escreverem no
        /// %LOCALAPPDATA% real do usuário — o app não chama isto.
        /// </summary>
        public static void RedirectTo(string folder)
        {
            lock (Gate)
            {
                _folderOverride = folder;
                _resolved = false;
                _filePath = null;
            }
        }

        public static void Info(string message) => Write("INF", message, null);
        public static void Warning(string message, Exception? ex = null) => Write("WRN", message, ex);
        public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

        private static string? CreateLogFilePath()
        {
            try
            {
                var folder = _folderOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClefExplorer", "logs");
                Directory.CreateDirectory(folder);

                PurgeOldFiles(folder);

                return Path.Combine(folder, $"clefexplorer-{DateTime.Now:yyyy-MM-dd}.log");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Mantém apenas os arquivos dos últimos 7 dias, para o log não crescer sem limite.</summary>
        private static void PurgeOldFiles(string folder)
        {
            try
            {
                var limite = DateTime.Now.AddDays(-7);
                foreach (var file in Directory.GetFiles(folder, "clefexplorer-*.log"))
                {
                    if (File.GetLastWriteTime(file) < limite)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // Limpeza é best-effort.
            }
        }

        private static void Write(string level, string message, Exception? ex)
        {
            var path = FilePath;
            if (path is null) return;

            try
            {
                var sb = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);

                if (ex is not null)
                {
                    sb.AppendLine().Append(ex);
                }

                lock (Gate)
                {
                    File.AppendAllText(path, sb.AppendLine().ToString());
                }
            }
            catch
            {
                // Falhar ao registrar não pode derrubar o app.
            }
        }
    }
}
