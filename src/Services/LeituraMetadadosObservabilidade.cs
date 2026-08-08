using System.Globalization;
using System.Numerics;
using System.Xml;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Converte extensões Seq/CLEF e campos OTLP conhecidos em metadados comuns. Aliases
    /// configuráveis só são usados para nomes e durações autodescritivas; números sem
    /// unidade nunca viram duração por inferência.
    /// </summary>
    public sealed class LeituraMetadadosObservabilidade
    {
        private static readonly DateTimeOffset UnixEpoch = DateTimeOffset.UnixEpoch;

        public MetadadosObservabilidadeEvento Extrair(
            ClefEvent evento,
            ConfiguracaoObservabilidade? configuracao = null)
        {
            ArgumentNullException.ThrowIfNull(evento);
            configuracao ??= new ConfiguracaoObservabilidade();

            var valores = new List<ValorNomeado>(evento.Properties?.Count + 8 ?? 8);
            if (evento.ObservabilidadeClef?.AtributosRecurso is { } recurso)
            {
                Enumerar(recurso, "@Resource", valores, 0);
            }
            if (evento.ObservabilidadeClef?.EscopoInstrumentacao is { } escopo)
            {
                Enumerar(escopo, "@Scope", valores, 0);
            }
            if (evento.Properties is not null)
            {
                foreach (var propriedade in evento.Properties)
                {
                    Enumerar(propriedade.Value, propriedade.Key, valores, 0);
                }
            }

            var traceId = PrimeiroTexto(evento.TraceId, EncontrarTexto(valores, "traceId"));
            var spanId = PrimeiroTexto(evento.SpanId, EncontrarTexto(valores, "spanId"));
            var parentSpanId = PrimeiroTexto(
                evento.ParentSpanId,
                EncontrarTexto(valores, "parentSpanId"),
                EncontrarTexto(valores, "parentId"));

            var inicioOtlp = Encontrar(valores, "startTimeUnixNano");
            var fimOtlp = Encontrar(valores, "endTimeUnixNano");
            var inicioOtlpConvertido = default(DateTimeOffset);
            var fimOtlpConvertido = default(DateTimeOffset);
            var ehOtlp = inicioOtlp is not null
                && fimOtlp is not null
                && TentarTimestampUnixNano(inicioOtlp.Value.Valor, out inicioOtlpConvertido)
                && TentarTimestampUnixNano(fimOtlp.Value.Valor, out fimOtlpConvertido)
                && inicioOtlpConvertido <= fimOtlpConvertido;

            var nomeExplicito = ehOtlp
                ? EncontrarTexto(valores, "name")
                : null;
            nomeExplicito ??= EncontrarTexto(valores, configuracao.CamposNomeOperacao);

            var tipoSpan = PrimeiroTexto(
                evento.ObservabilidadeClef?.TipoSpan,
                ehOtlp ? FormatarSpanKindOtlp(EncontrarEscalar(valores, "kind")) : null,
                EncontrarTexto(valores, configuracao.CamposTipoSpan));

            var nomeServico = EncontrarTexto(valores, configuracao.CamposNomeServico);
            var nomeOperacao = PrimeiroTexto(
                nomeExplicito,
                evento.MessageTemplate,
                evento.Message,
                "(operação sem nome)")!;

            if (evento.SpanStart is { } inicioSeq
                && evento.Timestamp is { } fimSeq
                && inicioSeq <= fimSeq)
            {
                return new MetadadosObservabilidadeEvento(
                    traceId,
                    spanId,
                    parentSpanId,
                    nomeOperacao,
                    nomeServico,
                    tipoSpan,
                    inicioSeq,
                    fimSeq,
                    OrigemDuracaoObservabilidade.SeqClef,
                    "@st");
            }

            if (ehOtlp)
            {
                return new MetadadosObservabilidadeEvento(
                    traceId,
                    spanId,
                    parentSpanId,
                    nomeOperacao,
                    nomeServico,
                    tipoSpan,
                    inicioOtlpConvertido,
                    fimOtlpConvertido,
                    OrigemDuracaoObservabilidade.OpenTelemetryOtlp,
                    "startTimeUnixNano/endTimeUnixNano");
            }

            // Uma duração configurada só identifica um span quando há contexto explícito
            // (nome/kind + SpanId) e o próprio valor carrega unidade. "Elapsed: 42" é
            // deliberadamente ignorado: poderia significar ticks, ms, segundos ou contagem.
            var duracaoInformada = Encontrar(valores, configuracao.CamposDuracao);
            if (!string.IsNullOrWhiteSpace(spanId)
                && evento.Timestamp is { } fimInformado
                && (nomeExplicito is not null || tipoSpan is not null)
                && duracaoInformada is { } valorDuracao
                && TentarDuracaoAutodescritiva(valorDuracao.Valor, out var duracao)
                && duracao >= TimeSpan.Zero
                && duracao <= fimInformado - DateTimeOffset.MinValue)
            {
                return new MetadadosObservabilidadeEvento(
                    traceId,
                    spanId,
                    parentSpanId,
                    nomeOperacao,
                    nomeServico,
                    tipoSpan,
                    fimInformado - duracao,
                    fimInformado,
                    OrigemDuracaoObservabilidade.CampoConfigurado,
                    valorDuracao.Caminho);
            }

            return new MetadadosObservabilidadeEvento(
                traceId,
                spanId,
                parentSpanId,
                nomeOperacao,
                nomeServico,
                tipoSpan,
                null,
                null,
                OrigemDuracaoObservabilidade.Nenhuma,
                null);
        }

        private static void Enumerar(
            LogEventPropertyValue valor,
            string caminho,
            List<ValorNomeado> destino,
            int profundidade)
        {
            if (profundidade > 16) return;
            var nome = caminho[(caminho.LastIndexOf('.') + 1)..];
            destino.Add(new ValorNomeado(nome, caminho, valor));

            switch (valor)
            {
                case StructureValue estrutura:
                    foreach (var propriedade in estrutura.Properties)
                    {
                        Enumerar(
                            propriedade.Value,
                            $"{caminho}.{propriedade.Name}",
                            destino,
                            profundidade + 1);
                    }
                    break;

                case DictionaryValue dicionario:
                    foreach (var par in dicionario.Elements)
                    {
                        if (par.Key is ScalarValue { Value: string chave })
                        {
                            Enumerar(par.Value, $"{caminho}.{chave}", destino, profundidade + 1);
                        }
                    }
                    break;
            }
        }

        private static ValorNomeado? Encontrar(
            IReadOnlyList<ValorNomeado> valores,
            string alias) => Encontrar(valores, new[] { alias });

        private static ValorNomeado? Encontrar(
            IReadOnlyList<ValorNomeado> valores,
            IEnumerable<string> aliases)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                foreach (var valor in valores)
                {
                    if (valor.Nome.Equals(alias, StringComparison.OrdinalIgnoreCase)
                        || valor.Caminho.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    {
                        return valor;
                    }
                }
            }

            return null;
        }

        private static string? EncontrarTexto(
            IReadOnlyList<ValorNomeado> valores,
            string alias) => Texto(Encontrar(valores, alias)?.Valor);

        private static string? EncontrarTexto(
            IReadOnlyList<ValorNomeado> valores,
            IEnumerable<string> aliases) => Texto(Encontrar(valores, aliases)?.Valor);

        private static object? EncontrarEscalar(
            IReadOnlyList<ValorNomeado> valores,
            string alias) => (Encontrar(valores, alias)?.Valor as ScalarValue)?.Value;

        private static string? Texto(LogEventPropertyValue? valor) => valor is ScalarValue escalar
            ? Formatar(escalar.Value)
            : null;

        private static string? Formatar(object? valor) => valor switch
        {
            null => null,
            string texto when !string.IsNullOrWhiteSpace(texto) => texto.Trim(),
            IFormattable formatavel => formatavel.ToString(null, CultureInfo.InvariantCulture),
            _ => valor.ToString(),
        };

        private static string? PrimeiroTexto(params string?[] valores) => valores
            .FirstOrDefault(valor => !string.IsNullOrWhiteSpace(valor))
            ?.Trim();

        private static string? FormatarSpanKindOtlp(object? valor)
        {
            var texto = Formatar(valor);
            return texto switch
            {
                "0" => "Unspecified",
                "1" => "Internal",
                "2" => "Server",
                "3" => "Client",
                "4" => "Producer",
                "5" => "Consumer",
                _ => texto,
            };
        }

        private static bool TentarTimestampUnixNano(
            LogEventPropertyValue valor,
            out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (valor is not ScalarValue escalar) return false;
            var texto = Formatar(escalar.Value);
            if (texto is null
                || !BigInteger.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos)
                || nanos < BigInteger.Zero)
            {
                return false;
            }

            var ticks = nanos / 100;
            var maximo = new BigInteger(DateTimeOffset.MaxValue.UtcTicks - UnixEpoch.UtcTicks);
            if (ticks > maximo) return false;

            timestamp = new DateTimeOffset(UnixEpoch.UtcTicks + (long)ticks, TimeSpan.Zero);
            return true;
        }

        private static bool TentarDuracaoAutodescritiva(
            LogEventPropertyValue valor,
            out TimeSpan duracao)
        {
            duracao = default;
            if (valor is not ScalarValue escalar) return false;
            if (escalar.Value is TimeSpan timeSpan)
            {
                duracao = timeSpan;
                return true;
            }
            if (escalar.Value is not string texto || string.IsNullOrWhiteSpace(texto)) return false;

            texto = texto.Trim();
            if (texto.Contains(':')
                && TimeSpan.TryParse(texto, CultureInfo.InvariantCulture, out duracao))
            {
                return true;
            }

            if (texto.StartsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    duracao = XmlConvert.ToTimeSpan(texto);
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            var unidades = new (string Sufixo, Func<double, TimeSpan> Converter)[]
            {
                ("ticks", valor => TimeSpan.FromTicks((long)valor)),
                ("ns", valor => TimeSpan.FromTicks((long)(valor / 100d))),
                ("µs", valor => TimeSpan.FromTicks((long)(valor * 10d))),
                ("us", valor => TimeSpan.FromTicks((long)(valor * 10d))),
                ("ms", TimeSpan.FromMilliseconds),
                ("min", TimeSpan.FromMinutes),
                ("s", TimeSpan.FromSeconds),
                ("h", TimeSpan.FromHours),
            };

            foreach (var (sufixo, converter) in unidades)
            {
                if (!texto.EndsWith(sufixo, StringComparison.OrdinalIgnoreCase)) continue;
                var numero = texto[..^sufixo.Length].Trim();
                if (!double.TryParse(
                        numero,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var valorNumerico)
                    || !double.IsFinite(valorNumerico)
                    || valorNumerico < 0)
                {
                    return false;
                }

                try
                {
                    duracao = converter(valorNumerico);
                    return true;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }

            return false;
        }

        private readonly record struct ValorNomeado(
            string Nome,
            string Caminho,
            LogEventPropertyValue Valor);
    }
}
