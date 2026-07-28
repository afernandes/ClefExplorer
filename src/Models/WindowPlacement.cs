using System.Text.Json.Serialization;

namespace ClefExplorer.Models
{
    /// <summary>Posição e tamanho da janela principal, preservados entre execuções.</summary>
    public class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }

        /// <summary>Um placement só é utilizável se tiver dimensões plausíveis.</summary>
        [JsonIgnore]
        public bool IsUsable => Width >= 400 && Height >= 300;
    }
}
