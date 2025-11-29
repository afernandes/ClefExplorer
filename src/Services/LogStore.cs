using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClefExplorer.Models;
using Serilog.Events;
using Serilog.Formatting.Compact.Reader;

namespace ClefExplorer.Services
{
    public class LogStore
    {
        private readonly SettingsService _settingsService;
        private readonly List<ClefEvent> _events = new();
        private string? _fileName;
        private string _filter = string.Empty;
        private readonly List<string> _loadedFiles = new();
        private readonly List<string> _availableFiles = new();

        public event Action? Changed;

        public LogStore(SettingsService settingsService)
        {
            _settingsService = settingsService;
            _settingsService.Changed += () => 
            {
                if (_loadedFiles.Any())
                {
                    _ = LoadFromPathsAsync(_loadedFiles.ToList());
                }
            };
        }

        public bool IsLoading { get; private set; }
        public IReadOnlyList<ClefEvent> Events => _events;
        public string? FileName => _fileName;
        public string Filter { get => _filter; set { _filter = value; Changed?.Invoke(); } }
        public IReadOnlyList<string> LoadedFiles => _loadedFiles;
        public IReadOnlyList<string> AvailableFiles => _availableFiles;

        public IReadOnlyList<ClefEvent> Filtered()
        {
            if (string.IsNullOrWhiteSpace(_filter)) return _events;
            var f = _filter.Trim().ToLowerInvariant();
            return _events.FindAll(e =>
                (e.Message ?? string.Empty).ToLowerInvariant().Contains(f) ||
                (e.Level ?? string.Empty).ToLowerInvariant().Contains(f) ||
                (e.Exception ?? string.Empty).ToLowerInvariant().Contains(f)
            );
        }

        public async Task LoadFromFile(string path)
        {
            await LoadFromPathsAsync(new[] { path });
        }

        public async Task LoadFromFolderAsync(string folder)
        {
            await LoadFromPathsAsync(new[] { folder });
        }

        public async Task LoadFromPathsAsync(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            IsLoading = true;
            Changed?.Invoke();

            await Task.Run(async () =>
            {
                var tempEvents = new ConcurrentBag<ClefEvent>();
                var allFiles = new List<string>();
                var explicitFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rawPath in pathList)
                {
                    var path = Environment.ExpandEnvironmentVariables(rawPath);
                    try
                    {
                        path = Path.GetFullPath(path);
                    }
                    catch
                    {
                        // Ignore invalid paths
                        continue;
                    }

                    if (File.Exists(path))
                    {
                        allFiles.Add(path);
                        explicitFiles.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        try 
                        {
                            var files = Directory.GetFiles(path, "*.clef", SearchOption.AllDirectories);
                            allFiles.AddRange(files);
                            
                            var gzFiles = Directory.GetFiles(path, "*.clef.gz", SearchOption.AllDirectories);
                            allFiles.AddRange(gzFiles);
                        }
                        catch 
                        {
                            // Ignore access errors
                        }
                    }
                }
                
                // Filter ignored files AND exclude .gz files from initial load (they should be unchecked by default)
                // UNLESS they were explicitly requested
                var filesToLoad = new List<string>();
                foreach (var f in allFiles)
                {
                    if (explicitFiles.Contains(f))
                    {
                        filesToLoad.Add(f);
                    }
                    else if (!IsFileIgnored(f) && !f.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    {
                        filesToLoad.Add(f);
                    }
                }

                await Parallel.ForEachAsync(filesToLoad, async (file, _) =>
                {
                    try
                    {
                        await ReadFileEvents(file, tempEvents);
                    }
                    catch (Exception)
                    {
                        // ignore files that can't be read
                    }
                });

                lock (_events)
                {
                    _events.Clear();
                    _events.AddRange(tempEvents);
                    _events.Sort((a, b) => Nullable.Compare(b.Timestamp, a.Timestamp));
                    _fileName = pathList.Count == 1 ? pathList[0] : "Múltiplos locais";
                    
                    _availableFiles.Clear();
                    _availableFiles.AddRange(allFiles);
                    
                    _loadedFiles.Clear();
                    _loadedFiles.AddRange(filesToLoad);
                }
            });

            IsLoading = false;
            Changed?.Invoke();
        }

