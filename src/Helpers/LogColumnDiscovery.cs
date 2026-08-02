using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClefExplorer.Models;
using Omni.Blazor.Models;
using Serilog.Events;

namespace ClefExplorer.Helpers
{
    /// <summary>
    /// Natureza dos valores de uma propriedade. Define como a coluna ORDENA e filtra:
    /// entregar tudo como texto faria "591" vir antes de "78", que é comparação de
    /// string, não de número.
    /// </summary>
    public enum ColumnValueKind
    {
        Text,
        Number,
        Date,
        Boolean,
    }

    /// <summary>Por qual regra a propriedade virou coluna — o que também separa as seções do menu.</summary>
    public enum ColumnSource
    {
        /// <summary>Propriedade recorrente do log (enricher, contexto): passou no corte de frequência.</summary>
        Property,

        /// <summary>
        /// Campo de um message template (o <c>{ProviderKey}</c> de <c>@mt</c>). Entra mesmo
        /// sendo raro: um log com centenas de templates dilui cada campo bem abaixo do corte,
        /// e são justamente esses campos que dão sentido a agrupar e filtrar pela mensagem.
        /// </summary>
        TemplateField,
    }

    /// <summary>Uma coluna derivada de uma propriedade estruturada do log.</summary>
    /// <param name="Key">Nome da propriedade no evento (ex.: <c>SourceContext</c>).</param>
    /// <param name="Title">Rótulo exibido no cabeçalho.</param>
    /// <param name="Frequency">Fração dos eventos amostrados que possuem a propriedade (0..1).</param>
    /// <param name="Kind">Tipo inferido dos valores amostrados.</param>
    /// <param name="Source">
    /// Regra que admitiu a coluna. Um campo de template frequente entra como
    /// <see cref="ColumnSource.Property"/> — a origem diz por onde ele passou, não onde apareceu.
    /// </param>
    public sealed record DiscoveredColumn(
        string Key,
        string Title,
        double Frequency,
        ColumnValueKind Kind = ColumnValueKind.Text,
        ColumnSource Source = ColumnSource.Property);

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
        /// Teto próprio para os campos de message template, que entram sem passar pela
        /// frequência. Sem ele, um log com centenas de templates distintos ofereceria
        /// centenas de colunas.
        /// </summary>
        public const int DefaultMaxTemplateColumns = 25;

        /// <summary>
        /// Tokens de um message template do Serilog: <c>{Nome}</c>, com os prefixos de
        /// destructuring (<c>{@Nome}</c>/<c>{$Nome}</c>), alinhamento (<c>{Nome,-10}</c>) e
        /// formato (<c>{Nome:000}</c>). As alternativas <c>{{</c> e <c>}}</c> vêm primeiro de
        /// propósito: consomem as chaves escapadas antes que elas sejam lidas como token.
        /// Posicionais (<c>{0}</c>) ficam de fora — não rendem coluna útil.
        /// </summary>
        private static readonly Regex TokenDeTemplate = new(
            @"\{\{|\}\}|\{[@$]?(?<nome>[A-Za-z_]\w*)(?:,-?\d+)?(?::[^}]*)?\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            double minFrequency = DefaultMinFrequency,
            int maxTemplateColumns = DefaultMaxTemplateColumns)
        {
            ArgumentNullException.ThrowIfNull(events);

            var contagem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tipos = new Dictionary<string, ColumnValueKind?>(StringComparer.OrdinalIgnoreCase);
            var camposDeTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var templatesLidos = new HashSet<string>(StringComparer.Ordinal);
            var amostrados = 0;

            foreach (var ev in events.Take(sampleSize))
            {
                amostrados++;

                // Um punhado de templates se repete por milhares de eventos: vale guardar
                // quais já foram lidos em vez de aplicar a regex em cada linha.
                if (ev.MessageTemplate is { Length: > 0 } template && templatesLidos.Add(template))
                {
                    foreach (var campo in CamposDoTemplate(template)) camposDeTemplate.Add(campo);
                }

                if (ev.Properties is null) continue;

                foreach (var (chave, valor) in ev.Properties)
                {
                    if (string.IsNullOrWhiteSpace(chave) || Excluded.Contains(chave)) continue;
                    contagem[chave] = contagem.GetValueOrDefault(chave) + 1;
                    AcumularTipo(tipos, chave, valor);
                }
            }

            if (amostrados == 0) return Array.Empty<DiscoveredColumn>();

            // Mais frequentes primeiro; nome como desempate, para a ordem ser estável entre
            // carregamentos (senão as colunas dançariam a cada abertura).
            var candidatas = contagem
                .Select(p => new
                {
                    p.Key,
                    Frequencia = p.Value / (double)amostrados,
                    Tipo = tipos.GetValueOrDefault(p.Key) ?? ColumnValueKind.Text,
                })
                .OrderByDescending(c => c.Frequencia)
                .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var colunas = candidatas
                .Where(c => c.Frequencia >= minFrequency)
                .Take(maxColumns)
                .Select(c => new DiscoveredColumn(c.Key, Humanize(c.Key), c.Frequencia, c.Tipo))
                .ToList();

            // Os campos de template entram DEPOIS e só se ainda não estiverem na lista: um
            // campo frequente já passou pelo corte acima, e duplicá-lo daria duas colunas
            // idênticas em seções diferentes do menu.
            var jaIncluidas = new HashSet<string>(colunas.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);

            colunas.AddRange(candidatas
                .Where(c => camposDeTemplate.Contains(c.Key) && !jaIncluidas.Contains(c.Key))
                .Take(maxTemplateColumns)
                .Select(c => new DiscoveredColumn(
                    c.Key, Humanize(c.Key), c.Frequencia, c.Tipo, ColumnSource.TemplateField)));

            return colunas;
        }

