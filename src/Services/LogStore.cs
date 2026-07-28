using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
        private readonly List<string> _loadedFiles = new();
        private readonly List<string> _availableFiles = new();
        private readonly List<LoadFailure> _loadFailures = new();

        public event Action? Changed;

        /// <summary>
        /// Disparado quando um conjunto NOVO de caminhos foi carregado — e não quando o
        /// usuário apenas (des)marca arquivos na árvore (<see cref="UpdateLoadedFiles"/>).
        /// Permite à UI limpar a seleção de arquivos visíveis sem interferir na interação
        /// com a árvore, cuja fonte precisa ficar estável durante a marcação.
        /// </summary>
        public event Action? PathsLoaded;

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

        /// <summary>Quantidade de eventos carregados, lida sob lock.</summary>
        public int Count
        {
            get { lock (_events) { return _events.Count; } }
        }

        /// <summary>
        /// Cópia consistente dos eventos para enumeração segura fora do lock.
        /// Evita a race com <see cref="UpdateLoadedFiles"/>/<see cref="LoadFromPathsAsync"/>,
        /// que mutam a lista em outra thread (enumerar a List viva durante mutação é
        /// comportamento indefinido e pode duplicar/saltar elementos ou lançar).
        /// </summary>
        public ClefEvent[] Snapshot()
        {
            lock (_events) { return _events.ToArray(); }
        }
        public string? FileName => _fileName;

        /// <summary>
        /// Arquivos atualmente carregados. Devolve uma CÓPIA sob o mesmo lock das mutações:
        /// estas listas são alteradas em background por <see cref="LoadFromPathsAsync"/> e
        /// <see cref="UpdateLoadedFiles"/>, enquanto a UI as enumera ao reagir a
        /// <see cref="Changed"/> — expor a lista viva é a mesma armadilha que causou o
        /// crash de chave duplicada na lista de eventos.
        /// </summary>
        public IReadOnlyList<string> LoadedFiles
        {
            get { lock (_events) { return _loadedFiles.ToArray(); } }
        }

        /// <summary>Arquivos encontrados nos caminhos abertos (marcados ou não). Também uma cópia.</summary>
        public IReadOnlyList<string> AvailableFiles
        {
            get { lock (_events) { return _availableFiles.ToArray(); } }
        }

        /// <summary>
        /// Caminhos que não puderam ser lidos no último carregamento, com o motivo.
        /// Antes essas falhas eram engolidas por <c>catch { }</c> e o usuário só via
        /// "faltando eventos", sem saber que um arquivo tinha ficado de fora.
        /// </summary>
        public IReadOnlyList<LoadFailure> LoadFailures
        {
            get { lock (_loadFailures) { return _loadFailures.ToArray(); } }
        }

        private void ClearFailures()
        {
            lock (_loadFailures) { _loadFailures.Clear(); }
        }

        private void RecordFailure(string path, Exception ex)
        {
            AppLog.Warning($"Falha ao ler '{path}'", ex);
            lock (_loadFailures) { _loadFailures.Add(new LoadFailure(path, ex.Message)); }
        }

        public async Task LoadFromFile(string path)
        {
            await LoadFromPathsAsync(new[] { path });
        }

        public async Task LoadFromFolderAsync(string folder)
        {
            await LoadFromPathsAsync(new[] { folder });
        }

        /// <summary>
        /// Cancela o carregamento em andamento, se houver. Um novo carregamento cancela o
        /// anterior automaticamente — abrir uma pasta enorme por engano não deve prender o
        /// usuário até o fim.
        /// </summary>
        public void CancelLoad()
        {
            var cts = Volatile.Read(ref _loadCts);
            if (cts is null) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        private CancellationTokenSource? _loadCts;

        public async Task LoadFromPathsAsync(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();

            // Cada carregamento é dono do próprio CTS e o descarta no finally; o anterior é
            // apenas cancelado, pois aquela execução ainda pode estar lendo o token.
            var cts = new CancellationTokenSource();
            var anterior = Interlocked.Exchange(ref _loadCts, cts);
            if (anterior is not null)
            {
                try { anterior.Cancel(); } catch (ObjectDisposedException) { }
            }
            var token = cts.Token;

            IsLoading = true;
            ClearFailures();
            Changed?.Invoke();

            try
            {
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
                    catch (Exception ex)
                    {
                        RecordFailure(rawPath, ex);
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
                        catch (Exception ex)
                        {
                            RecordFailure(path, ex);
                        }
                    }
                    else
                    {
                        RecordFailure(path, new FileNotFoundException("Caminho não encontrado."));
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

                var opcoes = new ParallelOptions { CancellationToken = token };
                await Parallel.ForEachAsync(filesToLoad, opcoes, async (file, ct) =>
                {
                    try
                    {
                        await ReadFileEvents(file, tempEvents, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // cancelamento não é falha de leitura
                    }
                    catch (Exception ex)
                    {
                        // Um arquivo ilegível não aborta os demais, mas fica registrado
                        // para ser reportado ao usuário no fim do carregamento.
                        RecordFailure(file, ex);
                    }
                });

                token.ThrowIfCancellationRequested();

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
            }, token);

            IsLoading = false;
            // PathsLoaded antes de Changed: assim quem escuta já limpou a seleção de
            // arquivos visíveis quando a UI for recalcular os filtros.
            PathsLoaded?.Invoke();
            Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Cancelado pelo usuário ou substituído por outro carregamento: o estado
                // anterior é preservado, pois só o trocamos ao final de uma leitura completa.
                AppLog.Info("Carregamento cancelado.");
                IsLoading = false;
                Changed?.Invoke();
            }
            finally
            {
                Interlocked.CompareExchange(ref _loadCts, null, cts);
                cts.Dispose();
            }
        }

        public async Task UpdateLoadedFiles(IEnumerable<string> newSelection)
        {
            // OrdinalIgnoreCase: no Windows os caminhos não diferenciam maiúsculas, e o
            // comparador padrão faria "C:\Logs\a.clef" e "C:\logs\a.clef" parecerem arquivos
            // distintos — recarregando um já carregado e nunca removendo o outro.
            // É o mesmo comparador já usado em explicitFiles e no cache de offsets.
            var newSet = new HashSet<string>(newSelection, StringComparer.OrdinalIgnoreCase);
            var currentSet = new HashSet<string>(LoadedFiles, StringComparer.OrdinalIgnoreCase);

            var toAdd = newSet.Except(currentSet).ToList();
            var toRemove = currentSet.Except(newSet).ToList();
            
            if (!toAdd.Any() && !toRemove.Any()) return;

            IsLoading = true;
            ClearFailures();
            Changed?.Invoke();
            
            // Conjunto (e não List.Contains) para a remoção também ignorar maiúsculas e
            // não ficar O(n×m) quando há muitos arquivos.
            var removeSet = new HashSet<string>(toRemove, StringComparer.OrdinalIgnoreCase);

            await Task.Run(async () => {
                 // Remove events
                 if (toRemove.Any())
                 {
                     lock(_events)
                     {
                         _events.RemoveAll(e => e.SourceFile != null && removeSet.Contains(e.SourceFile));
                         _loadedFiles.RemoveAll(removeSet.Contains);
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
                         catch (Exception ex)
                         {
                             RecordFailure(file, ex);
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

        private async Task ReadFileEvents(string file, ConcurrentBag<ClefEvent> eventsBag, CancellationToken token = default)
        {
             if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
             {
                 await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                 await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                 using var sr = new StreamReader(gz);
                 var reader = new LogEventReader(sr);
                 while (reader.TryRead(out var logEvent))
                 {
                     // Checado por evento: um único .clef.gz pode ter centenas de milhares
                     // de linhas, e cancelar só entre arquivos não daria resposta imediata.
                     token.ThrowIfCancellationRequested();

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
                     token.ThrowIfCancellationRequested();

                     var ev = MapLogEvent(logEvent, file);
                     if (!IsLogIgnored(ev))
                     {
                         eventsBag.Add(ev);
                     }
                 }

                 // Offset registrado DEPOIS de ler, com a posição realmente consumida.
                 // Guardar fs.Length antes da leitura duplicava eventos no modo tail: se o
                 // arquivo crescesse durante o carregamento, o StreamReader consumia além
                 // daquele comprimento e o tail relia o excedente.
                 RememberOffset(file, fs.Position);
             }
        }

        // --- Modo tail (acompanhar arquivos ao vivo) ---------------------------------

        private readonly Dictionary<string, long> _fileOffsets = new(StringComparer.OrdinalIgnoreCase);
        // Qualificado: System.Windows.Forms.Timer também está em escopo e exige a bomba de
        // mensagens da UI — aqui a sondagem precisa rodar em background.
        private System.Threading.Timer? _tailTimer;
        private int _tailBusy; // 0 = livre, 1 = varredura em andamento

        private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);

        /// <summary>Quanto o modo tail lê de cada arquivo por varredura (16 MB).</summary>
        private const int MaxTailReadBytes = 16 * 1024 * 1024;

        /// <summary>Indica se o aplicativo está acompanhando os arquivos carregados.</summary>
        public bool TailEnabled { get; private set; }

        private void RememberOffset(string file, long offset)
        {
            lock (_fileOffsets) { _fileOffsets[file] = offset; }
        }

        /// <summary>
        /// Liga/desliga o acompanhamento ao vivo. Usamos sondagem periódica em vez de
        /// <c>FileSystemWatcher</c>: o watcher perde eventos quando o buffer estoura e
        /// depende de o escritor atualizar os metadados, o que loggers com buffer nem
        /// sempre fazem na hora. Comparar o tamanho do arquivo é barato e previsível.
        /// </summary>
        public void SetTailEnabled(bool enabled)
        {
            if (TailEnabled == enabled) return;
            TailEnabled = enabled;

            if (enabled)
            {
                _tailTimer = new System.Threading.Timer(_ => _ = PollTailAsync(), null, TailInterval, TailInterval);
            }
            else
            {
                _tailTimer?.Dispose();
                _tailTimer = null;
            }

            Changed?.Invoke();
        }

        private async Task PollTailAsync()
        {
            // Uma varredura por vez: se a anterior ainda roda (pasta lenta, muitos
            // arquivos), pular este tique é melhor do que empilhar leituras.
            if (Interlocked.Exchange(ref _tailBusy, 1) == 1) return;

            try
            {
                if (IsLoading) return;

                string[] arquivos;
                lock (_events) { arquivos = _loadedFiles.ToArray(); }

                var novos = new List<ClefEvent>();
                foreach (var file in arquivos)
                {
                    // Um .gz é um arquivo fechado: não cresce, não há o que acompanhar.
                    if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        novos.AddRange(await ReadNewEventsAsync(file));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning($"Falha ao acompanhar '{file}'", ex);
                    }
                }

                if (novos.Count == 0) return;

                // Merge em vez de reordenar tudo: a lista já está ordenada e o tail roda a
                // cada segundo, então um Sort completo custaria O(n log n) sobre TODOS os
                // eventos carregados só para inserir alguns poucos novos.
                novos.Sort(PorTimestampDecrescente);

                lock (_events)
                {
                    MergeOrdenado(_events, novos);
                }

                Changed?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _tailBusy, 0);
            }
        }

        /// <summary>Ordem de exibição: do mais recente para o mais antigo.</summary>
        private static readonly Comparison<ClefEvent> PorTimestampDecrescente =
            (a, b) => Nullable.Compare(b.Timestamp, a.Timestamp);

        /// <summary>
        /// Intercala <paramref name="novos"/> (já ordenado) em <paramref name="destino"/>
        /// (também ordenado), em O(n+m) — em vez de concatenar e reordenar.
        /// </summary>
        private static void MergeOrdenado(List<ClefEvent> destino, List<ClefEvent> novos)
        {
            var resultado = new List<ClefEvent>(destino.Count + novos.Count);
            int i = 0, j = 0;

            while (i < destino.Count && j < novos.Count)
            {
                // <= 0 mantém os já existentes à frente em caso de empate (estabilidade).
                resultado.Add(PorTimestampDecrescente(destino[i], novos[j]) <= 0 ? destino[i++] : novos[j++]);
            }

            while (i < destino.Count) resultado.Add(destino[i++]);
            while (j < novos.Count) resultado.Add(novos[j++]);

            destino.Clear();
            destino.AddRange(resultado);
        }

        /// <summary>
        /// Lê apenas o que foi acrescentado ao arquivo desde a última leitura. Só consome
        /// até a última quebra de linha: uma linha ainda sendo escrita seria JSON
        /// incompleto e envenenaria o parser.
        /// </summary>
        private async Task<List<ClefEvent>> ReadNewEventsAsync(string file)
        {
            var resultado = new List<ClefEvent>();

            long offset;
            lock (_fileOffsets) { _fileOffsets.TryGetValue(file, out offset); }

            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Arquivo menor que o offset = foi truncado ou rotacionado: recomeça do zero.
            if (fs.Length < offset) offset = 0;
            if (fs.Length == offset) return resultado;

            // Teto por varredura: sem ele, um arquivo que cresceu centenas de MB (ou uma
            // rotação que zera o offset de um log gigante) tentaria alocar tudo de uma vez.
            // O excedente entra no próximo tique, um segundo depois.
            var pendente = Math.Min(fs.Length - offset, MaxTailReadBytes);

            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[(int)pendente];
            var lidos = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (lidos <= 0) return resultado;

            // '\n' não aparece no meio de uma sequência UTF-8 multibyte, então cortar aqui
            // nunca parte um caractere.
            var ultimaQuebra = Array.LastIndexOf(buffer, (byte)'\n', lidos - 1);
            if (ultimaQuebra < 0) return resultado; // ainda não há uma linha completa

            var texto = Encoding.UTF8.GetString(buffer, 0, ultimaQuebra + 1);

            using var sr = new StringReader(texto);
            var reader = new LogEventReader(sr);
            while (reader.TryRead(out var logEvent))
            {
                var ev = MapLogEvent(logEvent, file);
                if (!IsLogIgnored(ev))
                {
                    resultado.Add(ev);
                }
            }

            RememberOffset(file, offset + ultimaQuebra + 1);
            return resultado;
        }

        // Cache dos padrões de exclusão já compilados. Antes, o wildcard era convertido em
        // regex e interpretado para CADA arquivo × CADA padrão: numa pasta com centenas de
        // logs e alguns padrões, isso é dezenas de milhares de compilações por carregamento.
        private Regex[]? _ignoredFileRegexes;
        private string[]? _ignoredFilePatternsSnapshot;
        private readonly object _ignoredFilesGate = new();

        private Regex[] GetIgnoredFileRegexes()
        {
            // Cópia antes de qualquer enumeração: Settings.IgnoredFilePatterns é a lista
            // viva que a tela de configurações muta, enquanto o carregamento roda em
            // background — enumerá-la direto lançaria "Collection was modified".
            var patterns = _settingsService.Settings.IgnoredFilePatterns.ToArray();

            lock (_ignoredFilesGate)
            {
                // Reconstrói só quando as configurações mudam.
                if (_ignoredFileRegexes is not null
                    && _ignoredFilePatternsSnapshot is not null
                    && _ignoredFilePatternsSnapshot.SequenceEqual(patterns))
                {
                    return _ignoredFileRegexes;
                }

                var compilados = new List<Regex>(patterns.Length);
                foreach (var pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;

                    var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    try
                    {
                        compilados.Add(new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning($"Padrão de arquivo ignorado é inválido: '{pattern}'", ex);
                    }
                }

                _ignoredFilePatternsSnapshot = patterns;
                _ignoredFileRegexes = compilados.ToArray();
                return _ignoredFileRegexes;
            }
        }

        private bool IsFileIgnored(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            foreach (var regex in GetIgnoredFileRegexes())
            {
                if (regex.IsMatch(fileName)) return true;
            }
            return false;
        }

        private bool IsLogIgnored(ClefEvent ev)
        {
            // Cópia pelo mesmo motivo de GetIgnoredFileRegexes: a lista é mutada pela tela
            // de configurações enquanto isto roda em background, por evento lido.
            foreach (var text in _settingsService.Settings.IgnoredLogLines.ToArray())
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
