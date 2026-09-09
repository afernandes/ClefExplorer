using ClefExplorer.Services;

namespace ClefExplorer.Tests;

internal sealed class FilePickerTeste(string destino) : IFilePickerService
{
    public Task<string?> PickFileAsync(string filter) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string filter, string defaultFileName) => Task.FromResult<string?>(destino);
}
