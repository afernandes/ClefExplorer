using System.Threading.Tasks;

namespace ClefExplorer.Services
{
    public interface IFilePickerService
    {
        Task<string?> PickFileAsync(string filter);
        Task<string?> PickFolderAsync();

        /// <summary>Escolhe o destino de uma exportação. Devolve <c>null</c> se o usuário cancelar.</summary>
        Task<string?> PickSaveFileAsync(string filter, string defaultFileName);
    }
}
