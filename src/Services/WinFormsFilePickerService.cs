using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClefExplorer.Services
{
    public class WinFormsFilePickerService : IFilePickerService
    {
        public async Task<string?> PickFileAsync(string filter)
        {
            return await Task.Run(() =>
            {
                string? result = null;
                var t = new Thread(() =>
                {
                    using var ofd = new OpenFileDialog();
                    ofd.Filter = filter;
                    ofd.Multiselect = false;
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        result = ofd.FileName;
                    }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                return result;
            });
        }

        public async Task<string?> PickFolderAsync()
        {
            return await Task.Run(() =>
            {
                string? result = null;
                var t = new Thread(() =>
                {
                    using var fbd = new FolderBrowserDialog();
                    fbd.UseDescriptionForTitle = true;
                    fbd.ShowNewFolderButton = false;
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        result = fbd.SelectedPath;
                    }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                return result;
            });
        }
    }
}
