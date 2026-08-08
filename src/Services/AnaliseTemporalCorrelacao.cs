using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Classifica durações, intervalos e relações pai/filho sem atribuir semântica de
    /// tracing a eventos que carregam apenas um timestamp pontual.
    /// </summary>
    public sealed class AnaliseTemporalCorrelacao
    {
        private readonly LeituraMetadadosObservabilidade _leituraMetadados;

        public AnaliseTemporalCorrelacao()
            : this(new LeituraMetadadosObservabilidade())
        {
        }

        public AnaliseTemporalCorrelacao(LeituraMetadadosObservabilidade leituraMetadados)
        {
            _leituraMetadados = leituraMetadados;
        }

        public ResultadoAnaliseTemporalCorrelacao Analisar(
            ResultadoNavegacaoCorrelacao resultado,
            ConfiguracaoObservabilidade? configuracao = null)
        {
            ArgumentNullException.ThrowIfNull(resultado);

            var eventos = resultado.Eventos
                .Select((item, indice) => new EventoOrdenado(
                    item,
                    indice,
                    _leituraMetadados.Extrair(item.Evento, configuracao)))
                .Where(item => item.Evento.Evento.Timestamp is not null)
                .OrderBy(item => item.Evento.Evento.Timestamp)
                .ThenBy(item => item.Indice)
                .ToArray();

            if (eventos.Length == 0)
            {
                return new ResultadoAnaliseTemporalCorrelacao(
                    null,
                    null,
                    Array.Empty<ItemAnaliseTemporalCorrelacao>(),
                    Array.Empty<NoHierarquiaSpan>());
            }

            var itens = new ItemAnaliseTemporalCorrelacao[eventos.Length];
            for (var i = 0; i < eventos.Length; i++)
            {
                var atual = eventos[i];
                var instante = atual.Evento.Evento.Timestamp!.Value;

                if (atual.Metadados.EhSpan)
                {
                    itens[i] = new ItemAnaliseTemporalCorrelacao(
                        atual.Evento,
                        atual.Metadados.Inicio!.Value,
                        atual.Metadados.Fim!.Value,
                        atual.Metadados.OrigemDuracao == OrigemDuracaoObservabilidade.CampoConfigurado
                            ? TipoMedicaoTemporalCorrelacao.DuracaoInformadaPeloProdutor
                            : TipoMedicaoTemporalCorrelacao.DuracaoRealDoSpan,
                        atual.Metadados);
                    continue;
                }

                if (i + 1 < eventos.Length)
                {
                    itens[i] = new ItemAnaliseTemporalCorrelacao(
                        atual.Evento,
                        instante,
                        eventos[i + 1].Evento.Evento.Timestamp!.Value,
                        TipoMedicaoTemporalCorrelacao.IntervaloAteProximoEvento,
                        atual.Metadados);
                    continue;
                }

                itens[i] = new ItemAnaliseTemporalCorrelacao(
                    atual.Evento,
                    instante,
                    instante,
                    TipoMedicaoTemporalCorrelacao.InstanteDoEvento,
                    atual.Metadados);
            }

            return new ResultadoAnaliseTemporalCorrelacao(
                itens.Min(item => item.Inicio),
                itens.Max(item => item.Fim),
                itens,
                ConstruirHierarquia(itens));
        }

        private static IReadOnlyList<NoHierarquiaSpan> ConstruirHierarquia(
            IReadOnlyList<ItemAnaliseTemporalCorrelacao> itens)
        {
            var nos = itens
                .Select((item, indice) => new NoMutavel(item, indice))
                .ToArray();
            var spans = new Dictionary<string, NoMutavel>(StringComparer.OrdinalIgnoreCase);

            foreach (var no in nos)
            {
                var spanId = no.Item.Metadados.SpanId;
                if (no.Item.Metadados.EhSpan && !string.IsNullOrWhiteSpace(spanId))
                {
                    spans.TryAdd(spanId, no);
                }
            }

            foreach (var no in nos)
            {
                NoMutavel? pai = null;
                var spanId = no.Item.Metadados.SpanId;
                var ehSpanPrincipal = !string.IsNullOrWhiteSpace(spanId)
                    && spans.TryGetValue(spanId, out var principal)
                    && ReferenceEquals(principal, no);

                if (ehSpanPrincipal)
                {
                    var parentSpanId = no.Item.Metadados.ParentSpanId;
                    if (!string.IsNullOrWhiteSpace(parentSpanId))
                    {
                        spans.TryGetValue(parentSpanId, out pai);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(spanId))
                {
                    // Um log produzido dentro de uma Activity carrega o SpanId atual. O
                    // evento de término do span é o ramo; os demais ficam como suas folhas.
                    spans.TryGetValue(spanId, out pai);
                }

                if (pai is null || ReferenceEquals(pai, no) || CriariaCiclo(no, pai)) continue;
                no.Pai = pai;
                pai.Filhos.Add(no);
            }

            foreach (var no in nos)
            {
                no.Filhos.Sort(CompararNos);
            }

            return nos
                .Where(no => no.Pai is null)
                .OrderBy(no => no, Comparer<NoMutavel>.Create(CompararNos))
                .Select(Congelar)
                .ToArray();
        }

        private static bool CriariaCiclo(NoMutavel filho, NoMutavel pai)
        {
            for (var atual = pai; atual is not null; atual = atual.Pai)
            {
                if (ReferenceEquals(atual, filho)) return true;
            }

            return false;
        }

        private static int CompararNos(NoMutavel esquerdo, NoMutavel direito)
        {
            var inicioEsquerdo = esquerdo.Item.Metadados.Inicio
                ?? esquerdo.Item.Evento.Timestamp
                ?? DateTimeOffset.MaxValue;
            var inicioDireito = direito.Item.Metadados.Inicio
                ?? direito.Item.Evento.Timestamp
                ?? DateTimeOffset.MaxValue;
            var comparacao = inicioEsquerdo.CompareTo(inicioDireito);
            return comparacao != 0 ? comparacao : esquerdo.Ordem.CompareTo(direito.Ordem);
        }

        private static NoHierarquiaSpan Congelar(NoMutavel no) => new(
            no.Item,
            no.Filhos.Select(Congelar).ToArray());

        private sealed record EventoOrdenado(
            EventoCorrelacionado Evento,
            int Indice,
            MetadadosObservabilidadeEvento Metadados);

        private sealed class NoMutavel
        {
            public NoMutavel(ItemAnaliseTemporalCorrelacao item, int ordem)
            {
                Item = item;
                Ordem = ordem;
            }

            public ItemAnaliseTemporalCorrelacao Item { get; }
            public int Ordem { get; }
            public NoMutavel? Pai { get; set; }
            public List<NoMutavel> Filhos { get; } = new();
        }
    }
}