        /// <summary>
        /// Nomes das propriedades citadas num message template, sem os prefixos de
        /// destructuring — <c>{@Pedido}</c> é gravado como <c>Pedido</c> no CLEF.
        /// </summary>
        public static IEnumerable<string> CamposDoTemplate(string? template)
        {
            if (string.IsNullOrEmpty(template)) yield break;

            foreach (Match m in TokenDeTemplate.Matches(template))
            {
                var nome = m.Groups["nome"];
                if (nome.Success) yield return nome.Value;
            }
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
        /// Vai somando o tipo visto para a chave. Basta UM valor destoante para a coluna
        /// virar texto: a ordenação compara os valores entre si, e misturar número com
        /// string na mesma coluna quebraria a comparação.
        /// <c>null</c> no dicionário significa "ainda não vi valor nenhum".
        /// </summary>
        private static void AcumularTipo(
            Dictionary<string, ColumnValueKind?> tipos, string chave, LogEventPropertyValue? valor)
        {
            var visto = ClassificarValor(valor);
            if (visto is null) return;   // ausente/nulo não diz nada sobre o tipo

            if (!tipos.TryGetValue(chave, out var atual) || atual is null)
            {
                tipos[chave] = visto;
                return;
            }

            if (atual != visto) tipos[chave] = ColumnValueKind.Text;
        }

        private static ColumnValueKind? ClassificarValor(LogEventPropertyValue? valor)
        {
            if (valor is not ScalarValue { Value: { } bruto }) return null;

            return bruto switch
            {
                byte or sbyte or short or ushort or int or uint
                    or long or ulong or float or double or decimal => ColumnValueKind.Number,
                DateTime or DateTimeOffset => ColumnValueKind.Date,
                bool => ColumnValueKind.Boolean,
                _ => ColumnValueKind.Text,
            };
        }

        /// <summary>
        /// Valor usado para ORDENAR, agrupar e filtrar — em oposição ao texto exibido.
        ///
        /// <para>Devolve sempre o MESMO tipo para a coluna inteira: o grid ordena por
        /// <c>object?</c> com o comparador padrão, que exige valores mutuamente comparáveis.
        /// Um número que não converte (ou ausente) vira <c>null</c>, e não texto, para não
        /// misturar tipos e derrubar a comparação.</para>
        /// </summary>
        public static object? GetSortValue(ClefEvent ev, string key, ColumnValueKind kind)
        {
            if (ev.Properties is null || !ev.Properties.TryGetValue(key, out var valor)) return null;

            var bruto = (valor as ScalarValue)?.Value;
            if (bruto is null) return kind == ColumnValueKind.Text ? TextoOuNulo(ev, key) : null;

            return kind switch
            {
                // O próprio ClefEvent pode trazer o número já como int/long/double; e um log
                // que grave o valor como string ("123") ainda ordena certo por causa do parse.
                ColumnValueKind.Number => bruto is string s
                    ? (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                    : Convert.ToDouble(bruto, CultureInfo.InvariantCulture),

                ColumnValueKind.Date => bruto switch
                {
                    DateTimeOffset dto => dto,
                    DateTime dt => new DateTimeOffset(dt),
                    string s2 when DateTimeOffset.TryParse(s2, CultureInfo.InvariantCulture, out var p) => p,
                    _ => null,
                },

                ColumnValueKind.Boolean => bruto as bool?,

                _ => TextoOuNulo(ev, key),
            };
        }

        /// <summary>
        /// Texto da propriedade, ou <c>null</c> quando não há texto nenhum.
        ///
        /// <para>"Sem valor" chega de duas formas: a propriedade AUSENTE do evento e a
        /// propriedade presente valendo <c>null</c> — num log real de 3.660 linhas, o
        /// <c>CorrelationId</c> aparecia 3.216 vezes ausente e 46 vezes nulo. Devolver
        /// <c>null</c> num caso e <c>""</c> no outro dava dois grupos distintos na tabela,
        /// ambos rotulados "(vazio)". A ordenação tinha o mesmo problema.</para>
        /// </summary>
        private static object? TextoOuNulo(ClefEvent ev, string key)
        {
            var texto = FormatValue(ev, key);
            return texto.Length == 0 ? null : texto;
        }

        /// <summary>Filtro adequado ao tipo inferido — número pede operadores de número.</summary>
        public static ColumnFilterType FilterTypeFor(ColumnValueKind kind) => kind switch
        {
            ColumnValueKind.Number => ColumnFilterType.Number,
            ColumnValueKind.Date => ColumnFilterType.Date,
            ColumnValueKind.Boolean => ColumnFilterType.Boolean,
            _ => ColumnFilterType.Text,
        };

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
