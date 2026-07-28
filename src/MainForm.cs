using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using ClefExplorer.Services;

namespace ClefExplorer
{
    public class MainForm : Form
    {
        private readonly IServiceProvider _services;
        private readonly string? _initialFile;
        private BlazorWebView _blazorWebView = null!;

        public MainForm(IServiceProvider services, string? initialFile = null)
        {
            _services = services;
            _initialFile = initialFile;
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

            BuildUi();

            this.FormClosed += MainForm_FormClosed;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!string.IsNullOrEmpty(_initialFile) && System.IO.File.Exists(_initialFile))
            {
                var store = _services.GetRequiredService<LogStore>();
                await store.LoadFromFile(_initialFile);
            }
        }

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
        }

        private void CoreWebView2_PermissionRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2PermissionRequestedEventArgs e)
        {
            e.State = CoreWebView2PermissionState.Allow;
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
