using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
        private const int LimiteTemplates = 4096;
        private const int LimiteTextos = 65536;
        private const int LimiteComprimento = 4096;
        private readonly object _admissao = new();
        private int _totalTemplates;
        private int _totalTextos;
        public int QuantidadeTemplates => _templates.Count;
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

        internal TemplateCompilado Obter(string texto)
        {
            if (_templates.TryGetValue(texto, out var existente)) return existente;
            if (texto.Length > LimiteComprimento || Volatile.Read(ref _totalTemplates) >= LimiteTemplates)
                return new(texto, Parser.Parse(texto));
            lock (_admissao)
            {
                if (_templates.TryGetValue(texto, out existente)) return existente;
                var compilado = new TemplateCompilado(texto, Parser.Parse(texto));
                if (_totalTemplates < LimiteTemplates && _templates.TryAdd(texto, compilado))
                    Interlocked.Increment(ref _totalTemplates);
                return compilado;
            }
        }

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
            if (_textos.TryGetValue(texto, out var existente)) return existente;
            if (texto.Length > LimiteComprimento || Volatile.Read(ref _totalTextos) >= LimiteTextos) return texto;
            lock (_admissao)
            {
                if (_textos.TryGetValue(texto, out existente)) return existente;
                if (_totalTextos >= LimiteTextos) return texto;
                var canonico = _pool?.Compartilhar(texto) ?? texto;
                if (_textos.TryAdd(canonico, canonico)) Interlocked.Increment(ref _totalTextos);
                return canonico;
            }
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

}
