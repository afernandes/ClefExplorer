using ClefExplorer.Services;

namespace ClefExplorer.Tests;

internal sealed class AtualizacaoTeste : IAtualizadorLocal, IConsultorReleases
{
    public bool PodeAplicar => false;
    public Task<string?> PrepararAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public void AplicarEReiniciar() => throw new InvalidOperationException("Atualização não disponível no teste.");
    public Task<ReleaseGithub?> UltimoAsync(CancellationToken cancellationToken) => Task.FromResult<ReleaseGithub?>(null);
}
