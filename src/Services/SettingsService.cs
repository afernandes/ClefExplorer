using System;
using System.IO;
using System.Text.Json;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    public class SettingsService
    {
        private readonly string _storagePath;
        private Settings _settings = new();

        public event Action? Changed;

        public SettingsService()
        {
            //var appFolder = AppDomain.CurrentDomain.BaseDirectory;
            var appFolder = Directory.GetCurrentDirectory();
            _storagePath = Path.Combine(appFolder, "settings.json");
            LoadSettings();
        }

        public Settings Settings => _settings;

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
                Changed?.Invoke();
            }
            catch
            {
                // Ignore errors
            }
        }

        private void LoadSettings()
        {
            if (File.Exists(_storagePath))
            {
                try
                {
                    var json = File.ReadAllText(_storagePath);
                    _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
                catch
                {
                    _settings = new Settings();
                }
            }
        }
    }
}
