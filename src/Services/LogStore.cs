using System.Collections.Concurrent;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Mantém a sessão de logs exibida. Descoberta e parsing vivem em serviços próprios;
    /// esta classe coordena operações, publica snapshots atômicos e acompanha o tail.
    /// </summary>
    public sealed class LogStore : IDisposable
    {
        private readonly SettingsService _settingsService;
        private readonly DescobertaArquivosLog _descoberta;
        private readonly ILeitorArquivoLog _leitor;
        private readonly FiltroArquivosLogIgnorados _filtroArquivos;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _operationGate = new(1, 1);

        // Estado publicado por cópia-na-escrita: a escrita continua serializada pelo
        // _stateGate, mas quem lê só pega a referência já pronta. Antes cada acesso copiava
        // a coleção inteira segurando o _stateGate — 8 MB e 1,4 ms por Snapshot() com 1
        // milhão de eventos, com a thread da UI esperando o merge do tail terminar, e um
        // array NOVO de LoadedFiles a cada render, que fazia o Blazor redesenhar a árvore
        // de arquivos inteira mesmo sem nada ter mudado.
        private ClefEvent[] _events = Array.Empty<ClefEvent>();
        private string[] _loadedFiles = Array.Empty<string>();
        private string[] _availableFiles = Array.Empty<string>();
        private LoadFailure[] _loadFailures = Array.Empty<LoadFailure>();
        private string[] _openedPaths = Array.Empty<string>();
        private string? _fileName;
        private string? _loadProgressText;

        private CancellationTokenSource? _operationCts;
        private int _isLoading;
        private long _stateVersion;
        private bool _disposed;

        public event Action? Changed;

        /// <summary>
        /// Disparado quando um conjunto novo de caminhos foi carregado — não quando o
        /// usuário apenas (des)marca arquivos na árvore.
        /// </summary>
        public event Action? PathsLoaded;

        public LogStore(
            SettingsService settingsService,
            DescobertaArquivosLog descoberta,
            ILeitorArquivoLog leitor,
            FiltroArquivosLogIgnorados filtroArquivos)
        {
            _settingsService = settingsService;
            _descoberta = descoberta;
            _leitor = leitor;
            _filtroArquivos = filtroArquivos;
            _settingsService.Changed += OnSettingsChanged;
        }

        /// <summary>Construtor preservado para testes e integrações que criam o store diretamente.</summary>
        public LogStore(SettingsService settingsService)
            : this(
                settingsService,
                new DescobertaArquivosLog(),
                new LeitorArquivoLog(),
                new FiltroArquivosLogIgnorados(settingsService))
        {
        }

        public bool IsLoading => Volatile.Read(ref _isLoading) == 1;

        public int Count => Volatile.Read(ref _events).Length;

        /// <summary>
        /// Devolve o conjunto publicado, sem copiar. O array é imutável por contrato: quem
        /// recebe pode ordenar uma cópia, nunca o próprio array.
        /// </summary>
        public ClefEvent[] Snapshot() => Volatile.Read(ref _events);

        public string? FileName
        {
            get { lock (_stateGate) { return _fileName; } }
        }

        public IReadOnlyList<string> LoadedFiles => Volatile.Read(ref _loadedFiles);

        public IReadOnlyList<string> AvailableFiles => Volatile.Read(ref _availableFiles);

        public IReadOnlyList<LoadFailure> LoadFailures => Volatile.Read(ref _loadFailures);

        public string? LoadProgressText
        {
            get { lock (_stateGate) { return _loadProgressText; } }
        }

        public Task LoadFromFile(string path) => LoadFromPathsAsync(new[] { path });

        public Task LoadFromFolderAsync(string folder) => LoadFromPathsAsync(new[] { folder });

        public void CancelLoad()
        {
            var cts = Volatile.Read(ref _operationCts);
            if (cts is null) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public async Task LoadFromPathsAsync(IEnumerable<string> paths)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(paths);

            var pathList = paths.ToArray();
            var operacao = StartOperation();
            var acquired = false;
            var concluida = false;

            try
            {
                await _operationGate.WaitAsync(operacao.Token).ConfigureAwait(false);
                acquired = true;

                var descoberta = await Task.Run(
                    () => _descoberta.Descobrir(
                        pathList,
                        operacao.Token,
                        quantidade => ReportProgress(
                            operacao,
                            $"Descobrindo arquivos… {quantidade:N0}")),
                    operacao.Token).ConfigureAwait(false);

                var falhas = new ConcurrentBag<LoadFailure>(descoberta.Falhas);
                foreach (var falha in descoberta.Falhas)
                {
                    AppLog.Warning($"Falha ao descobrir '{falha.Path}': {falha.Reason}");
                }

                var arquivosCarregar = descoberta.Arquivos
                    .Where(arquivo => descoberta.ArquivosExplicitos.Contains(arquivo)
                        || (!_filtroArquivos.DeveIgnorar(arquivo)
                            && !arquivo.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                var eventos = new ConcurrentBag<ClefEvent>();
                var offsets = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var textosIgnorados = SnapshotIgnoredLogLines();
                // Um pool por SESSÃO, compartilhado entre os arquivos lidos em paralelo e
                // também com o tail: os mesmos templates e chaves se repetem em todos os
                // arquivos da aplicação, e os eventos ingeridos ao vivo pagavam de novo por
                // cada nível e cada chave (23% de memória a mais). Ele morre na carga
                // seguinte — nada fica retido de uma sessão anterior.
                var pool = new PoolDeTextos();
                var opcoes = new ParallelOptions { CancellationToken = operacao.Token };
                var arquivosLidos = 0;
                var intervaloProgresso = Math.Max(1, arquivosCarregar.Length / 20);
                ReportProgress(operacao, $"Lendo 0/{arquivosCarregar.Length:N0} arquivos…");

                await Parallel.ForEachAsync(arquivosCarregar, opcoes, async (arquivo, token) =>
                {
                    try
                    {
                        var leitura = await _leitor.LerAsync(arquivo, textosIgnorados, pool, token).ConfigureAwait(false);
                        foreach (var evento in leitura.Eventos) eventos.Add(evento);
                        if (leitura.OffsetFinal is { } offset) offsets[arquivo] = offset;

                        if (leitura.LinhasInvalidas > 0)
                        {
                            falhas.Add(new LoadFailure(
                                arquivo,
                                $"{leitura.LinhasInvalidas} linha(s) CLEF inválida(s). {leitura.PrimeiroErro}".Trim()));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RegistrarFalha(falhas, arquivo, ex);
                    }
                    finally
                    {
                        var processados = Interlocked.Increment(ref arquivosLidos);
                        if (processados == arquivosCarregar.Length || processados % intervaloProgresso == 0)
                        {
                            ReportProgress(
                                operacao,
                                $"Lendo {processados:N0}/{arquivosCarregar.Length:N0} arquivos…");
                        }
                    }
                }).ConfigureAwait(false);

                operacao.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(operacao)) return;

                var eventosOrdenados = eventos.ToArray();
                Array.Sort(eventosOrdenados, PorTimestampDecrescente);

                lock (_stateGate)
                {
                    if (!IsCurrent(operacao)) return;

                    // O array publicado tem exatamente o tamanho do conteúdo. A List anterior
                    // mantinha o array interno da MAIOR sessão já aberta: fechar 5 milhões de
                    // eventos e abrir um arquivo pequeno deixava 40 MB de referências vazias
                    // presas até o aplicativo ser fechado — os eventos eram coletados, o
                    // array não.
                    Volatile.Write(ref _events, eventosOrdenados);
                    _fileName = pathList.Length == 1 ? pathList[0] : "Múltiplos locais";
                    _poolSessao = pool;

                    Volatile.Write(ref _openedPaths, pathList);
                    Volatile.Write(ref _availableFiles, descoberta.Arquivos.ToArray());
                    Volatile.Write(ref _loadedFiles, arquivosCarregar);
                    PublicarFalhas(falhas);
                    PublicarOffsets(offsets, arquivosCarregar);
                    Interlocked.Increment(ref _stateVersion);
                }

                concluida = true;
            }
            catch (OperationCanceledException)
            {
                AppLog.Info("Carregamento cancelado.");
            }
            finally
            {
                if (acquired) _operationGate.Release();
                FinishOperation(operacao, pathsLoaded: concluida);
            }
        }

        public async Task UpdateLoadedFiles(IEnumerable<string> newSelection)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(newSelection);

            var selecao = DistinctPaths(newSelection);
            var operacao = StartOperation();
            var acquired = false;

            try
            {
                await _operationGate.WaitAsync(operacao.Token).ConfigureAwait(false);
                acquired = true;

                ClefEvent[] eventosAtuais;
                string[] arquivosAtuais;
                PoolDeTextos pool;
                lock (_stateGate)
                {
                    eventosAtuais = Volatile.Read(ref _events);
                    arquivosAtuais = Volatile.Read(ref _loadedFiles);
                    // Mesmo pool da carga: remarcar um arquivo na árvore reaproveita os
                    // textos que já estão na memória em vez de duplicá-los.
                    pool = _poolSessao ??= new PoolDeTextos();
                }

                var atual = new HashSet<string>(arquivosAtuais, StringComparer.OrdinalIgnoreCase);
                var novo = new HashSet<string>(selecao, StringComparer.OrdinalIgnoreCase);
                var adicionar = selecao.Where(arquivo => !atual.Contains(arquivo)).ToArray();
                var remover = atual.Where(arquivo => !novo.Contains(arquivo)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var falhas = new ConcurrentBag<LoadFailure>();
                var adicionados = new ConcurrentBag<ClefEvent>();
                var offsets = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var textosIgnorados = SnapshotIgnoredLogLines();
                var opcoes = new ParallelOptions { CancellationToken = operacao.Token };
                var arquivosLidos = 0;
                var intervaloProgresso = Math.Max(1, adicionar.Length / 20);
                ReportProgress(operacao, $"Atualizando 0/{adicionar.Length:N0} arquivos…");

                await Parallel.ForEachAsync(adicionar, opcoes, async (arquivo, token) =>
                {
                    try
                    {
                        var leitura = await _leitor.LerAsync(arquivo, textosIgnorados, pool, token).ConfigureAwait(false);
                        foreach (var evento in leitura.Eventos) adicionados.Add(evento);
                        if (leitura.OffsetFinal is { } offset) offsets[arquivo] = offset;
                        if (leitura.LinhasInvalidas > 0)
                        {
                            falhas.Add(new LoadFailure(
                                arquivo,
                                $"{leitura.LinhasInvalidas} linha(s) CLEF inválida(s). {leitura.PrimeiroErro}".Trim()));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RegistrarFalha(falhas, arquivo, ex);
                    }
                    finally
                    {
                        var processados = Interlocked.Increment(ref arquivosLidos);
                        if (processados == adicionar.Length || processados % intervaloProgresso == 0)
                        {
                            ReportProgress(
                                operacao,
                                $"Atualizando {processados:N0}/{adicionar.Length:N0} arquivos…");
                        }
                    }
                }).ConfigureAwait(false);

                operacao.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(operacao)) return;

                var resultado = eventosAtuais
                    .Where(evento => evento.SourceFile is null || !remover.Contains(evento.SourceFile))
                    .Concat(adicionados)
                    .ToArray();
                Array.Sort(resultado, PorTimestampDecrescente);

                lock (_stateGate)
                {
                    if (!IsCurrent(operacao)) return;

                    Volatile.Write(ref _events, resultado);
                    Volatile.Write(ref _loadedFiles, selecao);
                    PublicarFalhas(falhas);

                    lock (_fileOffsets)
                    {
                        foreach (var arquivo in remover)
                        {
                            _fileOffsets.Remove(arquivo);
                            _oversizedTailScanOffsets.Remove(arquivo);
                        }

                        foreach (var offset in offsets) _fileOffsets[offset.Key] = offset.Value;
                    }

                    Interlocked.Increment(ref _stateVersion);
                }

            }
            catch (OperationCanceledException)
            {
                AppLog.Info("Atualização da seleção de arquivos cancelada.");
            }
            finally
            {
                if (acquired) _operationGate.Release();
                FinishOperation(operacao, pathsLoaded: false);
            }
        }

        private CancellationTokenSource StartOperation()
        {
            var cts = new CancellationTokenSource();
            CancellationTokenSource? anterior;
            lock (_stateGate)
            {
                anterior = Interlocked.Exchange(ref _operationCts, cts);
                Volatile.Write(ref _isLoading, 1);
                Volatile.Write(ref _loadFailures, Array.Empty<LoadFailure>());
                _loadProgressText = "Preparando carregamento…";
            }

            if (anterior is not null)
            {
                try { anterior.Cancel(); } catch (ObjectDisposedException) { }
            }
            Changed?.Invoke();
            return cts;
        }

        private bool IsCurrent(CancellationTokenSource operacao) =>
            ReferenceEquals(Volatile.Read(ref _operationCts), operacao);

        private void FinishOperation(CancellationTokenSource operacao, bool pathsLoaded)
        {
            bool eraAtual;
            lock (_stateGate)
            {
                eraAtual = ReferenceEquals(
                    Interlocked.CompareExchange(ref _operationCts, null, operacao),
                    operacao);
                if (eraAtual)
                {
                    Volatile.Write(ref _isLoading, 0);
                    _loadProgressText = null;
                }
            }

            if (eraAtual)
            {
                if (pathsLoaded) PathsLoaded?.Invoke();
                Changed?.Invoke();
            }

            operacao.Dispose();
        }

        private void ReportProgress(CancellationTokenSource operacao, string text)
        {
            if (!IsCurrent(operacao)) return;
            lock (_stateGate)
            {
                if (!IsCurrent(operacao)) return;
                _loadProgressText = text;
            }
            Changed?.Invoke();
        }

        private static string[] DistinctPaths(IEnumerable<string> paths)
        {
            var resultado = new List<string>();
            var conhecidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) && conhecidos.Add(path)) resultado.Add(path);
            }
            return resultado.ToArray();
        }

        private void OnSettingsChanged()
        {
            if (_disposed) return;
            var caminhos = Volatile.Read(ref _openedPaths);
            if (caminhos.Length > 0) _ = LoadFromPathsAsync(caminhos);
        }

        private static void RegistrarFalha(
            ConcurrentBag<LoadFailure> falhas,
            string arquivo,
            Exception exception)
        {
            AppLog.Warning($"Falha ao ler '{arquivo}'", exception);
            falhas.Add(new LoadFailure(arquivo, exception.Message));
        }

        private void PublicarFalhas(IEnumerable<LoadFailure> falhas) =>
            Volatile.Write(
                ref _loadFailures,
                falhas.OrderBy(falha => falha.Path, StringComparer.OrdinalIgnoreCase).ToArray());

        private string[] SnapshotIgnoredLogLines() =>
            _settingsService.Settings.IgnoredLogLines.ToArray();

        private static string[] Concatenar(string[] atuais, IReadOnlyList<string> novos)
        {
            var resultado = new string[atuais.Length + novos.Count];
            atuais.CopyTo(resultado, 0);
            for (var i = 0; i < novos.Count; i++) resultado[atuais.Length + i] = novos[i];
            return resultado;
        }

        // --- Modo tail -------------------------------------------------------------

        private readonly Dictionary<string, long> _fileOffsets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _oversizedTailScanOffsets = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _tailCts;
        private Task? _tailTask;
        private int _tailEnabled;
        private PoolDeTextos? _poolSessao;

        private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);

        // A procura por arquivos novos tem cadência PRÓPRIA porque ela custa uma varredura
        // recursiva da árvore que o usuário abriu (medida em 29 s numa pasta com 738 mil
        // entradas), enquanto o poll precisa rodar a cada segundo. Sem ela o acompanhamento
        // morria em silêncio na virada do dia: em C:\TOTVSPDV\Logs a rotação é por NOME
        // (…log<AAAAMMDD>.clef), então à meia-noite os arquivos vigiados param de crescer e
        // os do dia seguinte nunca eram descobertos.
        private static readonly TimeSpan IntervaloRedescoberta = TimeSpan.FromSeconds(30);

        private static readonly EnumerationOptions OpcoesTamanhos = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        private const int MaxTailReadBytes = 16 * 1024 * 1024;
        private const int TailScanBufferBytes = 64 * 1024;

        public bool TailEnabled => Volatile.Read(ref _tailEnabled) == 1;

        public void SetTailEnabled(bool enabled)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (enabled)
            {
                if (Interlocked.Exchange(ref _tailEnabled, 1) == 1) return;
                var cts = new CancellationTokenSource();
                var anterior = Interlocked.Exchange(ref _tailCts, cts);
                if (anterior is not null)
                {
                    try { anterior.Cancel(); } catch (ObjectDisposedException) { }
                }
                _tailTask = RunTailAsync(cts);
            }
            else
            {
                if (Interlocked.Exchange(ref _tailEnabled, 0) == 0) return;
                var atual = Interlocked.Exchange(ref _tailCts, null);
                if (atual is not null)
                {
                    try { atual.Cancel(); } catch (ObjectDisposedException) { }
                }
            }

            Changed?.Invoke();
        }

        private async Task RunTailAsync(CancellationTokenSource owner)
        {
            try
            {
                using var timer = new PeriodicTimer(TailInterval);
                var proximaRedescoberta = 0L;
                while (await timer.WaitForNextTickAsync(owner.Token).ConfigureAwait(false))
                {
                    if (Environment.TickCount64 >= proximaRedescoberta)
                    {
                        var inicio = Environment.TickCount64;
                        await RedescobrirArquivosAsync(owner.Token).ConfigureAwait(false);

                        // Recuo proporcional ao custo medido: numa árvore enorme a varredura
                        // chega a dezenas de segundos e insistir na cadência fixa deixaria a
                        // thread do tail enumerando o tempo todo, sem nunca ler os arquivos.
                        var duracao = Environment.TickCount64 - inicio;
                        proximaRedescoberta = Environment.TickCount64
                            + Math.Max((long)IntervaloRedescoberta.TotalMilliseconds, duracao * 10);
                    }

                    await PollTailAsync(owner.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested)
            {
                // Encerramento normal do acompanhamento.
            }
            catch (Exception ex)
            {
                AppLog.Error("O acompanhamento ao vivo foi interrompido", ex);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _tailCts, null, owner), owner))
                {
                    Volatile.Write(ref _tailEnabled, 0);
                    Changed?.Invoke();
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _tailCts, null, owner);
                owner.Dispose();
            }
        }

        /// <summary>
        /// Procura arquivos que apareceram depois da carga (rotação diária, contexto que
        /// começou a logar agora) e os adota no acompanhamento.
        /// </summary>
        private async Task RedescobrirArquivosAsync(CancellationToken cancellationToken)
        {
            if (IsLoading) return;

            string[] caminhos;
            string[] conhecidos;
            lock (_stateGate)
            {
                caminhos = Volatile.Read(ref _openedPaths);
                conhecidos = Volatile.Read(ref _availableFiles);
            }

            if (caminhos.Length == 0) return;

            ResultadoDescobertaArquivosLog descoberta;
            try
            {
                descoberta = await Task.Run(
                    () => _descoberta.Descobrir(caminhos, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falha ao procurar arquivos novos durante o acompanhamento", ex);
                return;
            }

            // O diff é contra os arquivos CONHECIDOS, não contra os carregados: um arquivo
            // que o usuário desmarcou na árvore continua conhecido e não pode voltar sozinho.
            var jaConhecidos = new HashSet<string>(conhecidos, StringComparer.OrdinalIgnoreCase);
            var novos = descoberta.Arquivos.Where(arquivo => !jaConhecidos.Contains(arquivo)).ToArray();
            if (novos.Length == 0) return;

            AdotarArquivosNovos(novos, cancellationToken);
        }

        private void AdotarArquivosNovos(string[] novos, CancellationToken cancellationToken)
        {
            var acompanhar = new List<string>(novos.Length);
            var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var arquivo in novos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Os ignorados e os .gz entram na árvore, mas ficam fora do acompanhamento:
                // o tail não descompacta e o filtro do usuário vale igual para quem chega
                // depois.
                if (_filtroArquivos.DeveIgnorar(arquivo)
                    || arquivo.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // Offset no FIM: o acompanhamento mostra o que for escrito daqui para a
                    // frente. Adotar do byte 0 despejaria de uma vez um arquivo inteiro que
                    // por acaso tenha sido copiado para a pasta.
                    offsets[arquivo] = new FileInfo(arquivo).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warning($"Não foi possível adotar '{arquivo}' no acompanhamento", ex);
                    continue;
                }

                acompanhar.Add(arquivo);
            }

            lock (_stateGate)
            {
                // Uma carga em andamento vai republicar a lista inteira a partir da própria
                // descoberta dela; adotar por cima só criaria duplicata.
                if (IsLoading) return;

                Volatile.Write(ref _availableFiles, Concatenar(Volatile.Read(ref _availableFiles), novos));
                if (acompanhar.Count > 0)
                {
                    Volatile.Write(ref _loadedFiles, Concatenar(Volatile.Read(ref _loadedFiles), acompanhar));
                    lock (_fileOffsets)
                    {
                        foreach (var offset in offsets) _fileOffsets[offset.Key] = offset.Value;
                    }
                }

                Interlocked.Increment(ref _stateVersion);
            }

            AppLog.Info($"O acompanhamento adotou {novos.Length} arquivo(s) que apareceram depois da carga.");
            Changed?.Invoke();
        }

        private async Task PollTailAsync(CancellationToken cancellationToken)
        {
            if (IsLoading) return;

            string[] arquivos;
            long versao;
            PoolDeTextos? pool;
            lock (_stateGate)
            {
                arquivos = Volatile.Read(ref _loadedFiles);
                versao = Volatile.Read(ref _stateVersion);
                pool = _poolSessao;
            }

            var tamanhos = TamanhosDasPastasAcompanhadas(arquivos);
            var textosIgnorados = SnapshotIgnoredLogLines();
            var novos = new List<ClefEvent>();
            var novosOffsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var arquivo in arquivos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (arquivo.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) continue;

                // Portão por "tamanho igual ao que já foi lido", e não por "tamanho mudou
                // desde o poll anterior": depois de descartar uma linha gigante o offset
                // avança sem o arquivo crescer, e um portão por variação deixaria o resto do
                // arquivo preso para sempre. Tamanho menor que o offset também abre — é
                // truncamento, e a releitura precisa reposicionar no início.
                if (tamanhos.TryGetValue(arquivo, out var tamanho) && tamanho == OffsetLido(arquivo)) continue;

                try
                {
                    var trecho = await ReadNewEventsAsync(
                        arquivo,
                        textosIgnorados,
                        pool,
                        cancellationToken).ConfigureAwait(false);
                    novos.AddRange(trecho.Eventos);
                    if (trecho.NovoOffset is { } offset) novosOffsets[arquivo] = offset;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"Falha ao acompanhar '{arquivo}'", ex);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsLoading || versao != Volatile.Read(ref _stateVersion)) return;
            novos.Sort(PorTimestampDecrescente);

            lock (_stateGate)
            {
                if (IsLoading || versao != Volatile.Read(ref _stateVersion)) return;

                lock (_fileOffsets)
                {
                    foreach (var offset in novosOffsets) _fileOffsets[offset.Key] = offset.Value;
                }

                if (novos.Count > 0)
                {
                    Volatile.Write(ref _events, MesclarPorRegiao(Volatile.Read(ref _events), novos));
                }
            }

            if (novos.Count > 0) Changed?.Invoke();
        }

        /// <summary>
        /// Nome e tamanho de tudo o que está nas pastas dos arquivos acompanhados, numa única
        /// passada por pasta.
        /// </summary>
        /// <remarks>
        /// Abrir um FileStream por arquivo só para ler <c>Length</c> custava 2 ms nos 104
        /// arquivos de C:\TOTVSPDV\Logs em disco local e 2.494 ms sobre SMB — e 65 deles são
        /// de dias já encerrados, que nunca mais mudam. A enumeração é RASA e restrita às
        /// pastas que contêm arquivos acompanhados: nunca varre a árvore inteira que o
        /// usuário abriu (essa varredura é a da redescoberta, com cadência de 30 s).
        /// Arquivo ausente do resultado (pasta inacessível, atributo pulado) cai no caminho
        /// antigo — o poll abre o arquivo —, então uma falha aqui é lenta, nunca incorreta.
        /// </remarks>
        private static Dictionary<string, long> TamanhosDasPastasAcompanhadas(string[] arquivos)
        {
            var tamanhos = new Dictionary<string, long>(arquivos.Length, StringComparer.OrdinalIgnoreCase);
            var pastas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var arquivo in arquivos)
            {
                var pasta = Path.GetDirectoryName(arquivo);
                if (string.IsNullOrEmpty(pasta) || !pastas.Add(pasta)) continue;

                try
                {
                    foreach (var info in new DirectoryInfo(pasta).EnumerateFiles("*", OpcoesTamanhos))
                    {
                        tamanhos[info.FullName] = info.Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warning($"Não foi possível enumerar '{pasta}' para o acompanhamento", ex);
                }
            }

            return tamanhos;
        }

        private long OffsetLido(string arquivo)
        {
            lock (_fileOffsets)
            {
                _fileOffsets.TryGetValue(arquivo, out var offset);
                return offset;
            }
        }

        private sealed record ResultadoTail(IReadOnlyList<ClefEvent> Eventos, long? NovoOffset);

        private async Task<ResultadoTail> ReadNewEventsAsync(
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool,
            CancellationToken cancellationToken)
        {
            long offset;
            lock (_fileOffsets) { _fileOffsets.TryGetValue(arquivo, out offset); }

            await using var stream = new FileStream(
                arquivo,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: TailScanBufferBytes,
                useAsync: true);

            if (stream.Length < offset)
            {
                offset = 0;
                lock (_fileOffsets) { _oversizedTailScanOffsets.Remove(arquivo); }
            }
            if (stream.Length == offset) return new ResultadoTail(Array.Empty<ClefEvent>(), null);

            var pendente = Math.Min(stream.Length - offset, MaxTailReadBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[(int)pendente];
            var lidos = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (lidos <= 0) return new ResultadoTail(Array.Empty<ClefEvent>(), null);

            var ultimaQuebra = Array.LastIndexOf(buffer, (byte)'\n', lidos - 1);
            if (ultimaQuebra < 0)
            {
                if (stream.Length - offset > MaxTailReadBytes)
                {
                    var depoisDaLinha = await FindEndOfOversizedLineAsync(
                        stream,
                        arquivo,
                        offset + lidos,
                        cancellationToken).ConfigureAwait(false);
                    return new ResultadoTail(Array.Empty<ClefEvent>(), depoisDaLinha);
                }

                return new ResultadoTail(Array.Empty<ClefEvent>(), null);
            }

            // O trecho vai para o leitor INJETADO, e não para o parser estático: assim um
            // leitor de teste ou uma troca de implementação valem para o acompanhamento, não
            // só para a carga. O sinalizador de início é o que autoriza descartar o BOM —
            // quando a leitura recomeça do byte 0 (arquivo truncado, ou arquivo que falhou na
            // carga e ficou sem offset), o U+FEFF colado no '{' invalidaria a primeira linha.
            var leitura = _leitor.LerTrecho(
                buffer.AsSpan(0, ultimaQuebra + 1),
                arquivo,
                textosIgnorados,
                inicioDoArquivo: offset == 0,
                pool,
                cancellationToken);

            if (leitura.LinhasInvalidas > 0)
            {
                AppLog.Warning(
                    $"Tail de '{arquivo}' ignorou {leitura.LinhasInvalidas} linha(s) CLEF inválida(s): {leitura.PrimeiroErro}");
            }

            lock (_fileOffsets) { _oversizedTailScanOffsets.Remove(arquivo); }
            return new ResultadoTail(leitura.Eventos, offset + ultimaQuebra + 1);
        }

        private async Task<long?> FindEndOfOversizedLineAsync(
            FileStream stream,
            string arquivo,
            long firstUncheckedOffset,
            CancellationToken cancellationToken)
        {
            long scanOffset;
            var primeiraDeteccao = false;
            lock (_fileOffsets)
            {
                if (!_oversizedTailScanOffsets.TryGetValue(arquivo, out scanOffset)
                    || scanOffset < firstUncheckedOffset
                    || scanOffset > stream.Length)
                {
                    scanOffset = firstUncheckedOffset;
                    _oversizedTailScanOffsets[arquivo] = scanOffset;
                    primeiraDeteccao = true;
                }
            }

            if (primeiraDeteccao)
            {
                AppLog.Warning(
                    $"Tail de '{arquivo}' descartará uma linha maior que {MaxTailReadBytes / 1024 / 1024} MB.");
            }

            stream.Seek(scanOffset, SeekOrigin.Begin);
            var buffer = new byte[TailScanBufferBytes];
            while (scanOffset < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lidos = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (lidos <= 0) break;

                var quebra = Array.IndexOf(buffer, (byte)'\n', 0, lidos);
                if (quebra >= 0)
                {
                    lock (_fileOffsets) { _oversizedTailScanOffsets.Remove(arquivo); }
                    return scanOffset + quebra + 1;
                }

                scanOffset += lidos;
                lock (_fileOffsets) { _oversizedTailScanOffsets[arquivo] = scanOffset; }
            }

            return null;
        }

        private void PublicarOffsets(
            IReadOnlyDictionary<string, long> offsets,
            IReadOnlyList<string> arquivosCarregados)
        {
            lock (_fileOffsets)
            {
                _fileOffsets.Clear();
                _oversizedTailScanOffsets.Clear();
                foreach (var arquivo in arquivosCarregados)
                {
                    if (offsets.TryGetValue(arquivo, out var offset)) _fileOffsets[arquivo] = offset;
                }
            }
        }

        private static readonly Comparison<ClefEvent> PorTimestampDecrescente =
            (a, b) => Nullable.Compare(b.Timestamp, a.Timestamp);

        /// <summary>
        /// Intercala <paramref name="novos"/> (ordenado) em <paramref name="atuais"/>
        /// (ordenado) comparando apenas a região onde os dois se sobrepõem.
        /// </summary>
        /// <remarks>
        /// Exposto para o teste que confere, evento a evento, que o resultado é idêntico ao
        /// do merge completo. O merge anterior percorria os N eventos com um delegate e
        /// copiava a lista DUAS vezes a cada poll — 19,04 ms e 7,65 MB com 1 milhão de
        /// eventos, uma vez por segundo, segurando o _stateGate e travando a UI junto. Aqui
        /// duas buscas binárias delimitam o trecho afetado e o resto vai em blocos
        /// (<see cref="Array.Copy(Array, int, Array, int, int)"/>), sem nenhuma comparação:
        /// no caso dominante do tail (lote todo mais recente que o topo) não sobra região
        /// nenhuma para intercalar.
        /// </remarks>
        public static ClefEvent[] MesclarPorRegiao(ClefEvent[] atuais, IReadOnlyList<ClefEvent> novos)
        {
            ArgumentNullException.ThrowIfNull(atuais);
            ArgumentNullException.ThrowIfNull(novos);
            if (novos.Count == 0) return atuais;

            var resultado = new ClefEvent[atuais.Length + novos.Count];
            var inicio = PrimeiroMaisAntigoQue(atuais, 0, novos[0]);
            var fim = PrimeiroMaisAntigoQue(atuais, inicio, novos[novos.Count - 1]);

            Array.Copy(atuais, 0, resultado, 0, inicio);

            int i = inicio, j = 0, destino = inicio;
            while (i < fim && j < novos.Count)
            {
                // Empate mantém o evento que já estava na lista à frente, como no merge
                // completo — é o que fixa a ordem entre eventos de mesmo carimbo.
                resultado[destino++] = PorTimestampDecrescente(atuais[i], novos[j]) <= 0
                    ? atuais[i++]
                    : novos[j++];
            }

            while (i < fim) resultado[destino++] = atuais[i++];
            while (j < novos.Count) resultado[destino++] = novos[j++];

            Array.Copy(atuais, fim, resultado, destino, atuais.Length - fim);
            return resultado;
        }

        /// <summary>Primeiro índice cujo evento é estritamente mais antigo que a referência.</summary>
        private static int PrimeiroMaisAntigoQue(ClefEvent[] eventos, int inicio, ClefEvent referencia)
        {
            int menor = inicio, maior = eventos.Length;
            while (menor < maior)
            {
                var meio = menor + ((maior - menor) >> 1);
                if (PorTimestampDecrescente(eventos[meio], referencia) <= 0) menor = meio + 1;
                else maior = meio;
            }

            return menor;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settingsService.Changed -= OnSettingsChanged;

            CancelLoad();
            Interlocked.Exchange(ref _tailEnabled, 0);
            var tail = Interlocked.Exchange(ref _tailCts, null);
            if (tail is not null)
            {
                try { tail.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }
}
