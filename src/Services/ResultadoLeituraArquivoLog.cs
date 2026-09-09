using ClefExplorer.Models;

namespace ClefExplorer.Services;

public sealed record ResultadoLeituraArquivoLog(
    IReadOnlyList<ClefEvent> Eventos, long? OffsetFinal, int LinhasInvalidas, string? PrimeiroErro)
{
    public CheckpointArquivoLog? Checkpoint { get; init; }
}
