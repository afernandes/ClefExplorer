using System.Globalization;
using ClefExplorer.Models;
using Serilog.Events;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Descobre eventos relacionados por identificadores de rastreamento sem alterar os
    /// filtros da consulta atual. A relação é direta: um evento entra quando compartilha
    /// identificador lógico e valor com o evento de origem.
    /// </summary>
    public sealed class NavegacaoCorrelacao
    {
        private const string CampoTraceId = "TraceId";
        private const string CampoSpanId = "SpanId";
        private const string CampoRequestId = "RequestId";
        private const string CampoCorrelationId = "CorrelationId";

        private static readonly ComparadorIdentificador Comparador = new();
        private readonly ConfiguracaoCorrelacao _configuracao;

        public NavegacaoCorrelacao()
            : this(new ConfiguracaoCorrelacao())
        {
        }

        public NavegacaoCorrelacao(SettingsService settingsService)
            : this(settingsService?.Settings.Correlacao
                ?? throw new ArgumentNullException(nameof(settingsService)))
        {
        }

        public NavegacaoCorrelacao(ConfiguracaoCorrelacao configuracao)
        {
            _configuracao = configuracao
                ?? throw new ArgumentNullException(nameof(configuracao));
        }

        /// <summary>Indica se o evento oferece ao menos uma chave navegável.</summary>
        public bool PodeNavegar(ClefEvent? evento)
        {
            if (evento is null) return false;
            var identificadores = new List<IdentificadorCorrelacao>(4);
            ExtrairPara(evento, identificadores, CapturarCamposCorrelacao());
            return identificadores.Count > 0;
        }

        /// <summary>
        /// Indica se a propriedade é um dos aliases configurados para o identificador
        /// lógico <c>CorrelationId</c>.
        /// </summary>
        public bool EhCampoCorrelacao(string nome) =>
            Canonicalizar(nome, CapturarCamposCorrelacao()) is { EhCorrelacao: true };

        /// <summary>
        /// Localiza, em todos os eventos carregados, os que compartilham ao menos um dos
        /// identificadores da origem. Tipo lógico e valor precisam coincidir; um RequestId
        /// não é confundido com um TraceId de texto igual.
        /// </summary>
        public ResultadoNavegacaoCorrelacao Localizar(
            ClefEvent origem,
            IEnumerable<ClefEvent> eventos,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(origem);
            ArgumentNullException.ThrowIfNull(eventos);

            var camposCorrelacao = CapturarCamposCorrelacao();
            var identificadores = ExtrairIdentificadores(origem, camposCorrelacao);
            if (identificadores.Count == 0)
            {
                return new ResultadoNavegacaoCorrelacao(
                    origem,
                    identificadores,
                    Array.Empty<EventoCorrelacionado>());
            }

            var procurados = new HashSet<IdentificadorCorrelacao>(identificadores, Comparador);
            var encontrados = new List<(EventoCorrelacionado Evento, int Ordem)>();
            // Reutilizado durante a varredura: num conjunto com 1 milhão de eventos, criar
            // uma lista vazia para cada linha geraria dezenas de MB de lixo só para concluir
            // que a maioria não tem nenhuma das quatro chaves.
            var extraidos = new List<IdentificadorCorrelacao>(8);
            var origemEncontrada = false;
            var ordem = 0;

            foreach (var evento in eventos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                extraidos.Clear();
                ExtrairPara(evento, extraidos, camposCorrelacao);
                List<IdentificadorCorrelacao>? correspondencias = null;
                foreach (var identificador in extraidos)
                {
                    if (!procurados.Contains(identificador)
                        || correspondencias?.Contains(identificador, Comparador) == true)
                    {
                        continue;
                    }

                    (correspondencias ??= new List<IdentificadorCorrelacao>(2)).Add(identificador);
                }

                if (correspondencias is not null)
                {
                    encontrados.Add((new EventoCorrelacionado(evento, correspondencias.ToArray()), ordem));
                    if (ReferenceEquals(evento, origem)) origemEncontrada = true;
                }

                ordem++;
            }

            // O chamador normalmente usa o snapshot do Store, mas manter a origem garante
            // um resultado coerente também para integrações que passam uma amostra parcial.
            if (!origemEncontrada)
            {
                encontrados.Add((new EventoCorrelacionado(origem, identificadores), ordem));
            }

            var sequencia = encontrados
                .OrderBy(item => item.Evento.Evento.Timestamp is null)
                .ThenBy(item => item.Evento.Evento.Timestamp)
                .ThenBy(item => item.Ordem)
                .Select(item => item.Evento)
                .ToArray();

            return new ResultadoNavegacaoCorrelacao(origem, identificadores, sequencia);
        }

        /// <summary>
        /// Extrai as quatro chaves aceitas. Trace/span reservados vêm do modelo; as
        /// propriedades também são percorridas dentro de estruturas, sequências e mapas.
        /// </summary>
        public IReadOnlyList<IdentificadorCorrelacao> ExtrairIdentificadores(ClefEvent evento)
        {
            ArgumentNullException.ThrowIfNull(evento);
            return ExtrairIdentificadores(evento, CapturarCamposCorrelacao());
        }

        private static IReadOnlyList<IdentificadorCorrelacao> ExtrairIdentificadores(
            ClefEvent evento,
            IReadOnlyList<string> camposCorrelacao)
        {
            var resultado = new List<IdentificadorCorrelacao>(4);
            ExtrairPara(evento, resultado, camposCorrelacao);
            return resultado.Distinct(Comparador).ToArray();
        }

        private static void ExtrairPara(
            ClefEvent evento,
            List<IdentificadorCorrelacao> resultado,
            IReadOnlyList<string> camposCorrelacao)
        {
            Adicionar(resultado, CampoTraceId, evento.TraceId, separarValores: false);
            Adicionar(resultado, CampoSpanId, evento.SpanId, separarValores: false);

            if (evento.Properties is not null)
            {
                foreach (var propriedade in evento.Properties)
                {
                    ExtrairDaPropriedade(
                        propriedade.Key,
                        propriedade.Value,
                        resultado,
                        camposCorrelacao);
                }
            }
        }

        private static void ExtrairDaPropriedade(
            string nome,
            LogEventPropertyValue valor,
            List<IdentificadorCorrelacao> destino,
            IReadOnlyList<string> camposCorrelacao)
        {
            var campo = Canonicalizar(nome, camposCorrelacao);
            if (campo is { } reconhecido)
            {
                ExtrairValoresDoCampo(reconhecido, valor, destino);
            }

            // Mesmo quando o contêiner tem um nome reconhecido, seus filhos podem trazer
            // outros identificadores e também precisam ser visitados.
            switch (valor)
            {
                case StructureValue estrutura:
                    foreach (var propriedade in estrutura.Properties)
                    {
                        ExtrairDaPropriedade(
                            propriedade.Name,
                            propriedade.Value,
                            destino,
                            camposCorrelacao);
                    }
                    break;

                case SequenceValue sequencia:
                    foreach (var item in sequencia.Elements)
                    {
                        ExtrairDeValorAninhado(item, destino, camposCorrelacao);
                    }
                    break;

                case DictionaryValue dicionario:
                    foreach (var par in dicionario.Elements)
                    {
                        if (par.Key is ScalarValue { Value: string chave })
                        {
                            ExtrairDaPropriedade(chave, par.Value, destino, camposCorrelacao);
                        }
                        else
                        {
                            ExtrairDeValorAninhado(par.Value, destino, camposCorrelacao);
                        }
                    }
                    break;
            }
        }

        private static void ExtrairDeValorAninhado(
            LogEventPropertyValue valor,
            List<IdentificadorCorrelacao> destino,
            IReadOnlyList<string> camposCorrelacao)
        {
            switch (valor)
            {
                case StructureValue estrutura:
                    foreach (var propriedade in estrutura.Properties)
                    {
                        ExtrairDaPropriedade(
                            propriedade.Name,
                            propriedade.Value,
                            destino,
                            camposCorrelacao);
                    }
                    break;

                case SequenceValue sequencia:
                    foreach (var item in sequencia.Elements)
                    {
                        ExtrairDeValorAninhado(item, destino, camposCorrelacao);
                    }
                    break;

                case DictionaryValue dicionario:
                    foreach (var par in dicionario.Elements)
                    {
                        if (par.Key is ScalarValue { Value: string chave })
                        {
                            ExtrairDaPropriedade(chave, par.Value, destino, camposCorrelacao);
                        }
                        else
                        {
                            ExtrairDeValorAninhado(par.Value, destino, camposCorrelacao);
                        }
                    }
                    break;
            }
        }

        private static void ExtrairValoresDoCampo(
            CampoReconhecido campo,
            LogEventPropertyValue valor,
            List<IdentificadorCorrelacao> destino)
        {
            switch (valor)
            {
                case ScalarValue escalar:
                    Adicionar(
                        destino,
                        campo.Nome,
                        Formatar(escalar.Value),
                        separarValores: campo.SepararValores);
                    break;

                case SequenceValue sequencia:
                    foreach (var item in sequencia.Elements)
                    {
                        if (item is ScalarValue escalarDoItem)
                        {
                            Adicionar(
                                destino,
                                campo.Nome,
                                Formatar(escalarDoItem.Value),
                                separarValores: campo.SepararValores);
                        }
                    }
                    break;
            }
        }

        private static CampoReconhecido? Canonicalizar(
            string nome,
            IReadOnlyList<string> camposCorrelacao)
        {
            if (nome.Equals(CampoTraceId, StringComparison.OrdinalIgnoreCase))
                return new CampoReconhecido(CampoTraceId, EhCorrelacao: false, SepararValores: false);
            if (nome.Equals(CampoSpanId, StringComparison.OrdinalIgnoreCase))
                return new CampoReconhecido(CampoSpanId, EhCorrelacao: false, SepararValores: false);
            if (nome.Equals(CampoRequestId, StringComparison.OrdinalIgnoreCase))
                return new CampoReconhecido(CampoRequestId, EhCorrelacao: false, SepararValores: false);

            foreach (var alias in camposCorrelacao)
            {
                if (nome.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return new CampoReconhecido(
                        CampoCorrelationId,
                        EhCorrelacao: true,
                        SepararValores: nome.Equals(
                            "X-Correlation-Id",
                            StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private string[] CapturarCamposCorrelacao() => (_configuracao.Campos ?? new())
            .Where(campo => !string.IsNullOrWhiteSpace(campo))
            .Select(campo => campo.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private static string? Formatar(object? valor) => valor switch
        {
            null => null,
            string texto => texto,
            IFormattable formatavel => formatavel.ToString(null, CultureInfo.InvariantCulture),
            _ => valor.ToString(),
        };

        private static void Adicionar(
            List<IdentificadorCorrelacao> destino,
            string campo,
            string? valor,
            bool separarValores)
        {
            if (string.IsNullOrWhiteSpace(valor)) return;

            // X-Correlation-Id pode chegar como cabeçalho HTTP concatenado. Os demais
            // identificadores permanecem opacos e nunca são divididos por inferência.
            var partes = separarValores
                ? valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[] { valor.Trim() };

            foreach (var parte in partes)
            {
                if (!string.IsNullOrWhiteSpace(parte))
                {
                    destino.Add(new IdentificadorCorrelacao(campo, parte));
                }
            }
        }

        private readonly record struct CampoReconhecido(
            string Nome,
            bool EhCorrelacao,
            bool SepararValores);

        private sealed class ComparadorIdentificador : IEqualityComparer<IdentificadorCorrelacao>
        {
            public bool Equals(IdentificadorCorrelacao? x, IdentificadorCorrelacao? y) =>
                ReferenceEquals(x, y)
                || x is not null && y is not null
                && StringComparer.OrdinalIgnoreCase.Equals(x.Campo, y.Campo)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Valor, y.Valor);

            public int GetHashCode(IdentificadorCorrelacao obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Campo),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Valor));
        }
    }
}
