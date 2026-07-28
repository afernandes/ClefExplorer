using System;
using System.IO;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Resolve onde os dados do usuário (settings.json, groups.json) são lidos e gravados,
    /// e centraliza a leitura/escrita desses arquivos.
    ///
    /// <para>Antes, esses arquivos ficavam na pasta do executável (via
    /// <see cref="Directory.GetCurrentDirectory"/>). Isso quebra na instalação da Microsoft
    /// Store: o pacote MSIX é instalado em <c>C:\Program Files\WindowsApps\...</c>, que é
    /// somente leitura — a gravação falhava e o erro era engolido, fazendo o usuário perder
    /// grupos e configurações sem aviso.</para>
    ///
    /// <para>Agora usamos <see cref="Environment.SpecialFolder.LocalApplicationData"/>, que é
    /// gravável nos dois casos: na instalação normal aponta para <c>%LOCALAPPDATA%</c> e, no
    /// app empacotado, o Windows redireciona automaticamente para o armazenamento privado do
    /// pacote. Arquivos que já existam ao lado do executável são migrados na primeira
    /// execução, para ninguém perder o que já havia configurado.</para>
    /// </summary>
    public class AppStorage
    {
        private const string FolderName = "ClefExplorer";

        private readonly string _dataFolder;
        private readonly string? _legacyFolder;

        public AppStorage() : this(DefaultDataFolder(), DefaultLegacyFolder())
        {
        }

        /// <summary>Construtor usado pelos testes, com as pastas explícitas.</summary>
        public AppStorage(string dataFolder, string? legacyFolder = null)
        {
            _dataFolder = dataFolder;
            _legacyFolder = legacyFolder;
        }

        /// <summary>Pasta onde os dados do usuário são gravados.</summary>
        public string DataFolder => _dataFolder;

        private static string DefaultDataFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

        /// <summary>Pasta do executável — origem dos arquivos das versões anteriores.</summary>
        private static string? DefaultLegacyFolder()
        {
            try
            {
                // Environment.ProcessPath funciona no publish single-file, onde
                // AppContext.BaseDirectory pode apontar para a pasta temporária de extração.
                var processPath = Environment.ProcessPath;
                return !string.IsNullOrEmpty(processPath)
                    ? Path.GetDirectoryName(processPath)
                    : AppContext.BaseDirectory;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Caminho completo do arquivo na pasta de dados. Migra o arquivo legado da pasta do
        /// executável quando ainda não existe um correspondente na pasta de dados.
        /// </summary>
        public string GetPath(string fileName)
        {
            var target = Path.Combine(_dataFolder, fileName);
            MigrateLegacyIfNeeded(fileName, target);
            return target;
        }

        /// <summary>Conteúdo do arquivo, ou <c>null</c> se ele não existir.</summary>
        public string? ReadText(string fileName)
        {
            var path = GetPath(fileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// Grava o arquivo de forma atômica (arquivo temporário + troca), para que uma falha
        /// no meio da escrita não deixe um JSON truncado no lugar do arquivo bom.
        /// Lança em caso de erro — quem chama decide como reportar.
        /// </summary>
        public void WriteText(string fileName, string content)
        {
            Directory.CreateDirectory(_dataFolder);
            var path = Path.Combine(_dataFolder, fileName);
            var temp = path + ".tmp";

            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>
        /// Move um arquivo ilegível para <c>&lt;nome&gt;.corrupt</c> em vez de deixá-lo ser
        /// sobrescrito silenciosamente na próxima gravação. Devolve o caminho da quarentena,
        /// ou <c>null</c> se não foi possível movê-lo.
        /// </summary>
        public string? Quarantine(string fileName)
        {
            try
            {
                var path = Path.Combine(_dataFolder, fileName);
                if (!File.Exists(path)) return null;

                var corrupt = path + ".corrupt";
                File.Move(path, corrupt, overwrite: true);
                return corrupt;
            }
            catch
            {
                return null;
            }
        }

        private void MigrateLegacyIfNeeded(string fileName, string targetPath)
        {
            if (string.IsNullOrEmpty(_legacyFolder)) return;
            if (File.Exists(targetPath)) return;

            try
            {
                var legacyPath = Path.Combine(_legacyFolder, fileName);
                if (!File.Exists(legacyPath)) return;

                Directory.CreateDirectory(_dataFolder);
                // Copia (não move): a versão antiga do app pode continuar instalada e
                // depender do arquivo original ao lado do executável.
                File.Copy(legacyPath, targetPath, overwrite: false);
            }
            catch
            {
                // Migração é best-effort: se falhar, o app segue com a configuração padrão.
            }
        }
    }
}
