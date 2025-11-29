using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace ClefExplorer.Services
{
    public class FileAssociationService
    {
        private const string AppId = "ClefExplorer.ClefFile";
        private const string ExtensionClef = ".clef";
        private const string ExtensionClefGz = ".clef.gz";

        public bool IsDefaultHandler()
        {
            if (IsPackaged())
            {
                // When packaged (MSIX), file associations are managed by the manifest.
                // We return true to avoid prompting the user to set default, 
                // as the OS handles this during installation/updates.
                return true;
            }

            // We consider it default if .clef is associated. 
            // .clef.gz is tricky because of double extension, so we focus on .clef for the check
            // but we will try to register both.
            return CheckExtension(ExtensionClef);
        }

        private bool CheckExtension(string extension)
        {
            try
            {
                // Check if extension points to our AppId
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}");
                if (key == null) return false;
                
                var val = key.GetValue(null) as string;
                if (val != AppId) return false;

                // Check if our AppId points to current exe
                using var appKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{AppId}\shell\open\command");
                if (appKey == null) return false;

                var command = appKey.GetValue(null) as string;
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                
                if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(currentExe)) return false;

                return command.Contains(currentExe, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public void SetAsDefault()
        {
            if (IsPackaged())
            {
                // Packaged apps cannot write to HKCU\Software\Classes directly for associations.
                // The manifest handles this.
                return;
            }

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            try 
            {
                RegisterAppId(exePath);
                RegisterExtension(ExtensionClef);
                // Attempt to register .clef.gz, though Windows might treat it as .gz
                RegisterExtension(ExtensionClefGz);
                
                // Notify shell of change
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set association: {ex.Message}");
            }
        }

        private bool IsPackaged()
        {
            try
            {
                // Checks if the process is running with identity (MSIX/Store)
                return Windows.ApplicationModel.Package.Current != null;
            }
            catch
            {
                return false;
            }
        }

        private void RegisterAppId(string exePath)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{AppId}");
            if (key != null)
            {
                key.SetValue(null, "Reader Log File");
                key.SetValue("Icon", $"\"{exePath}\",0");

                using var shell = key.CreateSubKey("shell");
                using var open = shell.CreateSubKey("open");
                using var command = open.CreateSubKey("command");
                command.SetValue(null, $"\"{exePath}\" \"%1\"");
            }
        }

        private void RegisterExtension(string extension)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
            if (key != null)
            {
                key.SetValue(null, AppId);
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
