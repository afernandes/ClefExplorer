using System.Runtime.CompilerServices;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

internal static class TestSetup
{
    /// <summary>
    /// O <see cref="AppLog"/> é estático e, por padrão, grava no %LOCALAPPDATA% real.
    /// Os testes exercitam caminhos de erro de propósito, então redirecionamos o log para
    /// uma pasta temporária — caso contrário a suíte poluiria os logs do usuário.
    /// </summary>
    [ModuleInitializer]
    public static void RedirectAppLog()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", "_logs");
        AppLog.RedirectTo(folder);
    }
}
