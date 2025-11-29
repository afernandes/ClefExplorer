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
            catch { }
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
            var hostPage = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "wwwroot\\index.html");
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

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                Task.Run(() => _blazorWebView?.Dispose()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                //ignore
            }
        }

    }
}
