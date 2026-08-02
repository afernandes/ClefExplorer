using System.IO.Compression;
using System.Text;
using ClefExplorer.Services;

namespace ClefExplorer.Tests;

/// <summary>
/// Testes do <see cref="LogStore"/> contra arquivos CLEF reais em pasta temporária:
/// parsing, <c>.clef.gz</c>, padrões ignorados e (des)marcação de arquivos.
/// </summary>
public class LogStoreTests : IDisposable
{
    private readonly string _root;

    public LogStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ClefExplorerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* limpeza best-effort */ }
    }

    /// <summary>Uma linha CLEF. O nível é omitido para Information, como o Serilog faz.</summary>
    private static string ClefLine(string message, string? level = null, string timestamp = "2026-06-15T12:00:00.0000000Z")
    {
        var lvl = level is null ? "" : $@",""@l"":""{level}""";
        return $@"{{""@t"":""{timestamp}"",""@mt"":""{message}""{lvl}}}";
    }

    private string WriteClef(string fileName, params string[] lines)
    {
        var path = Path.Combine(_root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// Grava com BOM UTF-8, como faz o sink configurado com <c>Encoding.UTF8</c>. Os três
    /// bytes precisam sumir antes do parser: colados no '{' eles invalidam a primeira linha.
    /// </summary>
    private string WriteClefComBom(string fileName, params string[] lines)
    {
        var path = Path.Combine(_root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            string.Concat(lines.Select(linha => linha + Environment.NewLine)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private string WriteClefGz(string fileName, params string[] lines)
    {
        var path = Path.Combine(_root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        gz.Write(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines)));
        return path;
    }

    /// <summary>Store com um <see cref="AppStorage"/> isolado, para não tocar no %LOCALAPPDATA% real.</summary>
    private LogStore NewStore(out SettingsService settings)
    {
        var storage = new AppStorage(Path.Combine(_root, "_config"), legacyFolder: null);
        settings = new SettingsService(storage);
        return new LogStore(settings);
    }

    private LogStore NewStore() => NewStore(out _);

    /// <summary>Store com o leitor trocado, para observar por onde a leitura passa.</summary>
    private LogStore NewStore(ILeitorArquivoLog leitor)
    {
        var storage = new AppStorage(Path.Combine(_root, "_config"), legacyFolder: null);
        var settings = new SettingsService(storage);
        return new LogStore(settings, new DescobertaArquivosLog(), leitor, new FiltroArquivosLogIgnorados(settings));
    }

    private sealed class LeitorControlado : ILeitorArquivoLog
    {
        private readonly LeitorArquivoLog _real = new();
        private readonly string _primeiroArquivo;
        private readonly string _segundoArquivo;

        public TaskCompletionSource PrimeiroIniciado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SegundoIniciado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LiberarSegundo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LeitorControlado(string primeiroArquivo, string segundoArquivo)
        {
            _primeiroArquivo = primeiroArquivo;
            _segundoArquivo = segundoArquivo;
        }

        public async Task<ResultadoLeituraArquivoLog> LerAsync(
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(arquivo, _primeiroArquivo, StringComparison.OrdinalIgnoreCase))
            {
                PrimeiroIniciado.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (string.Equals(arquivo, _segundoArquivo, StringComparison.OrdinalIgnoreCase))
            {
                SegundoIniciado.SetResult();
                await LiberarSegundo.Task.WaitAsync(cancellationToken);
            }

            return new ResultadoLeituraArquivoLog(
                new[]
                {
                    new ClefExplorer.Models.ClefEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Level = "Information",
                        Message = Path.GetFileName(arquivo),
                        SourceFile = arquivo,
                    },
                },
                new FileInfo(arquivo).Length,
                0,
                null);
        }

        public ResultadoLeituraArquivoLog LerTrecho(
            ReadOnlySpan<byte> bloco,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            bool inicioDoArquivo,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default) =>
            _real.LerTrecho(bloco, arquivo, textosIgnorados, inicioDoArquivo, pool, cancellationToken);
    }

    /// <summary>
    /// Devolve um evento-carimbo em vez de interpretar o trecho: se ele chegar ao store, o
    /// acompanhamento passou mesmo pelo leitor injetado.
    /// </summary>
    private sealed class LeitorDeTrechoMarcado : ILeitorArquivoLog
    {
        public const string Marcador = "veio do leitor injetado";

        private readonly LeitorArquivoLog _real = new();

        public bool UltimoInicioDoArquivo { get; private set; }

        public Task<ResultadoLeituraArquivoLog> LerAsync(
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default) =>
            _real.LerAsync(arquivo, textosIgnorados, pool, cancellationToken);

        public ResultadoLeituraArquivoLog LerTrecho(
            ReadOnlySpan<byte> bloco,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            bool inicioDoArquivo,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default)
        {
            UltimoInicioDoArquivo = inicioDoArquivo;
            return new ResultadoLeituraArquivoLog(
                new[] { NovoEvento(Marcador, DateTimeOffset.UtcNow) },
                null,
                0,
                null);
        }
    }

    /// <summary>
    /// Faz a CARGA falhar e mantém a leitura de trechos real. O arquivo continua na sessão
    /// sem offset registrado — é assim que o acompanhamento acaba lendo do byte 0.
    /// </summary>
    private sealed class LeitorQueFalhaNaCarga : ILeitorArquivoLog
    {
        private readonly LeitorArquivoLog _real = new();

        public Task<ResultadoLeituraArquivoLog> LerAsync(
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default) =>
            throw new IOException($"falha simulada ao carregar '{arquivo}'");

        public ResultadoLeituraArquivoLog LerTrecho(
            ReadOnlySpan<byte> bloco,
            string arquivo,
            IReadOnlyList<string> textosIgnorados,
            bool inicioDoArquivo,
            PoolDeTextos? pool = null,
            CancellationToken cancellationToken = default) =>
            _real.LerTrecho(bloco, arquivo, textosIgnorados, inicioDoArquivo, pool, cancellationToken);
    }

    // --- Parsing ----------------------------------------------------------------

    [Fact]
    public async Task Loads_events_from_a_clef_file()
    {
        var file = WriteClef("app.clef", ClefLine("primeira"), ClefLine("segunda", "Error"));
        var store = NewStore();

        await store.LoadFromFile(file);

        Assert.Equal(2, store.Count);
        Assert.Contains(store.Snapshot(), e => e.Message == "primeira");
        Assert.Contains(store.Snapshot(), e => e.Level == "Error");
    }

    [Fact]
    public async Task Level_defaults_to_Information_when_omitted()
    {
        var file = WriteClef("app.clef", ClefLine("sem nivel"));
        var store = NewStore();

        await store.LoadFromFile(file);

        Assert.Equal("Information", store.Snapshot()[0].Level);
    }

    [Fact]
    public async Task Records_the_source_file_on_each_event()
    {
        var file = WriteClef("app.clef", ClefLine("x"));
        var store = NewStore();

        await store.LoadFromFile(file);

        Assert.Equal(file, store.Snapshot()[0].SourceFile);
    }

    [Fact]
    public async Task Events_are_sorted_from_newest_to_oldest()
    {
        var file = WriteClef("app.clef",
            ClefLine("antigo", timestamp: "2026-06-01T00:00:00.0000000Z"),
            ClefLine("novo", timestamp: "2026-06-20T00:00:00.0000000Z"));
        var store = NewStore();

        await store.LoadFromFile(file);

        Assert.Equal("novo", store.Snapshot()[0].Message);
    }

    [Fact]
    public async Task Reads_a_gz_file_when_explicitly_requested()
    {
        var file = WriteClefGz("app.clef.gz", ClefLine("comprimido"));
        var store = NewStore();

        await store.LoadFromFile(file);

        Assert.Equal(1, store.Count);
        Assert.Equal("comprimido", store.Snapshot()[0].Message);
    }

    [Fact]
    public async Task An_unreadable_file_does_not_abort_the_others()
    {
        WriteClef("bom.clef", ClefLine("ok"));
        File.WriteAllText(Path.Combine(_root, "ruim.clef"), "isto não é CLEF");
        var store = NewStore();

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(1, store.Count);
    }

    // --- Falhas de leitura reportadas -------------------------------------------

    [Fact]
    public async Task An_unreadable_file_is_recorded_as_a_failure()
    {
        // Antes essas falhas eram engolidas por catch {} e o usuário só via "faltando eventos".
        WriteClef("bom.clef", ClefLine("ok"));
        File.WriteAllText(Path.Combine(_root, "ruim.clef"), "isto não é CLEF");
        var store = NewStore();

        await store.LoadFromFolderAsync(_root);

        var falha = Assert.Single(store.LoadFailures);
        Assert.EndsWith("ruim.clef", falha.Path);
        Assert.False(string.IsNullOrWhiteSpace(falha.Reason));
    }

    [Fact]
    public async Task A_missing_path_is_recorded_as_a_failure()
    {
        var store = NewStore();

        await store.LoadFromPathsAsync(new[] { Path.Combine(_root, "nao-existe") });

        Assert.Single(store.LoadFailures);
    }

    [Fact]
    public async Task A_successful_load_reports_no_failures()
    {
        WriteClef("bom.clef", ClefLine("ok"));
        var store = NewStore();

        await store.LoadFromFolderAsync(_root);

        Assert.Empty(store.LoadFailures);
    }

    [Fact]
    public async Task Failures_from_the_previous_load_are_cleared()
    {
        File.WriteAllText(Path.Combine(_root, "ruim.clef"), "isto não é CLEF");
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.NotEmpty(store.LoadFailures);

        File.Delete(Path.Combine(_root, "ruim.clef"));
        WriteClef("bom.clef", ClefLine("ok"));
        await store.LoadFromFolderAsync(_root);

        Assert.Empty(store.LoadFailures);
    }

    // --- Carregamento de pasta ---------------------------------------------------

    [Fact]
    public async Task Loads_clef_files_recursively_from_a_folder()
    {
        WriteClef("raiz.clef", ClefLine("a"));
        WriteClef(Path.Combine("sub", "aninhado.clef"), ClefLine("b"));
        var store = NewStore();

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task Caminhos_sobrepostos_nao_duplicam_arquivos_nem_eventos()
    {
        var arquivo = WriteClef("unico.clef", ClefLine("uma vez"));
        var store = NewStore();

        await store.LoadFromPathsAsync(new[] { _root, arquivo, _root });

        Assert.Single(store.AvailableFiles);
        Assert.Single(store.LoadedFiles);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task Uma_linha_invalida_nao_descarta_eventos_posteriores_do_arquivo()
    {
        WriteClef("parcial.clef", ClefLine("antes"), "nao e json", ClefLine("depois"));
        var store = NewStore();

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(2, store.Count);
        Assert.Contains(store.Snapshot(), evento => evento.Message == "depois");
        Assert.Single(store.LoadFailures);
    }

    [Fact]
    public async Task Gz_files_in_a_folder_are_listed_but_not_loaded_by_default()
    {
        WriteClef("app.clef", ClefLine("normal"));
        var gz = WriteClefGz("antigo.clef.gz", ClefLine("compactado"));
        var store = NewStore();

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(1, store.Count);                    // só o .clef entrou
        Assert.Contains(gz, store.AvailableFiles);       // mas o .gz aparece na árvore
        Assert.DoesNotContain(gz, store.LoadedFiles);
    }

    [Fact]
    public async Task Invalid_paths_are_skipped()
    {
        var store = NewStore();

        await store.LoadFromPathsAsync(new[] { Path.Combine(_root, "nao-existe") });

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Expands_environment_variables_in_paths()
    {
        var file = WriteClef("app.clef", ClefLine("x"));
        Environment.SetEnvironmentVariable("CLEF_TEST_DIR", _root);
        try
        {
            var store = NewStore();

            await store.LoadFromPathsAsync(new[] { Path.Combine("%CLEF_TEST_DIR%", "app.clef") });

            Assert.Equal(1, store.Count);
            Assert.Equal(file, store.Snapshot()[0].SourceFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLEF_TEST_DIR", null);
        }
    }

    // --- Configurações de exclusão ----------------------------------------------

    [Fact]
    public async Task Ignores_files_matching_a_wildcard_pattern()
    {
        WriteClef("app.clef", ClefLine("mantido"));
        WriteClef("Totvs.Abp.TokenManager.Auth.clef", ClefLine("descartado"));
        var store = NewStore(out var settings);
        settings.Settings.IgnoredFilePatterns.Add("Totvs.Abp.TokenManager.*.clef");

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(1, store.Count);
        Assert.Equal("mantido", store.Snapshot()[0].Message);
    }

    [Fact]
    public async Task Changing_the_ignored_patterns_takes_effect_on_the_next_load()
    {
        // O cache de regex compilada precisa ser invalidado quando as configurações mudam.
        WriteClef("app.clef", ClefLine("mantido"));
        WriteClef("ruidoso.clef", ClefLine("barulho"));
        var store = NewStore(out var settings);

        await store.LoadFromFolderAsync(_root);
        Assert.Equal(2, store.Count);

        settings.Settings.IgnoredFilePatterns.Add("ruidoso.clef");
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(1, store.Count);

        settings.Settings.IgnoredFilePatterns.Clear();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task Ignore_patterns_treat_regex_metacharacters_literally()
    {
        // O padrão é wildcard, não regex: só '*' e '?' são curingas. Um '.' precisa
        // casar com um ponto de verdade, senão "a.clef" excluiria "axclef" também.
        WriteClef("axclef.clef", ClefLine("nao deve ser excluido"));
        var store = NewStore(out var settings);
        settings.Settings.IgnoredFilePatterns.Add("a.clef");

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Ignore_patterns_support_the_question_mark_wildcard()
    {
        WriteClef("log1.clef", ClefLine("um"));
        WriteClef("log22.clef", ClefLine("dois"));
        var store = NewStore(out var settings);
        settings.Settings.IgnoredFilePatterns.Add("log?.clef");  // casa log1, não log22

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(1, store.Count);
        Assert.Equal("dois", store.Snapshot()[0].Message);
    }

    [Fact]
    public async Task An_explicitly_opened_file_is_loaded_even_if_a_pattern_ignores_it()
    {
        var file = WriteClef("ruidoso.clef", ClefLine("pedido explicitamente"));
        var store = NewStore(out var settings);
        settings.Settings.IgnoredFilePatterns.Add("ruidoso.clef");

        await store.LoadFromFile(file);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Ignores_log_lines_containing_configured_text()
    {
        WriteClef("app.clef",
            ClefLine("Notification received"),
            ClefLine("erro de verdade", "Error"));
        var store = NewStore(out var settings);
        settings.Settings.IgnoredLogLines.Add("Notification received");

        await store.LoadFromFolderAsync(_root);

        Assert.Equal(1, store.Count);
        Assert.Equal("erro de verdade", store.Snapshot()[0].Message);
    }

    // --- (Des)marcação de arquivos na árvore ------------------------------------

    [Fact]
    public async Task UpdateLoadedFiles_removes_the_events_of_unchecked_files()
    {
        var a = WriteClef("a.clef", ClefLine("de a"));
        WriteClef("b.clef", ClefLine("de b"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        await store.UpdateLoadedFiles(new[] { a });

        Assert.Equal(1, store.Count);
        Assert.Equal("de a", store.Snapshot()[0].Message);
    }

    [Fact]
    public async Task UpdateLoadedFiles_adds_back_the_events_of_rechecked_files()
    {
        var a = WriteClef("a.clef", ClefLine("de a"));
        var b = WriteClef("b.clef", ClefLine("de b"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        await store.UpdateLoadedFiles(new[] { a });

        await store.UpdateLoadedFiles(new[] { a, b });

        Assert.Equal(2, store.Count);
        Assert.Equal(2, store.LoadedFiles.Count);
    }

    [Fact]
    public async Task UpdateLoadedFiles_compares_paths_ignoring_case()
    {
        // No Windows os caminhos não diferenciam maiúsculas: com o comparador padrão,
        // reenviar a mesma seleção com outra caixa recarregaria o arquivo (duplicando
        // eventos) e não removeria o antigo.
        var a = WriteClef("a.clef", ClefLine("de a"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(1, store.Count);

        await store.UpdateLoadedFiles(new[] { a.ToUpperInvariant() });

        Assert.Equal(1, store.Count);
        Assert.Single(store.LoadedFiles);
    }

    [Fact]
    public async Task LoadedFiles_is_a_copy_and_not_the_live_list()
    {
        WriteClef("a.clef", ClefLine("x"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        var antes = store.LoadedFiles;
        await store.UpdateLoadedFiles(Array.Empty<string>());

        // A cópia obtida antes continua válida mesmo depois de o store esvaziar.
        Assert.Single(antes);
        Assert.Empty(store.LoadedFiles);
    }

    [Fact]
    public async Task UpdateLoadedFiles_can_load_a_gz_file_on_demand()
    {
        WriteClef("app.clef", ClefLine("normal"));
        var gz = WriteClefGz("antigo.clef.gz", ClefLine("compactado"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        await store.UpdateLoadedFiles(store.LoadedFiles.Append(gz).ToList());

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task Snapshot_is_an_isolated_copy()
    {
        WriteClef("a.clef", ClefLine("x"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        var snapshot = store.Snapshot();
        await store.UpdateLoadedFiles(Array.Empty<string>());

        // A cópia continua válida mesmo depois de o store esvaziar.
        Assert.Single(snapshot);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Loading_again_replaces_the_previous_content()
    {
        var a = WriteClef("a.clef", ClefLine("de a"));
        var b = WriteClef("b.clef", ClefLine("de b"));
        var store = NewStore();

        await store.LoadFromFile(a);
        await store.LoadFromFile(b);

        Assert.Equal(1, store.Count);
        Assert.Equal("de b", store.Snapshot()[0].Message);
    }

    // --- Cancelamento -----------------------------------------------------------

    /// <summary>Gera um .clef grande o bastante para o carregamento não terminar instantaneamente.</summary>
    private string WriteBigClef(string fileName, int lines)
    {
        var path = Path.Combine(_root, fileName);
        using var writer = new StreamWriter(path);
        for (var i = 0; i < lines; i++)
        {
            writer.WriteLine(ClefLine($"evento {i}"));
        }
        return path;
    }

    [Fact]
    public void CancelLoad_without_a_load_in_progress_is_harmless()
    {
        var store = NewStore();

        store.CancelLoad(); // não deve lançar

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Cancelling_a_load_preserves_the_previous_content()
    {
        // O estado só é trocado ao final de uma leitura completa, então um
        // carregamento abortado não pode deixar a lista pela metade.
        WriteClef("pequeno.clef", ClefLine("conteúdo anterior"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(1, store.Count);

        File.Delete(Path.Combine(_root, "pequeno.clef"));
        WriteBigClef("grande.clef", 40_000);

        var carregando = store.LoadFromFolderAsync(_root);
        store.CancelLoad();
        await carregando;

        // Ou o conteúdo anterior (cancelou a tempo) ou o novo completo — nunca um meio-termo.
        Assert.True(store.Count == 1 || store.Count == 40_000, $"estado inconsistente: {store.Count}");
        Assert.False(store.IsLoading);
    }

    [Fact]
    public async Task A_new_load_supersedes_the_one_in_progress()
    {
        WriteBigClef("grande.clef", 30_000);
        var store = NewStore();

        var primeiro = store.LoadFromFolderAsync(_root);

        var outra = Path.Combine(_root, "outra");
        Directory.CreateDirectory(outra);
        File.WriteAllLines(Path.Combine(outra, "novo.clef"), new[] { ClefLine("do segundo carregamento") });

        var segundo = store.LoadFromPathsAsync(new[] { outra });
        await Task.WhenAll(primeiro, segundo);

        // Vence o último pedido, não o que por acaso terminar depois.
        Assert.Equal(1, store.Count);
        Assert.Equal("do segundo carregamento", store.Snapshot()[0].Message);
        Assert.False(store.IsLoading);
    }

    [Fact]
    public async Task Operacao_cancelada_nao_desliga_o_loading_da_operacao_mais_recente()
    {
        var primeiroArquivo = WriteClef("primeiro.clef", ClefLine("primeiro"));
        var segundoArquivo = WriteClef("segundo.clef", ClefLine("segundo"));
        var storage = new AppStorage(Path.Combine(_root, "_config-controlado"), legacyFolder: null);
        var leitor = new LeitorControlado(primeiroArquivo, segundoArquivo);
        var settings = new SettingsService(storage);
        using var store = new LogStore(
            settings,
            new DescobertaArquivosLog(),
            leitor,
            new FiltroArquivosLogIgnorados(settings));

        var primeira = store.LoadFromFile(primeiroArquivo);
        await leitor.PrimeiroIniciado.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var segunda = store.LoadFromFile(segundoArquivo);
        await leitor.SegundoIniciado.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await primeira;

        Assert.True(store.IsLoading);
        Assert.Contains("Lendo", store.LoadProgressText);

        leitor.LiberarSegundo.SetResult();
        await segunda;

        Assert.False(store.IsLoading);
        Assert.Null(store.LoadProgressText);
        Assert.Equal("segundo.clef", Assert.Single(store.Snapshot()).Message);
    }

    // --- Modo tail (acompanhar arquivos ao vivo) --------------------------------

    /// <summary>Espera até a condição valer ou estourar o tempo — o tail sonda a cada 1s.</summary>
    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 6000)
    {
        var limite = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < limite)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [Fact]
    public void Tail_is_off_by_default()
    {
        Assert.False(NewStore().TailEnabled);
    }

    [Fact]
    public async Task Tail_picks_up_lines_appended_after_the_load()
    {
        var file = WriteClef("app.clef", ClefLine("inicial"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(1, store.Count);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("apareceu depois") + Environment.NewLine);

            Assert.True(await WaitUntil(() => store.Count == 2), "o evento novo não foi captado");
            Assert.Contains(store.Snapshot(), e => e.Message == "apareceu depois");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_does_not_duplicate_lines_written_during_the_initial_load()
    {
        // Regressão: o offset era registrado ANTES de ler (com o comprimento de abertura).
        // Se o arquivo crescesse durante a carga, o StreamReader consumia além daquele
        // ponto e o tail relia o excedente, duplicando eventos.
        var file = WriteClef("app.clef", ClefLine("a"), ClefLine("b"));
        var store = NewStore();

        // Simula o crescimento acrescentando antes de ligar o tail: a carga inicial já leu
        // tudo até o fim, então o tail não pode trazer nada de novo.
        File.AppendAllText(file, ClefLine("c") + Environment.NewLine);
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(3, store.Count);

        store.SetTailEnabled(true);
        try
        {
            await Task.Delay(2500); // alguns tiques do tail

            Assert.Equal(3, store.Count);
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_does_not_reread_events_already_loaded()
    {
        var file = WriteClef("app.clef", ClefLine("a"), ClefLine("b"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("c") + Environment.NewLine);
            await WaitUntil(() => store.Count == 3);
            await Task.Delay(1500); // deixa passar mais alguns tiques

            Assert.Equal(3, store.Count); // e não 5, 7…
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_ignores_a_line_that_is_still_being_written()
    {
        // Uma linha sem "\n" ainda está sendo escrita: como JSON incompleto,
        // envenenaria o parser se fosse consumida.
        var file = WriteClef("app.clef", ClefLine("completa"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, @"{""@t"":""2026-06-15T12:00:00.00000");
            await Task.Delay(2000);

            Assert.Equal(1, store.Count);

            // Ao completar a linha, o evento entra.
            File.AppendAllText(file, @"00Z"",""@mt"":""agora sim""}" + Environment.NewLine);
            Assert.True(await WaitUntil(() => store.Count == 2), "a linha completada não foi captada");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_ignora_linha_invalida_e_continua_com_a_proxima()
    {
        var file = WriteClef("app.clef", ClefLine("inicial"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllLines(file, new[] { "nao e CLEF", ClefLine("valido depois") });

            Assert.True(await WaitUntil(() => store.Count == 2), "o tail ficou preso na linha inválida");
            Assert.Contains(store.Snapshot(), evento => evento.Message == "valido depois");
        }
        finally
        {
            store.SetTailEnabled(false);
            store.Dispose();
        }
    }

    [Fact]
    public async Task Tail_descarta_linha_acima_do_limite_e_avanca_para_a_proxima()
    {
        var file = WriteClef("app.clef", ClefLine("inicial"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            var linhaExcessiva = new string('x', 16 * 1024 * 1024 + 128);
            await File.AppendAllTextAsync(
                file,
                linhaExcessiva + Environment.NewLine + ClefLine("depois da linha grande") + Environment.NewLine);

            Assert.True(
                await WaitUntil(() => store.Snapshot().Any(e => e.Message == "depois da linha grande"), 10_000),
                "o tail não avançou após descartar a linha excessiva");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_keeps_the_list_sorted_when_new_events_are_older()
    {
        // O tail intercala os novos eventos em vez de reordenar a lista inteira; um evento
        // com carimbo antigo (relógio fora de sincronia, arquivo de outra origem) precisa
        // cair na posição certa.
        var file = WriteClef("app.clef",
            ClefLine("mais novo", timestamp: "2026-06-20T00:00:00.0000000Z"),
            ClefLine("meio", timestamp: "2026-06-10T00:00:00.0000000Z"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("antigo", timestamp: "2026-06-01T00:00:00.0000000Z") + Environment.NewLine);
            File.AppendAllText(file, ClefLine("novissimo", timestamp: "2026-06-30T00:00:00.0000000Z") + Environment.NewLine);

            Assert.True(await WaitUntil(() => store.Count == 4), "os eventos novos não foram captados");

            Assert.Equal(
                new[] { "novissimo", "mais novo", "meio", "antigo" },
                store.Snapshot().Select(e => e.Message));
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_restarts_from_the_beginning_when_the_file_is_truncated()
    {
        // Rotação de log: o arquivo encolhe e o offset antigo deixa de valer.
        var file = WriteClef("app.clef", ClefLine("velho1"), ClefLine("velho2"), ClefLine("velho3"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(3, store.Count);

        store.SetTailEnabled(true);
        try
        {
            File.WriteAllText(file, ClefLine("depois da rotação") + Environment.NewLine);

            Assert.True(await WaitUntil(() => store.Snapshot().Any(e => e.Message == "depois da rotação")),
                "o conteúdo pós-rotação não foi captado");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_descarta_o_BOM_ao_reler_do_inicio_o_arquivo_truncado()
    {
        // Truncar zera o offset e o tail volta ao byte 0 — onde mora o BOM. Só a CARGA
        // descartava esses três bytes; no acompanhamento o U+FEFF ficava colado no '{' e a
        // primeira linha depois da rotação virava linha inválida.
        var file = WriteClefComBom("app.clef", ClefLine("velho1"), ClefLine("velho2"), ClefLine("velho3"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(3, store.Count);

        store.SetTailEnabled(true);
        try
        {
            WriteClefComBom("app.clef", ClefLine("depois da rotação"));

            Assert.True(
                await WaitUntil(() => store.Snapshot().Any(e => e.Message == "depois da rotação")),
                "a primeira linha depois do BOM foi descartada");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_le_sem_o_BOM_o_arquivo_que_falhou_na_carga_e_cresce_depois()
    {
        // O outro jeito de o tail começar do byte 0: o arquivo falhou na carga, continua na
        // sessão e ficou sem offset registrado. Quando ele volta a crescer, o acompanhamento
        // relê tudo desde o começo — inclusive o BOM.
        var file = WriteClefComBom("app.clef", ClefLine("primeira, logo depois do BOM"));
        using var store = NewStore(new LeitorQueFalhaNaCarga());
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(0, store.Count);
        Assert.Contains(file, store.LoadedFiles);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("cresceu durante o acompanhamento") + Environment.NewLine);

            Assert.True(
                await WaitUntil(() => store.Count == 2),
                "o tail não recuperou o arquivo que falhou na carga");
            Assert.Contains(store.Snapshot(), e => e.Message == "primeira, logo depois do BOM");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_interpreta_o_trecho_pelo_leitor_injetado()
    {
        // A carga passava pela interface e o acompanhamento chamava o parser ESTÁTICO: um
        // leitor injetado cobria só metade do caminho, e a interface prometia mais do que
        // entregava.
        var file = WriteClef("app.clef", ClefLine("inicial"));
        var leitor = new LeitorDeTrechoMarcado();
        using var store = NewStore(leitor);
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(1, store.Count);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("apareceu depois") + Environment.NewLine);

            Assert.True(
                await WaitUntil(() => store.Snapshot().Any(e => e.Message == LeitorDeTrechoMarcado.Marcador)),
                "o acompanhamento não passou pelo leitor injetado");

            // O trecho começa depois do que a carga já leu: não é início de arquivo, e é esse
            // sinalizador que impede o descarte de três bytes no meio de uma linha válida.
            Assert.False(leitor.UltimoInicioDoArquivo);
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Disabling_tail_stops_picking_up_new_lines()
    {
        var file = WriteClef("app.clef", ClefLine("inicial"));
        var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        store.SetTailEnabled(true);
        store.SetTailEnabled(false);

        File.AppendAllText(file, ClefLine("nao deve aparecer") + Environment.NewLine);
        await Task.Delay(2000);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Tail_respects_the_ignored_log_lines_setting()
    {
        var file = WriteClef("app.clef", ClefLine("inicial"));
        var store = NewStore(out var settings);
        settings.Settings.IgnoredLogLines.Add("ruído");
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("ruído a ignorar") + Environment.NewLine);
            File.AppendAllText(file, ClefLine("evento relevante") + Environment.NewLine);

            Assert.True(await WaitUntil(() => store.Count == 2), "o evento relevante não foi captado");
            Assert.DoesNotContain(store.Snapshot(), e => e.Message == "ruído a ignorar");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Changed_is_raised_around_a_load()
    {
        WriteClef("a.clef", ClefLine("x"));
        var store = NewStore();
        var count = 0;
        store.Changed += () => count++;

        await store.LoadFromFolderAsync(_root);

        Assert.True(count >= 2, $"esperado ao menos 2 notificações (início e fim), veio {count}");
    }

    // --- Tail: arquivos que aparecem depois da carga -----------------------------

    [Fact]
    public async Task Tail_adopts_a_file_created_after_the_load()
    {
        // A rotação diária de C:\TOTVSPDV\Logs troca o NOME do arquivo à meia-noite
        // (…log<AAAAMMDD>.clef). Como o tail varria apenas a lista congelada na carga, o
        // modo ao vivo morria em silêncio na virada do dia: os arquivos vigiados paravam de
        // crescer e o do dia seguinte nunca era descoberto.
        WriteClef("dia-anterior.clef", ClefLine("de ontem"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        var novo = WriteClef("dia-seguinte.clef", ClefLine("escrito antes da adoção"));

        store.SetTailEnabled(true);
        try
        {
            Assert.True(
                await WaitUntil(() => store.LoadedFiles.Contains(novo, StringComparer.OrdinalIgnoreCase)),
                "o arquivo que apareceu depois da carga não foi adotado");
            Assert.Contains(novo, store.AvailableFiles);

            File.AppendAllText(novo, ClefLine("escrito depois da adoção") + Environment.NewLine);

            Assert.True(
                await WaitUntil(() => store.Snapshot().Any(e => e.Message == "escrito depois da adoção")),
                "o arquivo adotado não passou a ser acompanhado");

            // Offset no fim: o arquivo entra a partir do que for escrito depois da adoção,
            // para não despejar de uma vez um arquivo inteiro copiado para a pasta.
            Assert.DoesNotContain(store.Snapshot(), e => e.Message == "escrito antes da adoção");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_does_not_readopt_a_file_the_user_unchecked()
    {
        // A adoção compara com os arquivos CONHECIDOS, não com os carregados: comparar com
        // os carregados faria a redescoberta ressuscitar, a cada 30 s, tudo o que o usuário
        // tivesse desmarcado na árvore.
        var a = WriteClef("a.clef", ClefLine("de a"));
        var b = WriteClef("b.clef", ClefLine("de b"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        await store.UpdateLoadedFiles(new[] { a });

        store.SetTailEnabled(true);
        try
        {
            await Task.Delay(2500); // passa pela redescoberta do primeiro tique
            File.AppendAllText(b, ClefLine("nao deve voltar") + Environment.NewLine);
            await Task.Delay(2000);

            Assert.DoesNotContain(b, store.LoadedFiles);
            Assert.DoesNotContain(store.Snapshot(), e => e.Message == "nao deve voltar");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_lists_a_new_gz_file_without_following_it()
    {
        // O tail lê byte a byte a partir de um offset, então um .gz que apareça na pasta
        // pode entrar na árvore, mas nunca no acompanhamento.
        WriteClef("app.clef", ClefLine("inicial"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        var gz = WriteClefGz("antigo.clef.gz", ClefLine("compactado"));

        store.SetTailEnabled(true);
        try
        {
            Assert.True(
                await WaitUntil(() => store.AvailableFiles.Contains(gz, StringComparer.OrdinalIgnoreCase)),
                "o .gz novo não apareceu na árvore");

            Assert.DoesNotContain(gz, store.LoadedFiles);
            Assert.Equal(1, store.Count);
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_does_not_follow_a_new_file_matching_an_ignored_pattern()
    {
        // O filtro de arquivos ignorados vale igual para quem chega durante o
        // acompanhamento — senão o arquivo barulhento voltaria pela porta dos fundos.
        WriteClef("app.clef", ClefLine("inicial"));
        using var store = NewStore(out var settings);
        settings.Settings.IgnoredFilePatterns.Add("ruidoso.clef");
        await store.LoadFromFolderAsync(_root);

        var ruidoso = WriteClef("ruidoso.clef", ClefLine("barulho"));

        store.SetTailEnabled(true);
        try
        {
            Assert.True(
                await WaitUntil(() => store.AvailableFiles.Contains(ruidoso, StringComparer.OrdinalIgnoreCase)),
                "o arquivo novo não apareceu na árvore");

            File.AppendAllText(ruidoso, ClefLine("mais barulho") + Environment.NewLine);
            await Task.Delay(2000);

            Assert.DoesNotContain(ruidoso, store.LoadedFiles);
            Assert.Equal(1, store.Count);
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_follows_files_in_more_than_one_folder()
    {
        // O poll descobre os tamanhos com UMA enumeração por pasta acompanhada, em vez de
        // abrir os arquivos um a um só para ler Length (2 ms local e 2.494 ms em SMB nos
        // 104 arquivos reais). Uma sessão com subpastas precisa enxergar todas elas.
        var raiz = WriteClef("raiz.clef", ClefLine("da raiz"));
        var aninhado = WriteClef(Path.Combine("sub", "aninhado.clef"), ClefLine("da subpasta"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(2, store.Count);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(raiz, ClefLine("nova da raiz") + Environment.NewLine);
            File.AppendAllText(aninhado, ClefLine("nova da subpasta") + Environment.NewLine);

            Assert.True(await WaitUntil(() => store.Count == 4), "o tail não captou as duas pastas");
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_reuses_the_texts_from_the_pool_of_the_load()
    {
        // Os eventos ingeridos ao vivo não passavam pelo pool da sessão: cada linha nova
        // recriava o template e as chaves que já estavam na memória, jogando fora os 23%
        // de economia justamente na sessão que fica horas aberta.
        var file = WriteClef("app.clef", ClefLine("mensagem repetida"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);
        Assert.Equal(1, store.Count);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("mensagem repetida") + Environment.NewLine);
            Assert.True(await WaitUntil(() => store.Count == 2), "o evento novo não foi captado");

            var eventos = store.Snapshot();
            Assert.Same(eventos[0].MessageTemplate, eventos[1].MessageTemplate);
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    [Fact]
    public async Task Tail_publishes_an_array_with_no_spare_slots()
    {
        // A List de eventos dobrava a Capacity no primeiro poll do tail (com 1 milhão de
        // eventos ficavam 8 MB de referências vazias retidos), desfazendo a disciplina de
        // memória que a carga já aplicava.
        var file = WriteClef("app.clef", ClefLine("a"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        store.SetTailEnabled(true);
        try
        {
            File.AppendAllText(file, ClefLine("b") + Environment.NewLine);
            Assert.True(await WaitUntil(() => store.Count == 2), "o evento novo não foi captado");

            Assert.Equal(2, store.Snapshot().Length);
        }
        finally
        {
            store.SetTailEnabled(false);
        }
    }

    // --- Estado publicado sem cópia ----------------------------------------------

    [Fact]
    public async Task Snapshot_returns_the_same_instance_while_the_events_do_not_change()
    {
        // Snapshot() copiava a lista inteira segurando o _stateGate: 8 MB e 1,4 ms por
        // consulta com 1 milhão de eventos, com a thread da UI esperando o merge do tail.
        WriteClef("a.clef", ClefLine("x"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        Assert.Same(store.Snapshot(), store.Snapshot());
    }

    [Fact]
    public async Task LoadedFiles_returns_the_same_instance_while_nothing_changes()
    {
        // Cada acesso devolvia um array NOVO, e o diff do Blazor redesenhava a árvore de
        // arquivos inteira a cada render mesmo sem nada ter mudado.
        WriteClef("a.clef", ClefLine("x"));
        using var store = NewStore();
        await store.LoadFromFolderAsync(_root);

        Assert.Same(store.LoadedFiles, store.LoadedFiles);
        Assert.Same(store.AvailableFiles, store.AvailableFiles);
        Assert.Same(store.LoadFailures, store.LoadFailures);

        var antes = store.LoadedFiles;
        await store.UpdateLoadedFiles(Array.Empty<string>());

        Assert.NotSame(antes, store.LoadedFiles);
    }

    // --- Merge do tail -----------------------------------------------------------

    [Fact]
    public void MesclarPorRegiao_produces_the_same_order_as_the_full_merge()
    {
        // O merge do tail passou a comparar só a região onde os dois conjuntos se
        // sobrepõem, em vez de percorrer o conjunto inteiro (19,04 ms e 7,65 MB por poll
        // com 1 milhão de eventos, segurando o _stateGate). Como a ordem que o usuário vê
        // depende disso — inclusive o desempate entre eventos de mesmo carimbo —, o
        // resultado é conferido evento a evento contra o merge completo que existia antes.
        var aleatorio = new Random(20260731);

        for (var caso = 0; caso < 300; caso++)
        {
            var atuais = EventosOrdenados(aleatorio, aleatorio.Next(0, 40), $"atual{caso}");
            var novos = EventosOrdenados(aleatorio, aleatorio.Next(0, 8), $"novo{caso}");

            var esperado = MergeCompletoDeReferencia(atuais, novos);
            var obtido = LogStore.MesclarPorRegiao(atuais, novos);

            Assert.Equal(
                esperado.Select(evento => evento.Message),
                obtido.Select(evento => evento.Message));
        }
    }

    [Fact]
    public void MesclarPorRegiao_puts_a_batch_newer_than_everything_at_the_front()
    {
        // Caso dominante do tail: o lote inteiro é mais recente que o topo da lista, e aí
        // não sobra nenhuma região para intercalar.
        var atuais = EventosOrdenados(new Random(1), 5, "atual");
        var novos = new[]
        {
            NovoEvento("recente", DateTimeOffset.Parse("2030-01-01T00:00:00Z")),
        };

        var resultado = LogStore.MesclarPorRegiao(atuais, novos);

        Assert.Equal(6, resultado.Length);
        Assert.Equal("recente", resultado[0].Message);
    }

    private static ClefExplorer.Models.ClefEvent NovoEvento(string mensagem, DateTimeOffset? carimbo) =>
        new()
        {
            Timestamp = carimbo,
            Level = "Information",
            Message = mensagem,
            SourceFile = "memoria",
        };

    /// <summary>
    /// Eventos ordenados do mais novo para o mais antigo, com carimbos sorteados numa faixa
    /// curta (para haver empates de verdade) e alguns sem carimbo.
    /// </summary>
    private static ClefExplorer.Models.ClefEvent[] EventosOrdenados(Random aleatorio, int quantidade, string prefixo)
    {
        var origem = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var eventos = new List<ClefExplorer.Models.ClefEvent>(quantidade);
        for (var i = 0; i < quantidade; i++)
        {
            var sorteio = aleatorio.Next(0, 12);
            eventos.Add(NovoEvento(
                $"{prefixo}-{i}",
                sorteio == 0 ? null : origem.AddSeconds(aleatorio.Next(0, 5))));
        }

        // OrderByDescending é estável: mantém a ordem de inserção entre empates, que é
        // exatamente o invariante que o merge precisa receber.
        return eventos.OrderByDescending(evento => evento.Timestamp).ToArray();
    }

    /// <summary>Merge completo original, mantido só como referência para o teste.</summary>
    private static ClefExplorer.Models.ClefEvent[] MergeCompletoDeReferencia(
        ClefExplorer.Models.ClefEvent[] destino,
        IReadOnlyList<ClefExplorer.Models.ClefEvent> novos)
    {
        var resultado = new List<ClefExplorer.Models.ClefEvent>(destino.Length + novos.Count);
        int i = 0, j = 0;

        while (i < destino.Length && j < novos.Count)
        {
            resultado.Add(Nullable.Compare(novos[j].Timestamp, destino[i].Timestamp) <= 0
                ? destino[i++]
                : novos[j++]);
        }

        while (i < destino.Length) resultado.Add(destino[i++]);
        while (j < novos.Count) resultado.Add(novos[j++]);
        return resultado.ToArray();
    }
}
