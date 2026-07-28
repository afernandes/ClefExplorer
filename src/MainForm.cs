using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using ClefExplorer.Models;
using ClefExplorer.Services;

namespace ClefExplorer
{
    public class MainForm : Form
    {
        private readonly IServiceProvider _services;
        private readonly string[] _initialPaths;
        private BlazorWebView _blazorWebView = null!;
        private WindowPlacementService? _placementService;

        public MainForm(IServiceProvider services, params string[] initialPaths)
        {
            _services = services;
            _initialPaths = initialPaths ?? Array.Empty<string>();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            this.Text = $"Clef Explorer v{version}";
            try
            {
                this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Não foi possível extrair o ícone do executável", ex);
            }

            Width = 1200;
            Height = 800;
            RestoreWindowPlacement();

            // O usuário pode soltar arquivos/pastas em qualquer lugar da janela.
            AllowDrop = true;
            DragEnter += MainForm_DragEnter;
            DragDrop += MainForm_DragDrop;

            BuildUi();

            this.FormClosing += MainForm_FormClosing;
            this.FormClosed += MainForm_FormClosed;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            var existentes = _initialPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                .ToArray();

            if (existentes.Length > 0)
            {
                await LoadPathsAsync(existentes);
            }
        }

        /// <summary>Carrega caminhos vindos da linha de comando, de um drop ou de outra instância.</summary>
        public async Task LoadPathsAsync(IEnumerable<string> paths)
        {
            try
            {
                var store = _services.GetRequiredService<LogStore>();
                await store.LoadFromPathsAsync(paths);
            }
            catch (Exception ex)
            {
                AppLog.Error("Falha ao carregar os caminhos solicitados", ex);
            }
        }

        /// <summary>
        /// Ponto de entrada para uma segunda instância: traz a janela para frente e carrega
        /// os caminhos recebidos. Chamado de outra thread, por isso o Invoke.
        /// </summary>
        public void ReceivePathsFromOtherInstance(string[] paths)
        {
            if (IsDisposed) return;

            try
            {
                BeginInvoke(() =>
                {
                    if (WindowState == FormWindowState.Minimized)
                    {
                        WindowState = FormWindowState.Normal;
                    }
                    Activate();

                    if (paths.Length > 0)
                    {
                        _ = LoadPathsAsync(paths);
                    }
                });
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falha ao processar os caminhos de outra instância", ex);
            }
        }

        // --- Arrastar e soltar -------------------------------------------------------

        private static string[] ExtractDroppedPaths(IDataObject? data)
        {
            if (data?.GetData(DataFormats.FileDrop) is not string[] paths) return Array.Empty<string>();

            return paths
                .Where(p => Directory.Exists(p) || File.Exists(p))
                .ToArray();
        }

        private void MainForm_DragEnter(object? sender, DragEventArgs e)
        {
            e.Effect = ExtractDroppedPaths(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private async void MainForm_DragDrop(object? sender, DragEventArgs e)
        {
            var paths = ExtractDroppedPaths(e.Data);
            if (paths.Length > 0)
            {
                await LoadPathsAsync(paths);
            }
        }

        // --- Posição da janela -------------------------------------------------------

        private void RestoreWindowPlacement()
        {
            try
            {
                _placementService = _services.GetRequiredService<WindowPlacementService>();
                var placement = _placementService.Load();
                if (placement is null) return;

                var screens = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
                if (!WindowPlacementService.IsVisibleOnAnyScreen(placement, screens))
                {
                    // Monitor desconectado desde a última execução: cai no padrão centralizado.
                    AppLog.Info("Posição salva da janela está fora dos monitores atuais; usando o padrão.");
                    return;
                }

                StartPosition = FormStartPosition.Manual;
                Bounds = new System.Drawing.Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
                if (placement.Maximized)
                {
                    WindowState = FormWindowState.Maximized;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falha ao restaurar a posição da janela", ex);
            }
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // RestoreBounds tem o tamanho "normal" mesmo quando a janela está maximizada,
            // que é o que queremos guardar para restaurar depois de desmaximizar.
            var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

            _placementService?.Save(new WindowPlacement
            {
                X = normal.X,
                Y = normal.Y,
                Width = normal.Width,
                Height = normal.Height,
                Maximized = WindowState == FormWindowState.Maximized,
            });
        }

        // --- WebView -----------------------------------------------------------------

        private void BuildUi()
        {
            // Environment.ProcessPath funciona no publish single-file, onde
            // AppContext.BaseDirectory pode apontar para a pasta temporária de extração.
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var hostPage = Path.Combine(exeDir, "wwwroot\\index.html");
            var absPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, hostPage);
            if (System.IO.File.Exists(absPath))
            {
                hostPage = absPath;
            }

            _blazorWebView = new BlazorWebView
            {
                Dock = DockStyle.Fill,
                HostPage = hostPage,
                Services = _services
            };
            _blazorWebView.RootComponents.Add(new RootComponent("#app", typeof(App), parameters: null));
            _blazorWebView.WebView.CoreWebView2InitializationCompleted += WebViewOnCoreWebView2InitializationCompleted;

            Controls.Add(_blazorWebView);
        }

        private void WebViewOnCoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            _blazorWebView.WebView.CoreWebView2.IsMuted = true;
            _blazorWebView.WebView.CoreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;

            // O WebView2 engole o drop por padrão, e a API web não expõe o caminho completo
            // do arquivo. Desabilitando o drop interno, o evento chega ao formulário, que
            // recebe os caminhos reais via DataFormats.FileDrop.
            try
            {
                _blazorWebView.WebView.AllowExternalDrop = false;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Não foi possível desabilitar o drop interno do WebView2", ex);
            }
        }

        private void CoreWebView2_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            // Negar por padrão. O conteúdo é local e confiável hoje, mas liberar tudo
            // (câmera, microfone, geolocalização…) é um default invertido: qualquer
            // conteúdo futuro herdaria permissões amplas de graça.
            e.State = e.PermissionKind switch
            {
                // Usada pelo botão "copiar stack trace".
                CoreWebView2PermissionKind.ClipboardRead => CoreWebView2PermissionState.Allow,
                _ => CoreWebView2PermissionState.Deny,
            };

            if (e.State == CoreWebView2PermissionState.Deny)
            {
                AppLog.Info($"Permissão negada ao WebView2: {e.PermissionKind}");
            }
        }

        private void MainForm_FormClosed(object? sender, FormClosedEventArgs e)
        {
            try
            {
                // Dispose na thread de UI: o BlazorWebView é um controle do WinForms e
                // descartá-lo em outra thread (como era feito num Task.Run fire-and-forget)
                // é justamente o tipo de acesso cross-thread que o WinForms não suporta.
                _blazorWebView?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falha ao descartar o BlazorWebView", ex);
            }
        }
    }
}
