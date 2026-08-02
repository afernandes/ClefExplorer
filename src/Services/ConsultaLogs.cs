using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>Resultado identificado de uma consulta sobre os eventos carregados.</summary>
    public sealed record ResultadoConsultaLogs(long Geracao, IReadOnlyList<ClefEvent> Eventos);

    /// <summary>Falha associada à geração que executou a consulta.</summary>
    public sealed class FalhaConsultaLogsException : Exception
    {
        public long Geracao { get; }

        public FalhaConsultaLogsException(long geracao, Exception innerException)
            : base(innerException.Message, innerException)
        {
            Geracao = geracao;
        }
    }

    /// <summary>
    /// Coordena consultas concorrentes. Uma nova solicitação cancela a anterior e cada
    /// resultado carrega sua geração, permitindo que a UI rejeite callbacks atrasados.
    /// </summary>
    public sealed class ConsultaLogs : IDisposable
    {
        private CancellationTokenSource? _consultaAtual;
        private long _geracaoAtual;
        private bool _disposed;

        public async Task<ResultadoConsultaLogs?> ExecutarAsync(
            IReadOnlyList<ClefEvent> eventos,
            LogFilterCriteria criterios,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(eventos);
            ArgumentNullException.ThrowIfNull(criterios);

            var geracao = Interlocked.Increment(ref _geracaoAtual);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var anterior = Interlocked.Exchange(ref _consultaAtual, cts);
            if (anterior is not null)
            {
                try { anterior.Cancel(); } catch (ObjectDisposedException) { }
            }

            try
            {
                var resultado = await Task.Run(
                    () => LogFilter.Apply(eventos, criterios, cts.Token),
                    cts.Token).ConfigureAwait(false);

                cts.Token.ThrowIfCancellationRequested();
                return EstaAtual(geracao)
                    ? new ResultadoConsultaLogs(geracao, resultado)
                    : null;
            }
            catch (Exception) when (!EstaAtual(geracao))
            {
                // Uma consulta substituída não pode exibir erro nem publicar resultado.
                return null;
            }
            catch (Exception ex)
            {
                throw new FalhaConsultaLogsException(geracao, ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref _consultaAtual, null, cts);
                cts.Dispose();
            }
        }

        public bool EstaAtual(long geracao) => Volatile.Read(ref _geracaoAtual) == geracao;

        public void CancelarConsultaAtual()
        {
            Interlocked.Increment(ref _geracaoAtual);
            var atual = Interlocked.Exchange(ref _consultaAtual, null);
            if (atual is null) return;
            try { atual.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelarConsultaAtual();
        }
    }
}
