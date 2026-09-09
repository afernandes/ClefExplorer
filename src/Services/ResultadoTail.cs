using ClefExplorer.Models;

namespace ClefExplorer.Services;

internal sealed record ResultadoTail(IReadOnlyList<ClefEvent> Eventos, long? NovoOffset,
    CheckpointArquivoLog? Checkpoint = null);