        public async Task UpdateLoadedFiles(IEnumerable<string> newSelection)
        {
            var newSet = new HashSet<string>(newSelection);
            var currentSet = new HashSet<string>(_loadedFiles);
            
            var toAdd = newSet.Except(currentSet).ToList();
            var toRemove = currentSet.Except(newSet).ToList();
            
            if (!toAdd.Any() && !toRemove.Any()) return;
            
            IsLoading = true;
            Changed?.Invoke();
            
            await Task.Run(async () => {
                 // Remove events
                 if (toRemove.Any())
                 {
                     lock(_events)
                     {
                         _events.RemoveAll(e => e.SourceFile != null && toRemove.Contains(e.SourceFile));
                         _loadedFiles.RemoveAll(f => toRemove.Contains(f));
                     }
                 }
                 
                 // Add events
                 if (toAdd.Any())
                 {
                     var newEvents = new ConcurrentBag<ClefEvent>();
                     await Parallel.ForEachAsync(toAdd, async (file, _) => {
                         try
                         {
                             await ReadFileEvents(file, newEvents);
                         }
                         catch
                         {
                             // ignore
                         }
                     });
                     
                     lock(_events)
                     {
                         _events.AddRange(newEvents);
                         _events.Sort((a, b) => Nullable.Compare(b.Timestamp, a.Timestamp));
                         _loadedFiles.AddRange(toAdd);
                     }
                 }
            });
            
            IsLoading = false;
            Changed?.Invoke();
        }

        private async Task ReadFileEvents(string file, ConcurrentBag<ClefEvent> eventsBag)
        {
             if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
             {
                 await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                 await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                 using var sr = new StreamReader(gz);
                 var reader = new LogEventReader(sr);
                 while (reader.TryRead(out var logEvent))
                 {
                     var ev = MapLogEvent(logEvent, file);
                     if (!IsLogIgnored(ev))
                     {
                         eventsBag.Add(ev);
                     }
                 }
             }
             else
             {
                 await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                 using var sr = new StreamReader(fs);
                 var reader = new LogEventReader(sr);
                 while (reader.TryRead(out var logEvent))
                 {
                     var ev = MapLogEvent(logEvent, file);
                     if (!IsLogIgnored(ev))
                     {
                         eventsBag.Add(ev);
                     }
                 }
             }
        }

        // Deprecated synchronous method, kept for compatibility if needed, but redirects to async logic if possible or just does sync work
        public void LoadFromFolder(string folder)
        {
            LoadFromFolderAsync(folder).GetAwaiter().GetResult();
        }

        private bool IsFileIgnored(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            foreach (var pattern in _settingsService.Settings.IgnoredFilePatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                // Simple wildcard to regex conversion
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                if (Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsLogIgnored(ClefEvent ev)
        {
            foreach (var text in _settingsService.Settings.IgnoredLogLines)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if ((ev.Message ?? "").Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    (ev.Exception ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static ClefEvent MapLogEvent(LogEvent le, string sourceFile)
        {
            var ev = new ClefEvent
            {
                Timestamp = le.Timestamp,
                Level = le.Level.ToString(),
                MessageTemplate = le.MessageTemplate.Text,
                Message = le.RenderMessage(),
                Exception = le.Exception?.ToString(),
                SourceFile = sourceFile,
                Properties = new Dictionary<string, LogEventPropertyValue>(le.Properties.Count, StringComparer.OrdinalIgnoreCase)
            };

            foreach (var kvp in le.Properties)
            {
                ev.Properties[kvp.Key] = kvp.Value;
            }

            return ev;
        }
    }
}
