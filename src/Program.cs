using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ClefExplorer.Services;
using Omni.Blazor;

namespace ClefExplorer
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.GetTempPath() + @"ClefExplorer");

            // Uma segunda instância apenas entrega seus caminhos à janela já aberta e sai.
            // Antes, selecionar N arquivos no Explorer abria N janelas.
            //
            // Só encerramos se a entrega der certo: se o pipe estiver indisponível (a outra
            // instância travou, ou o mutex ficou órfão), sair sem abrir nada deixaria o
            // usuário sem conseguir usar o aplicativo. Nesse caso seguimos como instância
            // independente — mesma escolha feita quando o próprio mutex falha.
            if (!SingleInstance.TryAcquire(out var singleInstance)
                && SingleInstance.SendToExistingInstance(args))
            {
                return;
            }

            FixCurrentPath();

            ApplicationConfiguration.Initialize();

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddDebug());
            services.AddWindowsFormsBlazorWebView();
            services.AddOmniComponents();
            services.AddSingleton<AppStorage>();
            services.AddSingleton<WindowPlacementService>();
            services.AddSingleton<UiPreferencesService>();
            services.AddSingleton<LogStore>();
            services.AddSingleton<LogGroupService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<IFilePickerService, WinFormsFilePickerService>();
            services.AddSingleton<FileAssociationService>();

#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif

            var serviceProvider = services.BuildServiceProvider();

            // Todos os argumentos, não só o primeiro: abrir vários .clef de uma vez
            // carrega todos na mesma janela.
            using (singleInstance)
            {
                var form = new MainForm(serviceProvider, args);
                singleInstance?.StartListening(form.ReceivePathsFromOtherInstance);
                Application.Run(form);
            }
        }

        private static void FixCurrentPath()
        {
            try
            {
                // Use Environment.ProcessPath to get the actual executable path, 
                // which works correctly for single-file apps (unlike AppContext.BaseDirectory which might point to temp)
                var processPath = Environment.ProcessPath;
                var directoryPath = !string.IsNullOrEmpty(processPath)
                    ? Path.GetDirectoryName(processPath)
                    : AppContext.BaseDirectory;

                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.SetCurrentDirectory(directoryPath);
                    var currentDirectory = Directory.GetCurrentDirectory();
                    Console.WriteLine($"CURRENT DIRECTORY: {currentDirectory}");
                }
                else
                {
                    Console.WriteLine("WARNING: Could not determine application directory path.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR ON SET CURRENT DIRECTORY: " + ex.Message);
                Console.WriteLine(ex.ToString());
            }
        }

    }
}
