using ClefExplorer.Services;

namespace ClefExplorer.Tests;

internal sealed class LeitorBloqueavel : ILeitorArquivoLog
{
    private readonly LeitorArquivoLog _real = new();
    public TaskCompletionSource Iniciado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ResultadoLeituraArquivoLog> LerAsync(string arquivo, IReadOnlyList<string> textosIgnorados,
        PoolDeTextos? pool = null, CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(arquivo) == "lento.clef")
        {
            Iniciado.TrySetResult();
            await Liberar.Task.WaitAsync(cancellationToken);
        }
        return await _real.LerAsync(arquivo, textosIgnorados, pool, cancellationToken);
    }
    public ResultadoLeituraArquivoLog LerTrecho(ReadOnlySpan<byte> bloco, string arquivo,
        IReadOnlyList<string> textosIgnorados, bool inicioDoArquivo, PoolDeTextos? pool = null,
        CancellationToken cancellationToken = default)
        => _real.LerTrecho(bloco, arquivo, textosIgnorados, inicioDoArquivo, pool, cancellationToken);
}
