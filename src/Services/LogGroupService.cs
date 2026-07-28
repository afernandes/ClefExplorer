using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    public class LogGroupService
    {
        private const string FileName = "groups.json";

        private readonly AppStorage _storage;
        private List<LogGroup> _groups = new();

        public event Action? Changed;

        /// <summary>Erro na última tentativa de leitura/gravação, ou <c>null</c> se correu bem.</summary>
        public string? LastError { get; private set; }

        public LogGroupService(AppStorage storage)
        {
            _storage = storage;
            LoadGroups();
        }

        public IReadOnlyList<LogGroup> Groups => _groups;

        public void AddGroup(LogGroup group)
        {
            _groups.Add(group);
            SaveGroups();
            Changed?.Invoke();
        }

        public void UpdateGroup(LogGroup group)
        {
            var index = _groups.FindIndex(g => g.Id == group.Id);
            if (index >= 0)
            {
                _groups[index] = group;
                SaveGroups();
                Changed?.Invoke();
            }
        }

        public void DeleteGroup(string id)
        {
            var group = _groups.FirstOrDefault(g => g.Id == id);
            if (group != null)
            {
                _groups.Remove(group);
                SaveGroups();
                Changed?.Invoke();
            }
        }

        private void LoadGroups()
        {
            try
            {
                var json = _storage.ReadText(FileName);
                if (json is null) return;

                _groups = JsonSerializer.Deserialize<List<LogGroup>>(json) ?? new List<LogGroup>();
                LastError = null;
            }
            catch (Exception ex)
            {
                // Um groups.json inválido zerava a lista em memória e a próxima gravação
                // apagava os grupos do usuário. Agora o arquivo vai para .corrupt e o erro
                // fica registrado para ser exibido.
                AppLog.Error($"Arquivo de grupos inválido em '{FileName}'", ex);
                var quarantined = _storage.Quarantine(FileName);
                _groups = new List<LogGroup>();
                LastError = quarantined is null
                    ? $"Arquivo de grupos inválido foi ignorado: {ex.Message}"
                    : $"Arquivo de grupos inválido foi movido para {quarantined}: {ex.Message}";
            }
        }

        private void SaveGroups()
        {
            try
            {
                var json = JsonSerializer.Serialize(_groups, new JsonSerializerOptions { WriteIndented = true });
                _storage.WriteText(FileName, json);
                LastError = null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Falha ao salvar os grupos", ex);
                LastError = $"Não foi possível salvar os grupos: {ex.Message}";
            }
        }
    }
}
