using ClefExplorer.Helpers;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Tests;

/// <summary>
/// Agregações do painel de estatísticas — o que transforma o app de leitor em analisador.
/// </summary>
public class LogStatisticsTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);

    private static ClefEvent Event(
        string level = "Information",
        string? message = "mensagem",
        string? template = null,
        string? exception = null,
        string? sourceContext = null,
        string? sourceFile = null,
        int minutosDepois = 0)
    {
        var ev = new ClefEvent
        {
            Level = level,
            Message = message,
            MessageTemplate = template,
            Exception = exception,
            SourceFile = sourceFile,
            Timestamp = Base.AddMinutes(minutosDepois),
        };

        if (sourceContext is not null)
        {
            ev.Properties = new Dictionary<string, LogEventPropertyValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["SourceContext"] = new ScalarValue(sourceContext),
            };
        }

        return ev;
    }

    // --- Contagem por nível --------------------------------------------------------

    [Fact]
    public void Counts_events_by_level()
    {
        var stats = LogStatistics.Compute(new[]
        {
            Event("Error"), Event("Error"), Event("Warning"), Event("Information"),
        });

        Assert.Equal(4, stats.Total);
        Assert.Equal(2, stats.ByLevel.Single(e => e.Key == "Error").Count);
        Assert.Equal(1, stats.ByLevel.Single(e => e.Key == "Warning").Count);
    }

    [Fact]
    public void Levels_come_in_severity_order_not_by_count()
    {
        // Ordem estável: o olho procura "Error" sempre no mesmo lugar, mesmo quando
        // Information é muito mais numeroso.
        var eventos = Enumerable.Repeat(Event("Information"), 50)
            .Append(Event("Error"))
            .Append(Event("Warning"))
            .ToArray();

        var stats = LogStatistics.Compute(eventos);

        Assert.Equal(new[] { "Error", "Warning", "Information" }, stats.ByLevel.Select(e => e.Key));
    }

    [Fact]
    public void An_unknown_level_still_appears_at_the_end()
    {
        var stats = LogStatistics.Compute(new[] { Event("Information"), Event("Auditoria") });

        Assert.Equal("Auditoria", stats.ByLevel.Last().Key);
    }

    [Fact]
    public void ErrorCount_covers_both_Error_and_Fatal()
    {
        var stats = LogStatistics.Compute(new[] { Event("Error"), Event("Fatal"), Event("Warning") });

        Assert.Equal(2, stats.ErrorCount);
    }

    // --- Mensagens -----------------------------------------------------------------

    [Fact]
    public void Messages_are_grouped_by_template_not_by_rendered_text()
    {
        // O ponto central do ranking: 3 renderizações do mesmo template são a MESMA
        // ocorrência. Agrupar pelo texto renderizado daria três linhas de contagem 1 e
        // esconderia justamente o que mais se repete.
        var eventos = new[]
        {
            Event(message: "Pedido 1 processado", template: "Pedido {Id} processado"),
            Event(message: "Pedido 2 processado", template: "Pedido {Id} processado"),
            Event(message: "Pedido 3 processado", template: "Pedido {Id} processado"),
        };

        var stats = LogStatistics.Compute(eventos);

        var top = Assert.Single(stats.TopMessages);
        Assert.Equal("Pedido {Id} processado", top.Key);
        Assert.Equal(3, top.Count);
    }

    [Fact]
    public void Without_a_template_the_rendered_message_is_used()
    {
        var stats = LogStatistics.Compute(new[] { Event(message: "sem template"), Event(message: "sem template") });

        Assert.Equal(2, stats.TopMessages.Single().Count);
    }

    [Fact]
    public void Rankings_are_capped()
    {
        var eventos = Enumerable.Range(0, 30).Select(i => Event(message: $"msg {i}")).ToArray();

        var stats = LogStatistics.Compute(eventos, topCount: 5);

        Assert.Equal(5, stats.TopMessages.Count);
    }

    // --- Exceções ------------------------------------------------------------------

    [Fact]
    public void Exceptions_are_grouped_by_their_first_line()
    {
        // O stack trace inteiro é único por ocorrência e não agruparia nada; a primeira
        // linha traz o tipo e a mensagem, que é o que se repete.
        var eventos = new[]
        {
            Event("Error", exception: "System.TimeoutException: tempo esgotado\n   at Foo.Bar()"),
            Event("Error", exception: "System.TimeoutException: tempo esgotado\n   at Outro.Metodo()"),
        };

        var stats = LogStatistics.Compute(eventos);

        var top = Assert.Single(stats.TopExceptions);
        Assert.Equal("System.TimeoutException: tempo esgotado", top.Key);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Leading_blank_lines_do_not_become_the_exception_key()
    {
        // O recorte por Span substituiu o Split('\n', RemoveEmptyEntries), que descartava
        // as quebras iniciais: sem reproduzir isso, a chave viraria "" e todas as exceções
        // com stack trace precedido de quebra de linha cairiam num grupo só.
        var eventos = new[]
        {
            Event("Error", exception: "\n\nSystem.IO.IOException: disco cheio\n   at Gravar()"),
            Event("Error", exception: "System.IO.IOException: disco cheio\n   at Outro()"),
        };

        var stats = LogStatistics.Compute(eventos);

        var top = Assert.Single(stats.TopExceptions);
        Assert.Equal("System.IO.IOException: disco cheio", top.Key);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void A_whitespace_only_first_line_is_not_ranked()
    {
        // Entradas só com espaço NÃO eram removidas pelo Split; a primeira linha continua
        // sendo aquela, e um grupo de chave vazia não diz nada.
        var stats = LogStatistics.Compute(new[] { Event("Error", exception: "   \nSystem.Exception: x") });

        Assert.Empty(stats.TopExceptions);
    }

    [Fact]
    public void Events_without_an_exception_are_left_out_of_that_ranking()
    {
        var stats = LogStatistics.Compute(new[] { Event(), Event("Error", exception: "Boom: x") });

        Assert.Single(stats.TopExceptions);
    }

    // --- Origens -------------------------------------------------------------------

    [Fact]
    public void Source_prefers_the_SourceContext_property()
    {
        var stats = LogStatistics.Compute(new[]
        {
            Event(sourceContext: "Api.Pedido", sourceFile: @"C:\logs\a.clef"),
            Event(sourceContext: "Api.Pedido", sourceFile: @"C:\logs\b.clef"),
        });

        var top = Assert.Single(stats.TopSources);
        Assert.Equal("Api.Pedido", top.Key);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Source_falls_back_to_the_file_name()
    {
        var stats = LogStatistics.Compute(new[] { Event(sourceFile: @"C:\logs\pedidos.clef") });

        var top = Assert.Single(stats.TopSources);
        Assert.Equal(@"C:\logs\pedidos.clef", top.Key);   // a chave filtra pelo caminho
        Assert.Equal("pedidos.clef", top.Label);           // o rótulo mostra só o nome
    }

    // --- Timeline ------------------------------------------------------------------

    [Fact]
    public void The_timeline_has_the_requested_number_of_buckets()
    {
        var eventos = Enumerable.Range(0, 100).Select(i => Event(minutosDepois: i)).ToArray();

        var stats = LogStatistics.Compute(eventos, buckets: 10);

        Assert.Equal(10, stats.Timeline.Count);
        Assert.Equal(100, stats.Timeline.Sum(b => b.Total));
    }

    [Fact]
    public void The_timeline_separates_errors_from_the_total()
    {
        // É o que revela os picos: a série de erros sobreposta ao volume.
        var eventos = new[]
        {
            Event("Information", minutosDepois: 0),
            Event("Error", minutosDepois: 0),
            Event("Fatal", minutosDepois: 0),
        };

        var stats = LogStatistics.Compute(eventos, buckets: 1);

        Assert.Equal(3, stats.Timeline[0].Total);
        Assert.Equal(2, stats.Timeline[0].Errors);
    }

    [Fact]
    public void The_newest_event_lands_in_the_last_bucket()
    {
        // Sem o clamp, o evento do limite superior cairia num índice fora do vetor.
        var eventos = new[] { Event(minutosDepois: 0), Event(minutosDepois: 10) };

        var stats = LogStatistics.Compute(eventos, buckets: 4);

        Assert.Equal(2, stats.Timeline.Sum(b => b.Total));
        Assert.Equal(1, stats.Timeline.Last().Total);
    }

    [Fact]
    public void Events_all_at_the_same_instant_yield_a_single_bucket()
    {
        var eventos = new[] { Event(minutosDepois: 0), Event(minutosDepois: 0) };

        var stats = LogStatistics.Compute(eventos);

        Assert.Single(stats.Timeline);
        Assert.Equal(2, stats.Timeline[0].Total);
    }

    [Fact]
    public void The_bucket_size_shrinks_when_the_period_is_shorter()
    {
        // Comportamento observado ao filtrar: o mesmo painel re-fatia para o novo período.
        var largo = LogStatistics.Compute(
            Enumerable.Range(0, 50).Select(i => Event(minutosDepois: i * 60)).ToArray(), buckets: 10);
        var estreito = LogStatistics.Compute(
            Enumerable.Range(0, 50).Select(i => Event(minutosDepois: i)).ToArray(), buckets: 10);

        Assert.True(estreito.BucketSize < largo.BucketSize);
    }

    [Fact]
    public void Events_without_a_timestamp_do_not_break_the_timeline()
    {
        var eventos = new[] { new ClefEvent { Level = "Information" }, Event(minutosDepois: 5) };

        var stats = LogStatistics.Compute(eventos);

        Assert.Equal(2, stats.Total);
        Assert.NotEmpty(stats.Timeline);
    }

    // --- Vazio ---------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_bucket_count_fails_explicitly(int buckets)
    {
        // Sem a validação isto viraria DivideByZeroException lá dentro (ou um índice
        // negativo), bem longe da causa.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogStatistics.Compute(new[] { Event() }, buckets: buckets));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_top_count_fails_explicitly(int topCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogStatistics.Compute(new[] { Event() }, topCount: topCount));
    }

    [Fact]
    public void An_empty_set_yields_empty_stats_without_throwing()
    {
        var stats = LogStatistics.Compute(Array.Empty<ClefEvent>());

        Assert.Equal(0, stats.Total);
        Assert.Empty(stats.ByLevel);
        Assert.Empty(stats.Timeline);
        Assert.Equal(0, stats.ErrorCount);
    }

    // --- Equivalência com o algoritmo anterior --------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(37)]
    [InlineData(500)]
    public void The_single_pass_result_matches_the_previous_multi_pass_algorithm(int quantidade)
    {
        // O Compute foi reescrito em passagem única porque as sete passadas LINQ (mais o
        // Where().ToList() da timeline, uma cópia do conjunto inteiro) custavam 543,9 ms e
        // 135 MB por filtragem com 1 milhão de eventos, na thread da UI. Reescrita de
        // agregação erra em silêncio: um empate desfeito em outra ordem, um rótulo tirado
        // de outra ocorrência, uma fatia deslocada. Este teste compara campo a campo com o
        // algoritmo antigo, reproduzido abaixo, sobre um conjunto com todos os casos que
        // aparecem em log real (níveis desconhecidos, empates, eventos sem hora, exceções).
        var eventos = CorpusVariado(quantidade);

        var atual = LogStatistics.Compute(eventos, topCount: 5, buckets: 7);
        var anterior = AlgoritmoAnterior.Compute(eventos, topCount: 5, buckets: 7);

        Assert.Equal(anterior.Total, atual.Total);
        Assert.Equal(anterior.BucketSize, atual.BucketSize);
        Assert.Equal(anterior.ByLevel, atual.ByLevel);
        Assert.Equal(anterior.TopMessages, atual.TopMessages);
        Assert.Equal(anterior.TopExceptions, atual.TopExceptions);
        Assert.Equal(anterior.TopSources, atual.TopSources);
        Assert.Equal(anterior.Timeline, atual.Timeline);
    }

    /// <summary>
    /// Conjunto propositalmente irregular: níveis fora da lista conhecida, contagens
    /// empatadas (onde uma ordenação instável se denunciaria), eventos sem hora, origem
    /// vinda ora do SourceContext ora do arquivo, e a mesma chave com caixa diferente.
    /// </summary>
    private static ClefEvent[] CorpusVariado(int quantidade)
    {
        var niveis = new[] { "Error", "Information", "Warning", "Auditoria", "Fatal", "information" };
        var eventos = new List<ClefEvent>(quantidade);

        for (var i = 0; i < quantidade; i++)
        {
            var evento = Event(
                level: niveis[i % niveis.Length],
                message: $"Pedido {i} processado",
                template: i % 3 == 0 ? null : $"Pedido {{Id}} processado {i % 4}",
                exception: ExcecaoDoIndice(i),
                sourceContext: i % 7 == 0 ? null : $"Api.Servico{i % 6}",
                sourceFile: $@"C:\logs\app{i % 3}.clef",
                minutosDepois: i % 11);

            // Eventos sem hora existem em log real (linha sem @t) e a timeline precisa
            // ignorá-los sem deslocar as fatias dos demais.
            if (i % 13 == 0) evento.Timestamp = null;

            eventos.Add(evento);
        }

        return eventos.ToArray();
    }

    /// <summary>
    /// Bordas do recorte da primeira linha: o Split('\n') com RemoveEmptyEntries que havia
    /// antes descartava entradas VAZIAS mas não as só com espaço, então o texto começando
    /// por quebra de linha e o texto só com espaços caem em ramos diferentes.
    /// </summary>
    private static string? ExcecaoDoIndice(int i) => (i % 17) switch
    {
        0 => "System.TimeoutException: tempo esgotado\n   at Foo.Bar()",
        3 => "\n\nSystem.IO.IOException: disco cheio\n   at Gravar()",
        6 => "   \nSystem.Exception: depois de uma linha em branco",
        9 => "   ",
        12 => "System.Exception: linha unica sem quebra",
        _ => null,
    };

    /// <summary>
    /// Cópia literal do LogStatistics anterior à reescrita, mantida só como oráculo do
    /// teste acima. Não deve ganhar correções: se o comportamento mudar de propósito, o
    /// teste tem de falhar e ser reavaliado.
    /// </summary>
    private static class AlgoritmoAnterior
    {
        private static readonly string[] LevelOrder = ClefExplorer.Services.LogFilter.AllLevels;

        public static LogStats Compute(IReadOnlyList<ClefEvent> events, int topCount, int buckets)
        {
            if (events.Count == 0) return new LogStats();

            var (timeline, bucketSize) = MontarTimeline(events, buckets);

            return new LogStats
            {
                Total = events.Count,
                ByLevel = ContarPorNivel(events),
                TopMessages = TopPor(events, MensagemAgrupavel, topCount),
                TopExceptions = TopPor(events.Where(e => !string.IsNullOrEmpty(e.Exception)).ToList(), TipoDaExcecao, topCount),
                TopSources = TopPor(events, OrigemDoEvento, topCount),
                Timeline = timeline,
                BucketSize = bucketSize,
            };
        }

        private static IReadOnlyList<StatEntry> ContarPorNivel(IReadOnlyList<ClefEvent> events)
        {
            var contagem = events
                .GroupBy(e => e.Level ?? "Information", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var ordenados = LevelOrder
                .Where(contagem.ContainsKey)
                .Select(nivel => new StatEntry(nivel, nivel, contagem[nivel]))
                .ToList();

            ordenados.AddRange(contagem
                .Where(kv => !LevelOrder.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new StatEntry(kv.Key, kv.Key, kv.Value)));

            return ordenados;
        }

        private static IReadOnlyList<StatEntry> TopPor(
            IReadOnlyList<ClefEvent> events,
            Func<ClefEvent, (string Key, string Label)?> seletor,
            int topCount)
        {
            return events
                .Select(seletor)
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new StatEntry(g.Key, g.First().Label, g.Count()))
                .OrderByDescending(e => e.Count)
                .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .Take(topCount)
                .ToList();
        }

        private static (string, string)? MensagemAgrupavel(ClefEvent e)
        {
            var chave = !string.IsNullOrWhiteSpace(e.MessageTemplate) ? e.MessageTemplate : e.Message;
            return string.IsNullOrWhiteSpace(chave) ? null : (chave, chave);
        }

        private static (string, string)? TipoDaExcecao(ClefEvent e)
        {
            if (string.IsNullOrWhiteSpace(e.Exception)) return null;

            var primeiraLinha = e.Exception
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();

            return string.IsNullOrWhiteSpace(primeiraLinha) ? null : (primeiraLinha, primeiraLinha);
        }

        private static (string, string)? OrigemDoEvento(ClefEvent e)
        {
            if (e.Properties is not null
                && e.Properties.TryGetValue("SourceContext", out var ctx)
                && ctx is ScalarValue { Value: string s }
                && !string.IsNullOrWhiteSpace(s))
            {
                return (s, s);
            }

            if (!string.IsNullOrEmpty(e.SourceFile))
            {
                return (e.SourceFile, Path.GetFileName(e.SourceFile));
            }

            return null;
        }

        private static (IReadOnlyList<TimeBucket> Timeline, TimeSpan BucketSize) MontarTimeline(
            IReadOnlyList<ClefEvent> events, int buckets)
        {
            var comHora = events.Where(e => e.Timestamp.HasValue).ToList();
            if (comHora.Count == 0) return (Array.Empty<TimeBucket>(), TimeSpan.Zero);

            var inicio = comHora.Min(e => e.Timestamp!.Value);
            var fim = comHora.Max(e => e.Timestamp!.Value);
            var periodo = fim - inicio;

            if (periodo <= TimeSpan.Zero)
            {
                return (new[] { new TimeBucket(inicio, comHora.Count, comHora.Count(EhErro)) }, TimeSpan.FromSeconds(1));
            }

            var bucketSize = TimeSpan.FromTicks(Math.Max(1, periodo.Ticks / buckets));

            var porFatia = new int[buckets];
            var errosPorFatia = new int[buckets];

            foreach (var e in comHora)
            {
                var offset = (e.Timestamp!.Value - inicio).Ticks / bucketSize.Ticks;
                var indice = (int)Math.Min(offset, buckets - 1);

                porFatia[indice]++;
                if (EhErro(e)) errosPorFatia[indice]++;
            }

            var timeline = Enumerable.Range(0, buckets)
                .Select(i => new TimeBucket(inicio + TimeSpan.FromTicks(bucketSize.Ticks * i), porFatia[i], errosPorFatia[i]))
                .ToList();

            return (timeline, bucketSize);
        }

        private static bool EhErro(ClefEvent e) =>
            string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Level, "Fatal", StringComparison.OrdinalIgnoreCase);
    }
}
