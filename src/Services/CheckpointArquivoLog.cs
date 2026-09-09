namespace ClefExplorer.Services;

/// <summary>Identifica o arquivo e o conteúdo nas extremidades do trecho já consumido.</summary>
public sealed class CheckpointArquivoLog
{
    private const int TamanhoAmostra = 512;
    private readonly DateTime _criacao;
    private readonly DateTime _ultimaEscrita;
    private readonly byte[] _inicio;
    private readonly byte[] _fronteira;

    private CheckpointArquivoLog(long offset, DateTime criacao, DateTime ultimaEscrita, byte[] inicio, byte[] fronteira)
    {
        Offset = offset;
        _criacao = criacao;
        _ultimaEscrita = ultimaEscrita;
        _inicio = inicio;
        _fronteira = fronteira;
    }

    public long Offset { get; }

    public static CheckpointArquivoLog Capturar(FileStream arquivo, long offset)
    {
        var tamanho = (int)Math.Min(offset, TamanhoAmostra);
        return new(offset, File.GetCreationTimeUtc(arquivo.SafeFileHandle),
            File.GetLastWriteTimeUtc(arquivo.SafeFileHandle),
            Ler(arquivo, 0, tamanho), Ler(arquivo, offset - tamanho, tamanho));
    }

    public bool Corresponde(FileStream arquivo) => arquivo.Length >= Offset
        && File.GetCreationTimeUtc(arquivo.SafeFileHandle) == _criacao
        && (arquivo.Length != Offset || File.GetLastWriteTimeUtc(arquivo.SafeFileHandle) == _ultimaEscrita)
        && Ler(arquivo, 0, _inicio.Length).AsSpan().SequenceEqual(_inicio)
        && Ler(arquivo, Offset - _fronteira.Length, _fronteira.Length).AsSpan().SequenceEqual(_fronteira);

    private static byte[] Ler(FileStream arquivo, long offset, int tamanho)
    {
        var dados = new byte[tamanho];
        var lidos = 0;
        while (lidos < tamanho)
        {
            var quantidade = RandomAccess.Read(arquivo.SafeFileHandle, dados.AsSpan(lidos), offset + lidos);
            if (quantidade == 0) return dados[..lidos];
            lidos += quantidade;
        }
        return dados;
    }
}
