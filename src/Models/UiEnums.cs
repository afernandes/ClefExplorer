namespace ClefExplorer.Models
{
    /// <summary>Como os eventos são apresentados.</summary>
    public enum LogViewMode
    {
        /// <summary>Lista compacta (padrão): uma linha por evento, com a mensagem em destaque.</summary>
        List = 0,

        /// <summary>Tabela com colunas: permite ordenar, agrupar e escolher as colunas exibidas.</summary>
        Grid = 1,
    }

    /// <summary>Onde o painel de detalhes aparece em relação à lista de eventos.</summary>
    public enum DetailPanelPosition
    {
        /// <summary>Ao lado da lista (padrão). Bom para mensagens curtas e telas largas.</summary>
        Right = 0,

        /// <summary>Abaixo da lista. Bom para stack traces longos e telas estreitas.</summary>
        Bottom = 1,
    }
}
