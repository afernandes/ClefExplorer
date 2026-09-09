using System.Globalization;
using Serilog.Events;
using Serilog.Parsing;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Um <c>@mt</c> já parseado. <see cref="Constante"/> marca o template sem nenhum
    /// <see cref="PropertyToken"/>: a mensagem dele nunca muda, então renderizar uma vez basta
    /// Eventos com mensagem pronta (<c>@m</c>) dispensam esta compilação.
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

}
