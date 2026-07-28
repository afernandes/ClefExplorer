namespace ClefExplorer.Models
{
    /// <summary>Onde o painel de detalhes aparece em relação à lista de eventos.</summary>
    public enum DetailPanelPosition
    {
        /// <summary>Ao lado da lista (padrão). Bom para mensagens curtas e telas largas.</summary>
        Right = 0,

        /// <summary>Abaixo da lista. Bom para stack traces longos e telas estreitas.</summary>
        Bottom = 1,
    }
}
