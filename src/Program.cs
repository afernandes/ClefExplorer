using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ClefExplorer.Services;
using Omni.Blazor;
using Velopack;

namespace ClefExplorer
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // PRIMEIRA linha do Main, antes de qualquer outra coisa: é aqui que o Velopack
            // trata os hooks de instalação/atualização (o instalador reexecuta o próprio
            // executável com argumentos próprios e espera que ele saia). Qualquer código
            // antes disto — inclusive o mutex de instância única — rodaria durante a
            // instalação e poderia travá-la.
            VelopackApp.Build().Run();

            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.GetTempPath() + @"ClefExplorer");

            // Preserve a origem dos caminhos relativos antes de mudar o diretório para a
            // pasta do executável (necessário no publish single-file).
            var normalizedArgs = CaminhosEntrada.Normalizar(args, Directory.GetCurrentDirectory());

            // Uma segunda instância apenas entrega seus caminhos à janela já aberta e sai.
            // Antes, selecionar N arquivos no Explorer abria N janelas.
            //
            // Só encerramos se a entrega der certo: se o pipe estiver indisponível (a outra
            // instância travou, ou o mutex ficou órfão), sair sem abrir nada deixaria o
            // usuário sem conseguir usar o aplicativo. Nesse caso seguimos como instância
            // independente — mesma escolha feita quando o próprio mutex falha.
            if (!SingleInstance.TryAcquire(out var singleInstance)
                && SingleInstance.SendToExistingInstance(normalizedArgs))
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
            services.AddSingleton<DescobertaArquivosLog>();
            services.AddSingleton<ILeitorArquivoLog, LeitorArquivoLog>();
            services.AddSingleton<FiltroArquivosLogIgnorados>();
            services.AddSingleton<LogStore>();
            services.AddSingleton<ConsultaLogs>();
            services.AddSingleton<LogGroupService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<IFilePickerService, WinFormsFilePickerService>();
            services.AddSingleton<FileAssociationService>();
            services.AddSingleton<ExploradorArquivos>();
            services.AddSingleton<IAtualizadorLocal, AtualizadorVelopack>();
            services.AddSingleton<IConsultorReleases>(
                _ => new ConsultorReleasesGithub(ConsultorReleasesGithub.CriarCliente()));
            services.AddSingleton<ServicoAtualizacao>();

#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif

            using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            // Todos os argumentos, não só o primeiro: abrir vários .clef de uma vez
            // carrega todos na mesma janela.
            using (singleInstance)
            {
                var form = new MainForm(serviceProvider, normalizedArgs);
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
                    AppLog.Info($"Diretório atual: {Directory.GetCurrentDirectory()}");
                }
                else
                {
                    AppLog.Warning("Não foi possível determinar a pasta do aplicativo.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("Não foi possível ajustar o diretório atual do aplicativo", ex);
            }
        }

    }
}
