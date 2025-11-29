using System.Threading.Tasks;

namespace ClefExplorer.Services
{
    public interface IFilePickerService
    {
        Task<string?> PickFileAsync(string filter);
        Task<string?> PickFolderAsync();
    }
}
