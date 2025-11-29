using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    public class LogGroupService
    {
        private readonly string _storagePath;
        private List<LogGroup> _groups = new();

        public event Action? Changed;

        public LogGroupService()
        {
            //var appFolder = AppDomain.CurrentDomain.BaseDirectory;
            var appFolder = Directory.GetCurrentDirectory();
            _storagePath = Path.Combine(appFolder, "groups.json");
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
            if (File.Exists(_storagePath))
            {
                try
                {
                    var json = File.ReadAllText(_storagePath);
                    _groups = JsonSerializer.Deserialize<List<LogGroup>>(json) ?? new List<LogGroup>();
                }
                catch
                {
                    _groups = new List<LogGroup>();
                }
            }
        }

        private void SaveGroups()
        {
            try
            {
                var json = JsonSerializer.Serialize(_groups, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch
            {
                // Ignore errors for now
            }
        }
    }
}
