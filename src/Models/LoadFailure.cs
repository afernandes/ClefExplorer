namespace ClefExplorer.Models
{
    /// <summary>Um caminho que não pôde ser lido durante o carregamento, e o motivo.</summary>
    /// <param name="Path">Arquivo ou pasta que falhou.</param>
    /// <param name="Reason">Mensagem da exceção que impediu a leitura.</param>
    public record LoadFailure(string Path, string Reason);
}
