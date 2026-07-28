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
}
