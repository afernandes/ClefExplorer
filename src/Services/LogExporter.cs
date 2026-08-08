using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClefExplorer.Models;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace ClefExplorer.Services
{
    public enum ExportFormat
    {
        Csv,
        Clef,
        Text,
    }

    /// <summary>
    /// Serializa os eventos filtrados. A API de arquivo grava incrementalmente e troca o
    /// destino somente após concluir, evitando duplicar todo o conteúdo na memória.
    /// </summary>
    public static class LogExporter
    {
        public const string DialogFilter =
            "CSV (*.csv)|*.csv|CLEF (*.clef)|*.clef|Texto (*.txt)|*.txt";

        // "$type" e não o "_typeTag" padrão do JsonValueFormatter: é o nome que o CLEF reserva
        // para o tipo de uma estrutura, e o único que o leitor reconhece. Com o padrão, reabrir
        // o arquivo exportado transformava o tipo num campo comum DENTRO da estrutura.
        private static readonly JsonValueFormatter JsonValueFormatter = new(typeTagName: "$type");
        private static readonly UTF8Encoding Utf8WithoutBom = new(false);

        private static readonly Dictionary<string, LogEventPropertyValue> SemPropriedades = new(0);

        /// <summary>Um aviso de progresso a cada 1% do total…</summary>
        private const int FatiasDoProgresso = 100;

        /// <summary>…ou a cada 250 ms, o que vier antes (conjuntos pequenos, discos lentos).</summary>
        private static readonly TimeSpan IntervaloMinimoDeProgresso = TimeSpan.FromMilliseconds(250);

        public static string Extension(ExportFormat format) => format switch
        {
            ExportFormat.Csv => ".csv",
            ExportFormat.Clef => ".clef",
            _ => ".txt",
        };

        public static ExportFormat FormatFromPath(string path)
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Csv;
            if (path.EndsWith(".clef", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Clef;
            return ExportFormat.Text;
        }

        public static string Export(IEnumerable<ClefEvent> events, ExportFormat format) => format switch
        {
            ExportFormat.Csv => ToCsv(events),
            ExportFormat.Clef => ToClef(events),
            _ => ToText(events),
        };

        /// <summary>
        /// Grava em um temporário na mesma pasta e só substitui o destino ao final. Uma
        /// falha ou cancelamento não deixa um arquivo parcialmente exportado.
        /// </summary>
        public static async Task ExportToFileAsync(
            IEnumerable<ClefEvent> events,
            string destinationPath,
            ExportFormat format,
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(events);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            var fullPath = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("Não foi possível determinar a pasta de destino.");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            // TryGetNonEnumeratedCount e não Count(): a sobrecarga aceita IEnumerable e
            // contar aqui consumiria um enumerável de passagem única antes da gravação.
            // Sem total conhecido, o limitador cai só na régua de tempo.
            var total = events.TryGetNonEnumeratedCount(out var contagem) ? contagem : 0;
            var progressoLimitado = new ProgressoLimitado(progress, total);

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    useAsync: true))
                await using (var writer = new StreamWriter(stream, Utf8WithoutBom))
                {
                    await WriteAsync(events, writer, format, cancellationToken, progressoLimitado).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"Não foi possível remover o temporário de exportação '{temporaryPath}'", ex);
                }
            }
        }

        private static Task WriteAsync(
            IEnumerable<ClefEvent> events,
            TextWriter writer,
            ExportFormat format,
            CancellationToken cancellationToken,
            ProgressoLimitado progress) => format switch
        {
            ExportFormat.Csv => WriteCsvAsync(events, writer, cancellationToken, progress),
            ExportFormat.Clef => WriteClefAsync(events, writer, cancellationToken, progress),
            _ => WriteTextAsync(events, writer, cancellationToken, progress),
        };

        /// <summary>
        /// Espaça os avisos de progresso. Antes era um <c>Report</c> POR EVENTO, e cada um
        /// virava um <c>StateHasChanged</c> na UI: exportar 200 mil eventos gastava 3.839 ms
        /// só despachando callbacks no message pump (13,3 s com 1 milhão), sem contar os
        /// renders. Gravar as mesmas 200 mil linhas com 100 avisos custa o mesmo tempo de
        /// escrita (62,7 ms contra 60,4 ms), ou seja: o limitador sai de graça.
        /// </summary>
        private sealed class ProgressoLimitado
        {
            private readonly IProgress<int>? _destino;
            private readonly Stopwatch _relogio = Stopwatch.StartNew();
            private readonly long _passo;
            private long _proximaContagem;
            private TimeSpan _ultimoAviso;
            private int _reportado = -1;

            public ProgressoLimitado(IProgress<int>? destino, int total)
            {
                _destino = destino;
                // Passo 0 = total desconhecido: sobra só a régua de tempo, e a contagem
                // nunca dispara (a alternativa, um passo "infinito", estouraria a soma).
                _passo = total > 0 ? Math.Max(1, total / FatiasDoProgresso) : 0;
                _proximaContagem = _passo > 0 ? _passo : long.MaxValue;
            }

            public void Avancar(int processados)
            {
                if (_destino is null) return;

                var decorrido = _relogio.Elapsed;
                if (processados < _proximaContagem && decorrido - _ultimoAviso < IntervaloMinimoDeProgresso)
                {
                    return;
                }

                if (_passo > 0) _proximaContagem = processados + _passo;
                _ultimoAviso = decorrido;
                _reportado = processados;
                _destino.Report(processados);
            }

            /// <summary>
            /// O último aviso tem de trazer o total exato: com o limitador, o laço termina
            /// entre dois avisos e a barra ficaria parada em 99% depois de o arquivo pronto.
            /// </summary>
            public void Concluir(int processados)
            {
                if (_destino is null || _reportado == processados) return;
                _destino.Report(processados);
            }
        }

        // --- CSV ------------------------------------------------------------------

        public static string ToCsv(IEnumerable<ClefEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            var output = new StringBuilder();
            output.AppendLine("Timestamp,Level,Message,Exception,SourceFile");
            foreach (var evento in events) output.AppendLine(CsvLine(evento));
            return output.ToString();
        }

        private static async Task WriteCsvAsync(
            IEnumerable<ClefEvent> events,
            TextWriter writer,
            CancellationToken cancellationToken,
            ProgressoLimitado progress)
        {
            await writer.WriteLineAsync("Timestamp,Level,Message,Exception,SourceFile".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            var processados = 0;
            foreach (var evento in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(CsvLine(evento).AsMemory(), cancellationToken).ConfigureAwait(false);
                progress.Avancar(++processados);
            }

            progress.Concluir(processados);
        }

        private static string CsvLine(ClefEvent evento) => string.Join(",",
            CsvField(FormatTimestamp(evento.Timestamp)),
            CsvField(evento.Level),
            CsvField(evento.Message),
            CsvField(evento.Exception),
            CsvField(evento.SourceFile));

        /// <summary>
        /// Escapa conforme RFC 4180 e neutraliza células interpretáveis como fórmula por
        /// planilhas. O apóstrofo mantém o conteúdo visível e impede execução ao abrir o CSV.
        /// </summary>
        private static string CsvField(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var semEspacosIniciais = value.TrimStart();
            if (semEspacosIniciais.Length > 0 && semEspacosIniciais[0] is '=' or '+' or '-' or '@')
            {
                value = "'" + value;
            }

            var precisaAspas = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            var escapado = value.Replace("\"", "\"\"");
            return precisaAspas ? $"\"{escapado}\"" : escapado;
        }

        // --- CLEF -----------------------------------------------------------------

        public static string ToClef(IEnumerable<ClefEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            var output = new StringBuilder();
            var cache = new CacheDeTemplates();
            foreach (var evento in events) output.AppendLine(ClefLine(evento, cache));
            return output.ToString();
        }

        private static async Task WriteClefAsync(
            IEnumerable<ClefEvent> events,
            TextWriter writer,
            CancellationToken cancellationToken,
            ProgressoLimitado progress)
        {
            // Cache local à exportação, e não estático: os templates de um log já fechado não
            // podem ficar presos na memória depois que o arquivo foi gravado.
            var cache = new CacheDeTemplates();
            var processados = 0;
            foreach (var evento in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(ClefLine(evento, cache).AsMemory(), cancellationToken).ConfigureAwait(false);
                progress.Avancar(++processados);
            }

            progress.Concluir(processados);
        }

        private static string ClefLine(ClefEvent evento, CacheDeTemplates cache)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            writer.Write('{');
            var primeiro = true;

            WriteJsonProperty(writer, "@t", new ScalarValue(evento.Timestamp?.ToString("O", CultureInfo.InvariantCulture)), ref primeiro);

            if (!string.IsNullOrEmpty(evento.Level)
                && !string.Equals(evento.Level, "Information", StringComparison.OrdinalIgnoreCase))
            {
                WriteJsonProperty(writer, "@l", new ScalarValue(evento.Level), ref primeiro);
            }

            if (!string.IsNullOrEmpty(evento.MessageTemplate))
            {
                WriteJsonProperty(writer, "@mt", new ScalarValue(evento.MessageTemplate), ref primeiro);
                if (PodeTerTokenFormatado(evento.MessageTemplate))
                {
                    WriteRenderings(writer, evento, cache.Obter(evento.MessageTemplate), ref primeiro);
                }
            }
            else if (!string.IsNullOrEmpty(evento.Message))
            {
                WriteJsonProperty(writer, "@m", new ScalarValue(evento.Message), ref primeiro);
            }

            if (!string.IsNullOrEmpty(evento.Exception))
            {
                WriteJsonProperty(writer, "@x", new ScalarValue(evento.Exception), ref primeiro);
            }

            // Trace/span são campos reservados do CLEF, não propriedades comuns. Gravá-los
            // explicitamente mantém a navegação por correlação depois de exportar e reabrir.
            if (!string.IsNullOrWhiteSpace(evento.TraceId))
            {
                WriteJsonProperty(writer, "@tr", new ScalarValue(evento.TraceId), ref primeiro);
            }

            if (!string.IsNullOrWhiteSpace(evento.SpanId))
            {
                WriteJsonProperty(writer, "@sp", new ScalarValue(evento.SpanId), ref primeiro);
            }

            if (!string.IsNullOrWhiteSpace(evento.ParentSpanId))
            {
                WriteJsonProperty(writer, "@ps", new ScalarValue(evento.ParentSpanId), ref primeiro);
            }

            if (evento.SpanStart is { } inicioDoSpan)
            {
                WriteJsonProperty(
                    writer,
                    "@st",
                    new ScalarValue(inicioDoSpan.ToString("O", CultureInfo.InvariantCulture)),
                    ref primeiro);
            }

            if (evento.ObservabilidadeClef is { } observabilidade)
            {
                if (!string.IsNullOrWhiteSpace(observabilidade.TipoSpan))
                {
                    WriteJsonProperty(writer, "@sk", new ScalarValue(observabilidade.TipoSpan), ref primeiro);
                }

                if (observabilidade.EscopoInstrumentacao is { } escopo)
                {
                    WriteJsonProperty(writer, "@sc", escopo, ref primeiro);
                }

                if (observabilidade.AtributosRecurso is { } recurso)
                {
                    WriteJsonProperty(writer, "@ra", recurso, ref primeiro);
                }
            }

            if (evento.Properties is not null)
            {
                foreach (var propriedade in evento.Properties)
                {
                    WriteJsonProperty(writer, EscapeReservedName(propriedade.Key), propriedade.Value, ref primeiro);
                }
            }

            writer.Write('}');
            return writer.ToString();
        }

        /// <summary>
        /// Descarta, sem compilar o template, quem não tem como trazer token formatado: o
        /// formato só existe depois de ':' DENTRO de um token (<c>{Now:O}</c>).
        ///
        /// <para>Vale o desvio porque metade dos eventos reais não traz <c>@mt</c> — para eles a
        /// leitura usa a MENSAGEM INTEIRA escapada como template, e cada evento vira um template
        /// diferente (307.609 em 400.000, o maior com 21.820 caracteres). Compilar e guardar
        /// todos custava 3 µs por evento e enchia o cache com uma entrada por linha exportada.</para>
        /// </summary>
        private static bool PodeTerTokenFormatado(string texto)
        {
            var inicio = texto.IndexOf('{');
            while (inicio >= 0 && inicio + 1 < texto.Length)
            {
                // "{{" é chave literal e não abre token.
                if (texto[inicio + 1] == '{')
                {
                    inicio = texto.IndexOf('{', inicio + 2);
                    continue;
                }

                var fim = texto.IndexOf('}', inicio + 1);
                if (fim < 0) return false;
                if (texto.AsSpan(inicio + 1, fim - inicio - 1).Contains(':')) return true;
                inicio = texto.IndexOf('{', fim + 1);
            }

            return false;
        }

        /// <summary>
        /// Reemite o <c>@r</c>: o texto JÁ RENDERIZADO de cada token do template que tem
        /// formato, na ordem em que os tokens aparecem — é o contrato do
        /// <c>CompactJsonFormatter</c>.
        ///
        /// <para>Sem ele, reler o arquivo exportado mudava a mensagem de todo evento com token
        /// formatado: <c>{Now:O}</c> caía no valor cru e o <see cref="ScalarValue"/> devolvia a
        /// string ENTRE ASPAS (38 dos 314.973 eventos de um log real).</para>
        ///
        /// <para>Renderizar pelo próprio <see cref="PropertyToken"/> devolve o <c>@r</c>
        /// ORIGINAL sem custo de memória por evento: o leitor guarda as renderizações lidas
        /// dentro do valor da propriedade, então formatar de novo escreve o texto recebido em
        /// vez de reformatar o valor. Guardar o <c>@r</c> num <c>string[]</c> à parte seria um
        /// campo em TODOS os eventos para servir aos 4,4% que têm token formatado.</para>
        /// </summary>
        private static void WriteRenderings(
            TextWriter writer,
            ClefEvent evento,
            TemplateCompilado template,
            ref bool first)
        {
            var tokens = template.TokensFormatados;
            if (tokens.Length == 0) return;

            if (!first) writer.Write(',');
            first = false;
            JsonValueFormatter.WriteQuotedJsonString("@r", writer);
            writer.Write(":[");

            var rascunho = new StringBuilder();
            using var renderizado = new StringWriter(rascunho, CultureInfo.InvariantCulture);
            IReadOnlyDictionary<string, LogEventPropertyValue> propriedades = evento.Properties ?? SemPropriedades;

            for (var i = 0; i < tokens.Length; i++)
            {
                if (i > 0) writer.Write(',');
                rascunho.Clear();
                // InvariantCulture como no formatador oficial: o texto do @r não pode depender
                // da cultura da máquina que exportou.
                tokens[i].Render(propriedades, renderizado, CultureInfo.InvariantCulture);
                JsonValueFormatter.WriteQuotedJsonString(rascunho.ToString(), writer);
            }

            writer.Write(']');
        }

        /// <summary>
        /// Dobra o '@' inicial — o escape que o formato prevê e que o leitor desfaz ao abrir.
        /// Essas propriedades eram DESCARTADAS na exportação, e com elas ia embora o <c>@i</c>
        /// (id do evento), que a leitura guarda como uma propriedade chamada "@i".
        /// </summary>
        private static string EscapeReservedName(string name) => name.StartsWith('@') ? '@' + name : name;

        private static void WriteJsonProperty(
            TextWriter writer,
            string name,
            LogEventPropertyValue value,
            ref bool first)
        {
            if (!first) writer.Write(',');
            first = false;
            JsonValueFormatter.WriteQuotedJsonString(name, writer);
            writer.Write(':');
            JsonValueFormatter.Format(value, writer);
        }

        // --- Texto ----------------------------------------------------------------

        public static string ToText(IEnumerable<ClefEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            var output = new StringBuilder();
            foreach (var evento in events) AppendText(output, evento);
            return output.ToString();
        }

        private static async Task WriteTextAsync(
            IEnumerable<ClefEvent> events,
            TextWriter writer,
            CancellationToken cancellationToken,
            ProgressoLimitado progress)
        {
            var processados = 0;
            foreach (var evento in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(
                    $"[{FormatTimestamp(evento.Timestamp)}] {evento.Level ?? "Information"}: {evento.Message}"
                        .AsMemory(),
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(evento.Exception))
                {
                    await writer.WriteLineAsync(evento.Exception.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                progress.Avancar(++processados);
            }

            progress.Concluir(processados);
        }

        private static void AppendText(StringBuilder output, ClefEvent evento)
        {
            output.Append('[').Append(FormatTimestamp(evento.Timestamp)).Append("] ")
                .Append(evento.Level ?? "Information").Append(": ")
                .AppendLine(evento.Message);
            if (!string.IsNullOrEmpty(evento.Exception)) output.AppendLine(evento.Exception);
        }

        private static string FormatTimestamp(DateTimeOffset? timestamp) =>
            timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
