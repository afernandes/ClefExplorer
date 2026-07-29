using System;
using System.Collections.Generic;
using System.Linq;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Helpers
{
    /// <summary>Uma coluna derivada de uma propriedade estruturada do log.</summary>
    /// <param name="Key">Nome da propriedade no evento (ex.: <c>SourceContext</c>).</param>
    /// <param name="Title">Rótulo exibido no cabeçalho.</param>
    /// <param name="Frequency">Fração dos eventos amostrados que possuem a propriedade (0..1).</param>
    public sealed record DiscoveredColumn(string Key, string Title, double Frequency);

    /// <summary>
    /// Deriva colunas do CONTEÚDO dos logs.
    ///
    /// <para>Logs CLEF carregam propriedades estruturadas (<c>SourceContext</c>,
    /// <c>RequestId</c>, <c>MachineName</c>…) que variam conforme a aplicação que os
    /// gerou. Em vez de fixar uma lista, amostramos os eventos carregados e oferecemos como
    /// coluna as propriedades que aparecem com frequência — o usuário liga/desliga cada uma
    /// pelo menu de colunas.</para>
    /// </summary>
    public static class LogColumnDiscovery
    {
        /// <summary>Quantos eventos são inspecionados. Amostra basta e evita varrer milhões de linhas.</summary>
        public const int DefaultSampleSize = 2_000;

        /// <summary>Máximo de colunas sugeridas, para o menu não virar uma lista interminável.</summary>
        public const int DefaultMaxColumns = 15;

        /// <summary>Fração mínima de eventos com a propriedade para ela virar coluna.</summary>
        public const double DefaultMinFrequency = 0.05;

        /// <summary>
        /// Propriedades que já têm coluna própria ou que não agregam nada numa tabela.
        /// Comparação sem diferenciar maiúsculas, como o resto do tratamento de propriedades.
        /// </summary>
        private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        {
            // Já exibidas em colunas fixas.
            "SourceFile", "Level", "Message", "MessageTemplate", "Timestamp", "Exception",
            // Ruído do Serilog: o template renderizado já está na coluna Mensagem.
            "SourceContextTemplate",
        };

        public static IReadOnlyList<DiscoveredColumn> Discover(
            IEnumerable<ClefEvent> events,
            int sampleSize = DefaultSampleSize,
            int maxColumns = DefaultMaxColumns,
            double minFrequency = DefaultMinFrequency)
        {
            ArgumentNullException.ThrowIfNull(events);

            var contagem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var amostrados = 0;

            foreach (var ev in events.Take(sampleSize))
            {
                amostrados++;
                if (ev.Properties is null) continue;

                foreach (var chave in ev.Properties.Keys)
                {
                    if (string.IsNullOrWhiteSpace(chave) || Excluded.Contains(chave)) continue;
                    contagem[chave] = contagem.GetValueOrDefault(chave) + 1;
                }
            }

            if (amostrados == 0) return Array.Empty<DiscoveredColumn>();

            return contagem
                .Select(p => new DiscoveredColumn(p.Key, Humanize(p.Key), p.Value / (double)amostrados))
                .Where(c => c.Frequency >= minFrequency)
                // Mais frequentes primeiro; nome como desempate, para a ordem ser estável
                // entre carregamentos (senão as colunas dançariam a cada abertura).
                .OrderByDescending(c => c.Frequency)
                .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Take(maxColumns)
                .ToList();
        }

        /// <summary>
        /// "RequestId" → "Request Id". Nomes de propriedade vêm em PascalCase do código que
        /// emitiu o log; separá-los deixa o cabeçalho legível.
        /// </summary>
        public static string Humanize(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;

            var sb = new System.Text.StringBuilder(key.Length + 4);
            for (var i = 0; i < key.Length; i++)
            {
                var c = key[i];
                var anterior = i > 0 ? key[i - 1] : '\0';
                var proximo = i + 1 < key.Length ? key[i + 1] : '\0';

                // Espaço antes de uma maiúscula que inicia palavra — inclusive no fim de uma
                // sigla ("HTTPRequest" → "HTTP Request").
                var iniciaPalavra = char.IsUpper(c)
                    && i > 0
                    && (!char.IsUpper(anterior) || (char.IsUpper(anterior) && char.IsLower(proximo)));

                if (iniciaPalavra) sb.Append(' ');
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Texto da célula para uma propriedade estruturada.
        /// <see cref="ScalarValue.ToString()"/> envolve strings em aspas — indesejado numa
        /// tabela, onde a coluna já dá o contexto.
        /// </summary>
        public static string FormatValue(ClefEvent ev, string key)
        {
            if (ev.Properties is null || !ev.Properties.TryGetValue(key, out var valor)) return string.Empty;

            return valor switch
            {
                null => string.Empty,
                ScalarValue s => s.Value?.ToString() ?? string.Empty,
                _ => valor.ToString(),
            };
        }
    }
}
