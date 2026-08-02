using System;
using System.Collections.Generic;
using System.IO;
using ClefExplorer.Models;

namespace ClefExplorer.Helpers
{
    /// <summary>
    /// Busca textual na árvore de arquivos da barra lateral. Devolve o conjunto de nós que
    /// devem continuar visíveis, em vez de uma árvore nova: as instâncias dos nós são as
    /// mesmas, então a marcação dos checkboxes (que o <c>OmniTree</c> guarda por
    /// referência) sobrevive ao filtro.
    /// </summary>
    public static class FiltroArvoreArquivos
    {
        /// <summary>
        /// Nós visíveis para o termo informado. Termo vazio = todos. Um nó fica visível
        /// quando ele casa, quando um descendente casa (para não perder o caminho até o
        /// arquivo) ou quando um ancestral casa (achar uma pasta mostra o que há dentro).
        /// </summary>
        public static HashSet<FileTreeNode> Visiveis(IEnumerable<FileTreeNode> raizes, string? termo)
        {
            ArgumentNullException.ThrowIfNull(raizes);

            var visiveis = new HashSet<FileTreeNode>();
            var busca = termo?.Trim();
            if (string.IsNullOrEmpty(busca))
            {
                foreach (var raiz in raizes) MarcarTudo(raiz, visiveis);
                return visiveis;
            }

            foreach (var raiz in raizes) Marcar(raiz, busca, ancestralCasou: false, visiveis);
            return visiveis;
        }

        /// <summary>
        /// Diz se o nó casa com o termo. Além do rótulo, compara o caminho completo: a
        /// árvore quebra o nome do arquivo em vários níveis, então buscar "Fiscal.Zeus"
        /// não casaria com nenhum rótulo isolado.
        /// </summary>
        public static bool Casa(FileTreeNode node, string termo)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (node.Name.Contains(termo, StringComparison.OrdinalIgnoreCase)) return true;
            return node.FullPath is { } caminho
                && Path.GetFileName(caminho).Contains(termo, StringComparison.OrdinalIgnoreCase);
        }

        private static void MarcarTudo(FileTreeNode node, HashSet<FileTreeNode> visiveis)
        {
            visiveis.Add(node);
            foreach (var filho in node.Children) MarcarTudo(filho, visiveis);
        }

        private static bool Marcar(
            FileTreeNode node,
            string termo,
            bool ancestralCasou,
            HashSet<FileTreeNode> visiveis)
        {
            var casou = ancestralCasou || Casa(node, termo);

            var algumFilhoVisivel = false;
            foreach (var filho in node.Children)
            {
                algumFilhoVisivel |= Marcar(filho, termo, casou, visiveis);
            }

            var visivel = casou || algumFilhoVisivel;
            if (visivel) visiveis.Add(node);
            return visivel;
        }
    }
}
