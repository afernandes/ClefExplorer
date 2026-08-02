using System.Collections.Concurrent;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Compartilha as strings que se repetem em todo evento de log.
    ///
    /// <para>Medido num arquivo de 200 mil eventos (158 MB): o nome do nível, o
    /// <c>@mt</c> e as chaves das propriedades somavam <b>3,8 milhões de instâncias</b>
    /// para apenas <b>27 valores distintos</b> — cada linha do arquivo recria as mesmas
    /// strings. Guardar uma instância por valor devolve ~170 MB dos 790 MB que a carga
    /// ocupava, sem mudar nada no resto do aplicativo.</para>
    ///
    /// <para>Deliberadamente NÃO se usa <see cref="string.Intern"/>: o pool do runtime
    /// nunca libera, então os textos de um log já fechado ficariam para sempre. Este pool
    /// vive enquanto durar a carga e vai embora com ela.</para>
    /// </summary>
    public sealed class PoolDeTextos
    {
        // Concurrent porque a carga lê vários arquivos em paralelo, e o ganho só é pleno
        // quando o pool é compartilhado entre eles (os mesmos templates se repetem em
        // todos os arquivos de uma mesma aplicação).
        private readonly ConcurrentDictionary<string, string> _textos = new(StringComparer.Ordinal);

        /// <summary>Devolve a instância compartilhada para este texto.</summary>
        public string? Compartilhar(string? texto)
        {
            if (texto is null) return null;
            if (texto.Length == 0) return string.Empty;
            return _textos.GetOrAdd(texto, texto);
        }

        /// <summary>Quantos valores distintos o pool guarda (diagnóstico).</summary>
        public int Count => _textos.Count;
    }
}
