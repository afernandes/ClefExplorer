using ClefExplorer.Models;

namespace ClefExplorer.Services;

public interface ILeitorArquivoLog
{
    /// <param name="pool">
    /// Compartilha as strings repetidas entre eventos (nível, template, chaves de
    /// propriedade). Opcional: sem ele a leitura funciona igual, só ocupa mais memória.
    /// </param>
    Task<ResultadoLeituraArquivoLog> LerAsync(
        string arquivo,
        IReadOnlyList<string> textosIgnorados,
        PoolDeTextos? pool = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Interpreta um bloco de bytes já lido do arquivo, contendo apenas linhas completas.
    /// </summary>
    /// <remarks>
    /// Existe para o acompanhamento ao vivo, que precisa da mesma interpretação da carga
    /// mas não pode delegar a abertura do arquivo: quem acompanha é dono do offset, do
    /// reposicionamento após truncamento e do descarte da linha grande demais. Sem este
    /// método o tail chamava o parser estático e um leitor injetado cobria só a carga.
    /// <para><c>OffsetFinal</c> volta nulo — a posição no arquivo é de quem leu o bloco.</para>
    /// </remarks>
    /// <param name="inicioDoArquivo">
    /// Informa que o bloco começa no byte 0. Só então o BOM pode ser descartado: os mesmos
    /// três bytes no meio do arquivo são conteúdo de uma linha válida.
    /// </param>
    ResultadoLeituraArquivoLog LerTrecho(
        ReadOnlySpan<byte> bloco,
        string arquivo,
        IReadOnlyList<string> textosIgnorados,
        bool inicioDoArquivo,
        PoolDeTextos? pool = null,
        CancellationToken cancellationToken = default);
}
