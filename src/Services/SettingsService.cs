using System;
using System.Text.Json;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    public class SettingsService
    {
        private const string FileName = "settings.json";

        private readonly AppStorage _storage;
        private Settings _settings = new();

        public event Action? Changed;

        /// <summary>Erro na última tentativa de leitura/gravação, ou <c>null</c> se correu bem.</summary>
        public string? LastError { get; private set; }

        public SettingsService(AppStorage storage)
        {
            _storage = storage;
            LoadSettings();
        }

        public Settings Settings => _settings;

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                _storage.WriteText(FileName, json);
                LastError = null;
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Error("Falha ao salvar as configurações", ex);
                LastError = $"Não foi possível salvar as configurações: {ex.Message}";
            }
        }

        private void LoadSettings()
        {
            try
            {
                var json = _storage.ReadText(FileName);
                if (json is null) return;

                _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                LastError = null;
            }
            catch (Exception ex)
            {
                // Não sobrescrever o arquivo do usuário: move para .corrupt para que a próxima
                // gravação não apague silenciosamente o conteúdo que não conseguimos ler.
                AppLog.Error($"Configurações inválidas em '{FileName}'", ex);
                var quarantined = _storage.Quarantine(FileName);
                _settings = new Settings();
                LastError = quarantined is null
                    ? $"Configurações inválidas foram ignoradas: {ex.Message}"
                    : $"Configurações inválidas foram movidas para {quarantined}: {ex.Message}";
            }
        }
    }
}
