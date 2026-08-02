using System.Collections.Generic;

namespace ClefExplorer.Models
{
    /// <summary>
    /// Nó da árvore de arquivos da barra lateral. A hierarquia é VIRTUAL: vem do nome do
    /// arquivo dividido por <c>.</c> (ex.: <c>TOTVS.Omnishop.API.clef</c> → TOTVS › Omnishop
    /// › API), e não da estrutura de diretórios. Por isso um nó sem
    /// <see cref="FullPath"/> não corresponde necessariamente a uma pasta em disco.
    /// </summary>
    public class FileTreeNode
    {
        public string Name { get; set; } = "";

        /// <summary>Caminho do arquivo quando o nó é uma folha; <c>null</c> em nós de agrupamento.</summary>
        public string? FullPath { get; set; }

        public List<FileTreeNode> Children { get; set; } = new();
        public FileTreeNode? Parent { get; set; }
        public bool IsBackup { get; set; }

        /// <summary>Caminhos de log sob este nó, incluindo o do próprio nó quando houver.</summary>
        public IEnumerable<string> CaminhosDescendentes()
        {
            if (!string.IsNullOrEmpty(FullPath)) yield return FullPath;
            foreach (var filho in Children)
            {
                foreach (var caminho in filho.CaminhosDescendentes()) yield return caminho;
            }
        }
    }
}
