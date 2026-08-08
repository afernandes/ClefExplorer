using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ClefExplorer.Models;
using Serilog.Events;
using Serilog.Parsing;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Templates e chaves de propriedade compilados/compartilhados durante UMA carga.
    ///
    /// <para>O leitor anterior reparseava o <c>@mt</c> a cada linha: num arquivo de 200 mil
    /// eventos eram 200 mil parses para 5 templates distintos. Guardar o
    /// <see cref="MessageTemplate"/> pronto (e a mensagem já renderizada, quando o template
    /// não tem propriedade nenhuma) tira esse trabalho do caminho quente.</para>
    ///
    /// <para>É <see cref="ConcurrentDictionary{TKey,TValue}"/> porque a carga lê os arquivos
    /// com <c>Parallel.ForEachAsync</c> compartilhando o mesmo cache — um Dictionary comum
    /// corrompe a tabela sob concorrência.</para>
    ///
    /// <para>O escopo é o da carga, não estático: a instância é amarrada ao
    /// <see cref="PoolDeTextos"/> por uma <see cref="ConditionalWeakTable{TKey,TValue}"/>, de
    /// modo que os templates de um log já fechado somem junto com o pool. Cache estático
    /// seguraria para sempre o texto de logs que o usuário nem abriu mais.</para>
    /// </summary>
    public sealed class CacheDeTemplates
    {
        private static readonly ConditionalWeakTable<PoolDeTextos, CacheDeTemplates> PorPool = new();
        private static readonly MessageTemplateParser Parser = new();

        private readonly PoolDeTextos? _pool;
        private readonly ConcurrentDictionary<string, TemplateCompilado> _templates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _textos = new(StringComparer.Ordinal);

        // As buscas por span são o caminho quente (uma por propriedade de cada linha); guardar
        // a estrutura de lookup evita revalidar o comparador a cada chamada.
        private readonly ConcurrentDictionary<string, TemplateCompilado>.AlternateLookup<ReadOnlySpan<char>> _templatesPorSpan;
        private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _textosPorSpan;

        public CacheDeTemplates(PoolDeTextos? pool = null)
        {
            _pool = pool;
            _templatesPorSpan = _templates.GetAlternateLookup<ReadOnlySpan<char>>();
            _textosPorSpan = _textos.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        /// <summary>
        /// Template de um evento sem <c>@mt</c> e sem <c>@m</c> (texto e mensagem vazios). É
        /// estático porque o resultado é sempre o mesmo e o tail cria um cache por linha.
        /// </summary>
        internal static TemplateCompilado Vazio { get; } = new(string.Empty, Parser.Parse(string.Empty));

        /// <summary>
        /// Devolve o cache da carga a que este pool pertence. Sem pool (tail e testes) cada
        /// chamada ganha um cache próprio — o mesmo custo do leitor antigo, que reparseava tudo.
        /// </summary>
        public static CacheDeTemplates Para(PoolDeTextos? pool) =>
            pool is null ? new CacheDeTemplates(null) : PorPool.GetValue(pool, static p => new CacheDeTemplates(p));

        internal TemplateCompilado Obter(string texto) =>
            _templates.GetOrAdd(Compartilhar(texto), static t => new TemplateCompilado(t, Parser.Parse(t)));

        /// <summary>
        /// Busca o template pelos caracteres já decodificados, sem materializar string: no
        /// caminho quente o texto do <c>@mt</c> é o mesmo em toda linha e só a primeira precisa
        /// virar objeto.
        /// </summary>
        internal TemplateCompilado Obter(ReadOnlySpan<char> texto)
        {
            if (_templatesPorSpan.TryGetValue(texto, out var compilado))
            {
                return compilado;
            }

            return Obter(new string(texto));
        }

        internal string Compartilhar(string texto)
        {
            if (texto.Length == 0) return string.Empty;
            var canonico = _pool?.Compartilhar(texto) ?? texto;
            return _textos.GetOrAdd(canonico, canonico);
        }

        internal string Compartilhar(ReadOnlySpan<char> texto)
        {
            if (texto.IsEmpty) return string.Empty;
            if (_textosPorSpan.TryGetValue(texto, out var existente))
            {
                return existente;
            }

            return Compartilhar(new string(texto));
        }

        // ── Pool de valores escalares ────────────────────────────────────────────
        //
        // A premissa "valor é único por evento" caiu na medição: nos logs reais, os
        // valores das propriedades têm 9,9% de cardinalidade — "VAREJO", "PDV OMNI",
        // o nome da máquina e o CNPJ se repetem em TODA linha, cada uma criando seu
        // próprio ScalarValue com sua própria string. Compartilhar a INSTÂNCIA é seguro
        // (ScalarValue é imutável no Serilog) e elimina as duas alocações de uma vez.

        /// <summary>true/false/null são três valores no mundo — três objetos no processo.</summary>
        public static readonly ScalarValue EscalarVerdadeiro = new(true);
        public static readonly ScalarValue EscalarFalso = new(false);
        public static readonly ScalarValue EscalarNulo = new(null);

        // Contagens pequenas (ProcessorCount, códigos de loja, quantidades) dominam os
        // inteiros dos logs reais; o cache evita o boxing E o ScalarValue por linha.
        private static readonly ScalarValue[] LongsPequenos = CriarLongsPequenos();

        private static ScalarValue[] CriarLongsPequenos()
        {
            var valores = new ScalarValue[1024];
            for (var i = 0; i < valores.Length; i++) valores[i] = new ScalarValue((long)i);
            return valores;
        }

        // Caps: um log despeja um GUID novo por linha (SpanId) e, sem teto, o pool
        // guardaria 315 mil strings que nunca repetem. Os valores QUENTES aparecem nas
        // primeiras linhas e entram antes de o teto ser atingido.
        private const int MaximoDeEscalares = 64 * 1024;
        private const int MaximoDeCandidatos = 64 * 1024;
        private const int MaiorTextoCompartilhavel = 128;

        private readonly ConcurrentDictionary<string, ScalarValue> _escalares = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _candidatos = new(StringComparer.Ordinal);

        // Contadores próprios: ConcurrentDictionary.Count ADQUIRE TODOS OS LOCKS da
        // tabela — chamado uma vez por valor de cada linha, com 20 workers, serializava
        // a leitura paralela inteira (medido: a carga triplicou por causa disso).
        private int _totalEscalares;
        private int _totalCandidatos;

        public ScalarValue EscalarDe(string? texto)
        {
            if (texto is null) return EscalarNulo;
            if (texto.Length is 0 or > MaiorTextoCompartilhavel) return new ScalarValue(texto);
            if (_escalares.TryGetValue(texto, out var existente)) return existente;

            // Promoção na SEGUNDA vista: um log despeja um GUID único por linha (SpanId),
            // e inseri-lo direto no pool o encheria de texto que nunca repete. O que não
            // reaparece morre na lista de candidatos; só o que repete é promovido.
            if (Volatile.Read(ref _totalCandidatos) < MaximoDeCandidatos && _candidatos.TryAdd(texto, true))
            {
                Interlocked.Increment(ref _totalCandidatos);
                return new ScalarValue(texto);
            }

            if (_candidatos.TryRemove(texto, out _) && Volatile.Read(ref _totalEscalares) < MaximoDeEscalares)
            {
                Interlocked.Increment(ref _totalEscalares);
                return _escalares.GetOrAdd(texto, static t => new ScalarValue(t));
            }

            return new ScalarValue(texto);
        }

        public static ScalarValue EscalarDeNumero(object bruto) =>
            bruto is long inteiro and >= 0 and < 1024 ? LongsPequenos[(int)inteiro] : new ScalarValue(bruto);
    }

    /// <summary>
    /// Um <c>@mt</c> já parseado. <see cref="Constante"/> marca o template sem nenhum
    /// <see cref="PropertyToken"/>: a mensagem dele nunca muda, então renderizar uma vez basta
    /// — é o caso de todo evento que traz só <c>@m</c> (o template é o próprio texto escapado).
    /// </summary>
    internal sealed class TemplateCompilado
    {
        private static readonly Dictionary<string, LogEventPropertyValue> SemPropriedades = new(0);

        public TemplateCompilado(string texto, MessageTemplate template)
        {
            Texto = texto;
            Template = template;

            var formatados = new List<PropertyToken>();
            var temPropriedade = false;
            foreach (var token in template.Tokens)
            {
                if (token is not PropertyToken propriedade) continue;
                temPropriedade = true;
                if (propriedade.Format != null) formatados.Add(propriedade);
            }

            TokensFormatados = formatados.Count == 0 ? [] : formatados.ToArray();
            Constante = !temPropriedade;
            // Renderiza pelo próprio Serilog em vez de "desescapar" o texto na mão: é o que
            // garante que "{{" volte a ser "{" exatamente como o leitor antigo devolvia.
            TextoRenderizado = Constante ? template.Render(SemPropriedades, CultureInfo.InvariantCulture) : string.Empty;
        }

        public string Texto { get; }

        public MessageTemplate Template { get; }

        public PropertyToken[] TokensFormatados { get; }

        public bool Constante { get; }

        public string TextoRenderizado { get; }
    }

    /// <summary>
    /// Parser CLEF sobre <see cref="Utf8JsonReader"/>, escrito para substituir o
    /// <c>Serilog.Formatting.Compact.Reader</c> (que passa por Newtonsoft e materializa um
    /// JObject por linha).
    ///
    /// <para>Cada regra aqui reproduz o comportamento observado do leitor oficial, incluindo as
    /// partes contraintuitivas: número integral que não cabe em <see cref="long"/> vira
    /// <see cref="BigInteger"/> (nunca <c>decimal</c>), <c>@m</c> é descartado quando há
    /// <c>@mt</c>, e <c>@r</c> casa POSICIONALMENTE com os tokens formatados do template.</para>
    /// </summary>
    public static class LeitorClef
    {
        // Nomes reservados na comparação EXATA (sensível a maiúsculas) — "@T" não é reservado
        // e vira propriedade comum, como no leitor oficial.
        private const string NomeInvalidoSubstituto = "(unnamed)";
        private const int TamanhoRascunho = 512;

        private static readonly JsonReaderOptions Opcoes = new()
        {
            // Sem AllowTrailingCommas e sem comentários de propósito: a leniência do Newtonsoft
            // (vírgula final, aspas simples, JSON truncado) não é reproduzida — nenhum formatter
            // do Serilog produz isso e aceitar mais só esconde arquivo corrompido.
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };

        public static bool TentarLer(
            ReadOnlySpan<byte> linha,
            string arquivo,
            CacheDeTemplates cache,
            out ClefEvent? evento,
            out string? erro)
        {
            try
            {
                evento = Ler(linha, arquivo, cache);
                erro = null;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Isolamento por linha: uma linha corrompida não pode derrubar o arquivo inteiro.
                evento = null;
                erro = ex.Message;
                return false;
            }
        }

        public static bool TentarLer(
            string linha,
            string arquivo,
            CacheDeTemplates cache,
            out ClefEvent? evento,
            out string? erro)
        {
            ArgumentNullException.ThrowIfNull(linha);

            var maximo = Encoding.UTF8.GetMaxByteCount(linha.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(maximo);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(linha, buffer);
                return TentarLer(buffer.AsSpan(0, bytes), arquivo, cache, out evento, out erro);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Linha vazia ou só com espaços é pulada, não conta como inválida.</summary>
        public static bool EhLinhaEmBranco(ReadOnlySpan<byte> linha)
        {
            foreach (var b in linha)
            {
                // O leitor antigo usava string.IsNullOrWhiteSpace sobre a linha já decodificada;
                // na prática só os brancos ASCII aparecem entre registros CLEF.
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C)) return false;
            }

            return true;
        }

        private static ClefEvent Ler(ReadOnlySpan<byte> linha, string arquivo, CacheDeTemplates cache)
        {
            var reader = new Utf8JsonReader(linha, Opcoes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("A linha não é um objeto JSON completo.");
            }

            Span<char> rascunho = stackalloc char[TamanhoRascunho];

            DateTimeOffset? timestamp = null;
            TemplateCompilado? templateDeclarado = null;
            string? mensagemPronta = null;
            string? nivel = null;
            string? excecao = null;
            string? traceId = null;
            string? spanId = null;
            string? parentSpanId = null;
            DateTimeOffset? spanStart = null;
            string? spanKind = null;
            LogEventPropertyValue? instrumentationScope = null;
            LogEventPropertyValue? resourceAttributes = null;
            object? eventId = null;
            var temEventId = false;
            List<LogEventProperty>? propriedades = null;
            List<(string? Texto, bool Convertivel)>? renderizacoes = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (EhReservado(ref reader))
                {
                    if (reader.ValueTextEquals("@t"u8))
                    {
                        timestamp = LerTimestamp(ref reader, "@t", obrigatorio: true);
                    }
                    else if (reader.ValueTextEquals("@mt"u8))
                    {
                        templateDeclarado = LerTemplate(ref reader, rascunho, cache);
                    }
                    else if (reader.ValueTextEquals("@m"u8))
                    {
                        mensagemPronta = LerTexto(ref reader, "@m");
                    }
                    else if (reader.ValueTextEquals("@l"u8))
                    {
                        nivel = LerNivel(ref reader, cache);
                    }
                    else if (reader.ValueTextEquals("@x"u8))
                    {
                        excecao = LerTexto(ref reader, "@x");
                    }
                    else if (reader.ValueTextEquals("@r"u8))
                    {
                        renderizacoes = LerRenderizacoes(ref reader);
                    }
                    else if (reader.ValueTextEquals("@i"u8))
                    {
                        temEventId = LerEventId(ref reader, out eventId);
                    }
                    else if (reader.ValueTextEquals("@tr"u8))
                    {
                        traceId = LerTexto(ref reader, "@tr");
                        // Além de guardar o texto original, preservamos a validação do
                        // leitor oficial: um identificador corrompido invalida a linha.
                        if (traceId != null) ActivityTraceId.CreateFromString(traceId.AsSpan());
                    }
                    else if (reader.ValueTextEquals("@sp"u8))
                    {
                        spanId = LerTexto(ref reader, "@sp");
                        if (spanId != null) ActivitySpanId.CreateFromString(spanId.AsSpan());
                    }
                    else if (reader.ValueTextEquals("@ps"u8))
                    {
                        parentSpanId = LerTexto(ref reader, "@ps");
                        if (parentSpanId != null) ActivitySpanId.CreateFromString(parentSpanId.AsSpan());
                    }
                    else if (reader.ValueTextEquals("@st"u8))
                    {
                        spanStart = LerTimestamp(ref reader, "@st", obrigatorio: false);
                    }
                    else if (reader.ValueTextEquals("@sk"u8))
                    {
                        spanKind = LerTexto(ref reader, "@sk");
                    }
                    else if (reader.ValueTextEquals("@sc"u8))
                    {
                        reader.Read();
                        instrumentationScope = LerValor(ref reader, rascunho, cache);
                    }
                    else
                    {
                        reader.Read();
                        resourceAttributes = LerValor(ref reader, rascunho, cache);
                    }

                    continue;
                }

                var nome = LerNomeDePropriedade(ref reader, rascunho, cache);
                reader.Read();
                var valor = LerValor(ref reader, rascunho, cache);
                Acrescentar(ref propriedades, nome, valor);
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                // Texto solto depois do objeto: o Newtonsoft aceitava e ignorava, aqui é linha
                // inválida (arquivo corrompido não deve passar por evento bom).
                throw new InvalidDataException("A linha não é um objeto JSON completo.");
            }

            if (timestamp is not { } instante)
            {
                throw new InvalidDataException("A linha não inclui o campo obrigatório `@t`.");
            }

            var template = templateDeclarado
                ?? (mensagemPronta is null ? CacheDeTemplates.Vazio : cache.Obter(Escapar(mensagemPronta)));

            if (renderizacoes is not null)
            {
                AplicarRenderizacoes(propriedades, template.TokensFormatados, renderizacoes);
            }

            // O @i entra por último na lista, exatamente como no leitor oficial.
            if (temEventId) Acrescentar(ref propriedades, cache.Compartilhar("@i"), new ScalarValue(eventId), substituir: false);

            var evento = new ClefEvent
            {
                Timestamp = instante,
                Level = nivel ?? "Information",
                MessageTemplate = template.Texto,
                Message = template.Constante
                    ? template.TextoRenderizado
                    // InvariantCulture explícito: a mensagem tem de sair idêntica numa máquina
                    // pt-BR e numa en-US (o leitor antigo não passava provider e caía no
                    // invariante por acaso, dentro do ScalarValue).
                    : template.Template.Render(new PropriedadesOrdinais(propriedades), CultureInfo.InvariantCulture),
                Exception = excecao,
                SourceFile = arquivo,
                TraceId = traceId,
                SpanId = spanId,
                ParentSpanId = parentSpanId,
                SpanStart = spanStart,
                ObservabilidadeClef = spanKind is null
                    && instrumentationScope is null
                    && resourceAttributes is null
                        ? null
                        : new MetadadosClefObservabilidade
                        {
                            TipoSpan = spanKind,
                            EscopoInstrumentacao = instrumentationScope,
                            AtributosRecurso = resourceAttributes,
                        },
                // A forma compacta em vez de Dictionary: 18 pares imutáveis não precisam
                // de buckets, e multiplicado por centenas de milhares de eventos o
                // dicionário era uma das maiores fatias da memória retida.
                Properties = propriedades is null
                    ? PropriedadesEvento.Vazio
                    : new PropriedadesEvento(propriedades),
            };

            return evento;
        }

        /// <summary>
        /// Só vale a pena comparar com os quatorze nomes reservados quando o nome começa com '@'.
        /// Nome escapado ("@t") entra na comparação porque o ValueTextEquals desescapa.
        /// </summary>
        private static bool EhReservado(ref Utf8JsonReader reader)
        {
            if (!reader.ValueIsEscaped)
            {
                var bruto = reader.ValueSpan;
                if (bruto.Length is < 2 or > 3 || bruto[0] != (byte)'@') return false;
            }

            return reader.ValueTextEquals("@t"u8)
                || reader.ValueTextEquals("@mt"u8)
                || reader.ValueTextEquals("@m"u8)
                || reader.ValueTextEquals("@l"u8)
                || reader.ValueTextEquals("@x"u8)
                || reader.ValueTextEquals("@r"u8)
                || reader.ValueTextEquals("@i"u8)
                || reader.ValueTextEquals("@tr"u8)
                || reader.ValueTextEquals("@sp"u8)
                || reader.ValueTextEquals("@ps"u8)
                || reader.ValueTextEquals("@st"u8)
                || reader.ValueTextEquals("@sk"u8)
                || reader.ValueTextEquals("@sc"u8)
                || reader.ValueTextEquals("@ra"u8);
        }

        private static void Acrescentar(
            ref List<LogEventProperty>? propriedades,
            string nome,
            LogEventPropertyValue valor,
            bool substituir = true)
        {
            propriedades ??= new List<LogEventProperty>(8);
            if (substituir)
            {
                // Chave repetida: o JObject do leitor antigo substitui o valor mantendo a posição
                // da PRIMEIRA ocorrência. Sem isso a propriedade apareceria duas vezes.
                for (var i = 0; i < propriedades.Count; i++)
                {
                    if (!string.Equals(propriedades[i].Name, nome, StringComparison.Ordinal)) continue;
                    propriedades[i] = new LogEventProperty(nome, valor);
                    return;
                }
            }

            propriedades.Add(new LogEventProperty(nome, valor));
        }

        private static string? LerTexto(ref Utf8JsonReader reader, string campo)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new InvalidDataException($"O valor de `{campo}` não está num formato suportado.");
            }

            return reader.GetString();
        }

        private static TemplateCompilado? LerTemplate(ref Utf8JsonReader reader, scoped Span<char> rascunho, CacheDeTemplates cache)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new InvalidDataException("O valor de `@mt` não está num formato suportado.");
            }

            if (TamanhoEmBytes(ref reader) <= rascunho.Length)
            {
                var lidos = reader.CopyString(rascunho);
                return cache.Obter(rascunho[..lidos]);
            }

            return cache.Obter(reader.GetString()!);
        }

        private static string LerNivel(ref Utf8JsonReader reader, CacheDeTemplates cache)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.Null) return "Information";
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new InvalidDataException("O valor de `@l` não está num formato suportado.");
            }

            // Atalho pelos bytes para os seis nomes canônicos: cobre praticamente todas as
            // linhas e evita string + Enum.TryParse + ToString por evento.
            if (reader.ValueTextEquals("Information"u8)) return "Information";
            if (reader.ValueTextEquals("Error"u8)) return "Error";
            if (reader.ValueTextEquals("Warning"u8)) return "Warning";
            if (reader.ValueTextEquals("Debug"u8)) return "Debug";
            if (reader.ValueTextEquals("Verbose"u8)) return "Verbose";
            if (reader.ValueTextEquals("Fatal"u8)) return "Fatal";

            var texto = reader.GetString()!;
            // Enum.TryParse é a própria regra do leitor oficial: aceita nome sem diferenciar
            // maiúsculas, número com sinal e até lista separada por vírgula ("Debug,Error"→Fatal).
            if (!Enum.TryParse<LogEventLevel>(texto, ignoreCase: true, out var nivel))
            {
                throw new InvalidDataException("O valor de `@l` não é um `LogEventLevel` válido.");
            }

            return cache.Compartilhar(nivel.ToString());
        }

        private static bool LerEventId(ref Utf8JsonReader reader, out object? eventId)
        {
            reader.Read();
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    eventId = null;
                    return false;
                case JsonTokenType.String:
                    eventId = reader.GetString();
                    return true;
                case JsonTokenType.Number when reader.TryGetUInt32(out var numero):
                    // UInt32, não long: é o contrato explícito do leitor oficial. Negativo,
                    // fracionário ou acima de 4294967295 derruba a linha.
                    eventId = numero;
                    return true;
                default:
                    throw new InvalidDataException("O valor de `@i` não está num formato suportado.");
            }
        }

        private static List<(string? Texto, bool Convertivel)> LerRenderizacoes(ref Utf8JsonReader reader)
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                // Atenção: `@r: null` também é inválido — diferente dos campos de texto, aqui o
                // leitor oficial não trata null como ausente.
                throw new InvalidDataException("O valor de `@r` não é um array como esperado.");
            }

            var itens = new List<(string?, bool)>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        itens.Add((reader.GetString(), true));
                        break;
                    case JsonTokenType.Null:
                        itens.Add((null, true));
                        break;
                    case JsonTokenType.True:
                        itens.Add(("True", true));
                        break;
                    case JsonTokenType.False:
                        itens.Add(("False", true));
                        break;
                    case JsonTokenType.Number:
                        itens.Add((Convert.ToString(LerNumero(ref reader), CultureInfo.InvariantCulture), true));
                        break;
                    default:
                        // Objeto ou array só derruba a linha se chegar a ser PAREADO com um token
                        // formatado — o Zip do leitor oficial nem converte os elementos sobrando.
                        reader.Skip();
                        itens.Add((null, false));
                        break;
                }
            }

            return itens;
        }

        private static void AplicarRenderizacoes(
            List<LogEventProperty>? propriedades,
            PropertyToken[] tokensFormatados,
            List<(string? Texto, bool Convertivel)> renderizacoes)
        {
            var pares = Math.Min(tokensFormatados.Length, renderizacoes.Count);
            if (pares == 0) return;

            // O Zip do leitor oficial converte TODO elemento pareado, mesmo que nenhuma
            // propriedade do evento use aquele nome — por isso a validação vem antes.
            for (var j = 0; j < pares; j++)
            {
                if (!renderizacoes[j].Convertivel)
                {
                    throw new InvalidDataException("Um elemento de `@r` não pôde ser convertido para texto.");
                }
            }

            if (propriedades is null) return;

            for (var i = 0; i < propriedades.Count; i++)
            {
                // O RenderableScalarValue do leitor oficial só existe para valor escalar: objeto
                // e array recebem null de rendering.
                if (propriedades[i].Value is not ScalarValue escalar) continue;

                List<(string Formato, string? Texto)>? doNome = null;
                for (var j = 0; j < pares; j++)
                {
                    if (!string.Equals(tokensFormatados[j].PropertyName, propriedades[i].Name, StringComparison.Ordinal)) continue;
                    (doNome ??= new List<(string, string?)>(2)).Add((tokensFormatados[j].Format!, renderizacoes[j].Texto));
                }

                if (doNome is null) continue;
                propriedades[i] = new LogEventProperty(
                    propriedades[i].Name,
                    new ValorEscalarRenderizavel(escalar.Value, doNome));
            }
        }

        private static string LerNomeDePropriedade(ref Utf8JsonReader reader, scoped Span<char> rascunho, CacheDeTemplates cache)
        {
            if (TamanhoEmBytes(ref reader) > rascunho.Length)
            {
                return cache.Compartilhar(Desescapar(reader.GetString()!));
            }

            var lidos = reader.CopyString(rascunho);
            var nome = (ReadOnlySpan<char>)rascunho[..lidos];
            // "@@x" vira "@x" (tira UM arroba); "@foo" passa cru e vira propriedade comum.
            if (nome.Length >= 2 && nome[0] == '@' && nome[1] == '@') nome = nome[1..];
            // O formato permite nome vazio, o Serilog não representa: vira "(unnamed)".
            return nome.IsWhiteSpace() ? NomeInvalidoSubstituto : cache.Compartilhar(nome);
        }

        private static string Desescapar(string nome)
        {
            if (nome.StartsWith("@@", StringComparison.Ordinal)) nome = nome[1..];
            return string.IsNullOrWhiteSpace(nome) ? NomeInvalidoSubstituto : nome;
        }

        private static LogEventPropertyValue LerValor(ref Utf8JsonReader reader, scoped Span<char> rascunho, CacheDeTemplates cache)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    // Compartilhado: a premissa "valor é único por evento" caiu na medição —
                    // nos logs reais os valores têm ~10% de cardinalidade ("VAREJO", máquina,
                    // CNPJ repetem em toda linha). O EscalarDe tem teto para o que realmente
                    // é único (SpanId) não inchar o pool.
                    return cache.EscalarDe(reader.GetString());
                case JsonTokenType.Number:
                    return CacheDeTemplates.EscalarDeNumero(LerNumero(ref reader));
                case JsonTokenType.True:
                    return CacheDeTemplates.EscalarVerdadeiro;
                case JsonTokenType.False:
                    return CacheDeTemplates.EscalarFalso;
                case JsonTokenType.Null:
                    // null NÃO é o mesmo que ausente: o app distingue os dois na descoberta de colunas.
                    return CacheDeTemplates.EscalarNulo;
                case JsonTokenType.StartObject:
                    return LerObjeto(ref reader, rascunho, cache);
                case JsonTokenType.StartArray:
                    return LerSequencia(ref reader, rascunho, cache);
                default:
                    throw new InvalidDataException("Valor de propriedade em formato não suportado.");
            }
        }

        private static LogEventPropertyValue LerObjeto(ref Utf8JsonReader reader, scoped Span<char> rascunho, CacheDeTemplates cache)
        {
            List<LogEventProperty>? campos = null;
            string? tipo = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                // Só "$type" é reconhecido — "$typeTag" não é campo especial e continua propriedade.
                if (reader.ValueTextEquals("$type"u8))
                {
                    reader.Read();
                    tipo = TextoDeEscalar(ref reader);
                    continue;
                }

                // Dentro de objeto NÃO se aplica o unescape de "@@": o leitor oficial chama a
                // fábrica de propriedade direto, sem passar pelo ClefFields.Unescape.
                string nome;
                if (TamanhoEmBytes(ref reader) > rascunho.Length)
                {
                    var texto = reader.GetString()!;
                    nome = string.IsNullOrWhiteSpace(texto) ? NomeInvalidoSubstituto : cache.Compartilhar(texto);
                }
                else
                {
                    var lidos = reader.CopyString(rascunho);
                    var span = (ReadOnlySpan<char>)rascunho[..lidos];
                    nome = span.IsWhiteSpace() ? NomeInvalidoSubstituto : cache.Compartilhar(span);
                }

                reader.Read();
                Acrescentar(ref campos, nome, LerValor(ref reader, rascunho, cache));
            }

            return new StructureValue(campos ?? Enumerable.Empty<LogEventProperty>(), tipo);
        }

        private static LogEventPropertyValue LerSequencia(ref Utf8JsonReader reader, scoped Span<char> rascunho, CacheDeTemplates cache)
        {
            var itens = new List<LogEventPropertyValue>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                itens.Add(LerValor(ref reader, rascunho, cache));
            }

            return new SequenceValue(itens);
        }

        /// <summary>
        /// Regra numérica do leitor antigo, medida contra ele em 27 literais: long → BigInteger
        /// → double. <c>decimal</c> NUNCA aparece (o serializer fica no FloatParseHandling
        /// padrão), e trocá-lo mudaria o texto exibido — "1.0" sairia como "1.0" em vez de "1".
        /// </summary>
        private static object LerNumero(ref Utf8JsonReader reader)
        {
            if (reader.TryGetInt64(out var inteiro)) return inteiro;

            if (!reader.HasValueSequence)
            {
                var bruto = reader.ValueSpan;
                if (bruto.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0)
                {
                    // Integral que estoura o long: virar double mudaria "9223372036854775808"
                    // para "9.223372036854776E+18" na tela e na exportação.
                    return BigInteger.Parse(
                        Encoding.UTF8.GetString(bruto),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture);
                }
            }

            return reader.GetDouble();
        }

        private static string? TextoDeEscalar(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.True => "True",
            JsonTokenType.False => "False",
            JsonTokenType.Number => Convert.ToString(LerNumero(ref reader), CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("O valor de `$type` não pôde ser convertido para texto."),
        };

        private static string Escapar(string texto) => texto.Replace("{", "{{").Replace("}", "}}");

        private static int TamanhoEmBytes(ref Utf8JsonReader reader) =>
            reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;

        private static DateTimeOffset? LerTimestamp(
            ref Utf8JsonReader reader,
            string campo,
            bool obrigatorio)
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    if (!obrigatorio) return null;
                    throw new InvalidDataException($"A linha não inclui o campo obrigatório `{campo}`.");
                }

                throw new InvalidDataException($"O valor de `{campo}` não está num formato suportado.");
            }

            if (!reader.ValueIsEscaped && !reader.HasValueSequence && TentarTimestampCanonico(reader.ValueSpan, out var canonico))
            {
                return canonico;
            }

            // Fallback com CurrentCulture, como o leitor antigo (DateTimeOffset.TryParse sem
            // provider). Trocar por InvariantCulture aqui mudaria quais textos são aceitos.
            if (!DateTimeOffset.TryParse(reader.GetString(), out var valor))
            {
                throw new InvalidDataException($"O valor de `{campo}` não está num formato de data suportado.");
            }

            return valor;
        }

        /// <summary>
        /// Caminho rápido para o formato que 100% dos arquivos reais usam
        /// (<c>yyyy-MM-ddTHH:mm:ss.fffffff</c> seguido de <c>Z</c> ou de <c>±HH:mm</c>), lendo
        /// dígito a dígito nos bytes. Qualquer coisa fora disso volta para o TryParse.
        /// </summary>
        private static bool TentarTimestampCanonico(ReadOnlySpan<byte> bytes, out DateTimeOffset valor)
        {
            valor = default;
            if (bytes.Length < 19) return false;
            if (bytes[4] != (byte)'-' || bytes[7] != (byte)'-' || bytes[10] != (byte)'T'
                || bytes[13] != (byte)':' || bytes[16] != (byte)':')
            {
                return false;
            }

            if (!Numero(bytes.Slice(0, 4), out var ano)
                || !Numero(bytes.Slice(5, 2), out var mes)
                || !Numero(bytes.Slice(8, 2), out var dia)
                || !Numero(bytes.Slice(11, 2), out var hora)
                || !Numero(bytes.Slice(14, 2), out var minuto)
                || !Numero(bytes.Slice(17, 2), out var segundo))
            {
                return false;
            }

            var posicao = 19;
            long fracao = 0;
            if (posicao < bytes.Length && bytes[posicao] == (byte)'.')
            {
                posicao++;
                var digitos = 0;
                while (posicao < bytes.Length && bytes[posicao] is >= (byte)'0' and <= (byte)'9')
                {
                    fracao = (fracao * 10) + (bytes[posicao] - (byte)'0');
                    posicao++;
                    digitos++;
                }

                // Mais de 7 casas: o TryParse ARREDONDA e a leitura dígito a dígito truncaria.
                if (digitos is 0 or > 7) return false;
                for (var i = digitos; i < 7; i++) fracao *= 10;
            }

            TimeSpan deslocamento;
            if (posicao == bytes.Length - 1 && bytes[posicao] == (byte)'Z')
            {
                deslocamento = TimeSpan.Zero;
            }
            else if (bytes.Length - posicao == 6
                && bytes[posicao] is (byte)'+' or (byte)'-'
                && bytes[posicao + 3] == (byte)':'
                && Numero(bytes.Slice(posicao + 1, 2), out var horaFuso)
                && Numero(bytes.Slice(posicao + 4, 2), out var minutoFuso))
            {
                if (horaFuso > 14 || minutoFuso > 59 || (horaFuso == 14 && minutoFuso > 0)) return false;
                var total = new TimeSpan(horaFuso, minutoFuso, 0);
                deslocamento = bytes[posicao] == (byte)'-' ? -total : total;
            }
            else
            {
                // Sem zona o valor herda o fuso LOCAL da máquina; deixa para o TryParse resolver.
                return false;
            }

            if (ano is < 1 or > 9999 || mes is < 1 or > 12 || dia < 1 || hora > 23 || minuto > 59 || segundo > 59) return false;
            if (dia > DateTime.DaysInMonth(ano, mes)) return false;

            var instante = new DateTime(ano, mes, dia, hora, minuto, segundo, DateTimeKind.Unspecified).AddTicks(fracao);
            var emUtc = instante.Ticks - deslocamento.Ticks;
            if (emUtc < 0 || emUtc > DateTime.MaxValue.Ticks) return false;

            valor = new DateTimeOffset(instante, deslocamento);
            return true;
        }

        private static bool Numero(ReadOnlySpan<byte> bytes, out int valor)
        {
            valor = 0;
            foreach (var b in bytes)
            {
                if (b is < (byte)'0' or > (byte)'9') return false;
                valor = (valor * 10) + (b - (byte)'0');
            }

            return true;
        }

        /// <summary>
        /// Equivalente ao <c>RenderableScalarValue</c> do leitor oficial: quando o template pede
        /// um formato que veio pronto no <c>@r</c>, escreve o texto recebido em vez de formatar.
        /// </summary>
        private sealed class ValorEscalarRenderizavel : ScalarValue
        {
            private readonly List<(string Formato, string? Texto)> _renderizacoes;

            public ValorEscalarRenderizavel(object? valor, List<(string Formato, string? Texto)> renderizacoes)
                : base(valor)
            {
                _renderizacoes = renderizacoes;
            }

            public override void Render(TextWriter output, string? format = null, IFormatProvider? formatProvider = null)
            {
                if (format != null)
                {
                    // De trás para frente: o dicionário do leitor oficial deixa o ÚLTIMO formato
                    // repetido vencer.
                    for (var i = _renderizacoes.Count - 1; i >= 0; i--)
                    {
                        if (!string.Equals(_renderizacoes[i].Formato, format, StringComparison.Ordinal)) continue;
                        output.Write(_renderizacoes[i].Texto);
                        return;
                    }
                }

                base.Render(output, format, formatProvider);
            }
        }

        /// <summary>
        /// Visão Ordinal das propriedades para o <see cref="MessageTemplate.Render"/>. O
        /// dicionário do ClefEvent é OrdinalIgnoreCase (contrato do app), mas o Serilog resolve
        /// os tokens do template com comparação Ordinal — renderizar pelo dicionário do evento
        /// resolveria "{User}" com uma chave "user" e mudaria a mensagem.
        /// </summary>
        private sealed class PropriedadesOrdinais : IReadOnlyDictionary<string, LogEventPropertyValue>
        {
            private readonly List<LogEventProperty>? _itens;

            public PropriedadesOrdinais(List<LogEventProperty>? itens) => _itens = itens;

            public int Count => _itens?.Count ?? 0;

            public IEnumerable<string> Keys
            {
                get
                {
                    if (_itens is null) yield break;
                    foreach (var item in _itens) yield return item.Name;
                }
            }

            public IEnumerable<LogEventPropertyValue> Values
            {
                get
                {
                    if (_itens is null) yield break;
                    foreach (var item in _itens) yield return item.Value;
                }
            }

            public LogEventPropertyValue this[string key] =>
                TryGetValue(key, out var valor) ? valor : throw new KeyNotFoundException(key);

            public bool ContainsKey(string key) => TryGetValue(key, out _);

            public bool TryGetValue(string key, out LogEventPropertyValue value)
            {
                if (_itens is not null)
                {
                    // De trás para frente: chave repetida faz o último valor vencer, como no
                    // dicionário que o Serilog monta.
                    for (var i = _itens.Count - 1; i >= 0; i--)
                    {
                        if (!string.Equals(_itens[i].Name, key, StringComparison.Ordinal)) continue;
                        value = _itens[i].Value;
                        return true;
                    }
                }

                value = null!;
                return false;
            }

            public IEnumerator<KeyValuePair<string, LogEventPropertyValue>> GetEnumerator()
            {
                if (_itens is null) yield break;
                foreach (var item in _itens)
                {
                    yield return new KeyValuePair<string, LogEventPropertyValue>(item.Name, item.Value);
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
