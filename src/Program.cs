using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ClefExplorer.Services;

namespace ClefExplorer
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.GetTempPath() + @"ClefExplorer");

            FixCurrentPath();

            ApplicationConfiguration.Initialize();

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddDebug());
            services.AddWindowsFormsBlazorWebView();
            services.AddSingleton<LogStore>();
            services.AddSingleton<LogGroupService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<IFilePickerService, WinFormsFilePickerService>();
            services.AddSingleton<FileAssociationService>();

#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif

            var serviceProvider = services.BuildServiceProvider();

            string? initialFile = args.Length > 0 ? args[0] : null;
            Application.Run(new MainForm(serviceProvider, initialFile));
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
