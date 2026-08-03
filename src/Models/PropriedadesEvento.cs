using System.Collections;
using Serilog.Events;
using Serilog.Parsing;

namespace ClefExplorer.Models
{
    /// <summary>
    /// As propriedades de um evento, em dois arrays paralelos com o tamanho exato.
    ///
    /// <para>O <see cref="Dictionary{TKey,TValue}"/> que ficava aqui custava caro no
    /// agregado: buckets + entries + overhead de objeto por evento, multiplicados por
    /// centenas de milhares de eventos — para um conjunto que tem ~18 pares, nunca muda
    /// depois de construído e quase sempre é lido por enumeração, não por chave. O lookup
    /// linear com <see cref="ReferenceEquals(object,object)"/> primeiro (as chaves vêm do
    /// pool da sessão, então a MESMA instância aparece em todo evento) empata com o hash
    /// para n desse tamanho.</para>
    /// </summary>
    public sealed class PropriedadesEvento : IReadOnlyDictionary<string, LogEventPropertyValue>
    {
        public static PropriedadesEvento Vazio { get; } = new(null);

        private readonly string[] _chaves;
        private readonly LogEventPropertyValue[] _valores;

        /// <summary>
        /// Constrói a partir da lista do parser, com a MESMA semântica do dicionário que
        /// substitui: chave repetida (OrdinalIgnoreCase) faz o último valor vencer.
        /// </summary>
        public PropriedadesEvento(IReadOnlyList<LogEventProperty>? itens)
        {
            if (itens is null || itens.Count == 0)
            {
                _chaves = Array.Empty<string>();
                _valores = Array.Empty<LogEventPropertyValue>();
                return;
            }

            var chaves = new string[itens.Count];
            var valores = new LogEventPropertyValue[itens.Count];
            var quantidade = 0;

            for (var i = 0; i < itens.Count; i++)
            {
                var nome = itens[i].Name;

                // Um único loop com o teste combinado: duplicata é raríssima, e rodar o
                // fallback OrdinalIgnoreCase completo por inserção (como o lookup de
                // leitura faz) custava caro multiplicado por 18 propriedades por evento.
                var indice = -1;
                for (var j = 0; j < quantidade; j++)
                {
                    if (ReferenceEquals(chaves[j], nome)
                        || string.Equals(chaves[j], nome, StringComparison.OrdinalIgnoreCase))
                    {
                        indice = j;
                        break;
                    }
                }

                if (indice >= 0)
                {
                    valores[indice] = itens[i].Value;
                    continue;
                }

                chaves[quantidade] = nome;
                valores[quantidade] = itens[i].Value;
                quantidade++;
            }

            if (quantidade == itens.Count)
            {
                _chaves = chaves;
                _valores = valores;
            }
            else
            {
                // Havia duplicata: os arrays exatos evitam carregar as sobras para sempre.
                _chaves = chaves.AsSpan(0, quantidade).ToArray();
                _valores = valores.AsSpan(0, quantidade).ToArray();
            }
        }

        /// <summary>Acesso direto para os caminhos quentes (filtro), sem enumerator.</summary>
        internal LogEventPropertyValue[] ValoresInternos => _valores;

        internal string[] ChavesInternas => _chaves;

        public int Count => _chaves.Length;

        public IEnumerable<string> Keys => _chaves;

        public IEnumerable<LogEventPropertyValue> Values => _valores;

        public LogEventPropertyValue this[string key] =>
            TryGetValue(key, out var valor) ? valor : throw new KeyNotFoundException(key);

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out LogEventPropertyValue value)
        {
            var indice = IndiceDe(_chaves, _chaves.Length, key);
            if (indice >= 0)
            {
                value = _valores[indice];
                return true;
            }

            value = null!;
            return false;
        }

        private static int IndiceDe(string[] chaves, int quantidade, string chave)
        {
            // Referência primeiro: com o pool, "SourceContext" é UMA instância no processo
            // inteiro e a comparação é um ponteiro. O fallback mantém o contrato do
            // dicionário antigo (OrdinalIgnoreCase) para chaves vindas de fora do pool.
            for (var i = 0; i < quantidade; i++)
            {
                if (ReferenceEquals(chaves[i], chave)) return i;
            }

            for (var i = 0; i < quantidade; i++)
            {
                if (string.Equals(chaves[i], chave, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }

        public IEnumerator<KeyValuePair<string, LogEventPropertyValue>> GetEnumerator()
        {
            for (var i = 0; i < _chaves.Length; i++)
            {
                yield return new KeyValuePair<string, LogEventPropertyValue>(_chaves[i], _valores[i]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
