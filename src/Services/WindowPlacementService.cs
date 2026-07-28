using System;
using System.Drawing;
using System.Text.Json;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Preserva posição/tamanho da janela entre execuções. Antes o app abria sempre em
    /// 1200x800 no mesmo lugar, ignorando como o usuário tinha deixado.
    /// </summary>
    public class WindowPlacementService
    {
        private const string FileName = "window.json";

        private readonly AppStorage _storage;

        public WindowPlacementService(AppStorage storage) => _storage = storage;

        public WindowPlacement? Load()
        {
            try
            {
                var json = _storage.ReadText(FileName);
                if (json is null) return null;

                var placement = JsonSerializer.Deserialize<WindowPlacement>(json);
                return placement is { IsUsable: true } ? placement : null;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Não foi possível ler a posição da janela", ex);
                return null;
            }
        }

        public void Save(WindowPlacement placement)
        {
            try
            {
                _storage.WriteText(FileName, JsonSerializer.Serialize(placement, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // Não é acionável pelo usuário: na próxima abertura usamos o tamanho padrão.
                AppLog.Warning("Não foi possível salvar a posição da janela", ex);
            }
        }

        /// <summary>
        /// Diz se o placement ainda cabe em algum monitor conectado. Sem isso, desconectar
        /// um monitor secundário faria a janela reabrir fora da área visível.
        /// </summary>
        public static bool IsVisibleOnAnyScreen(WindowPlacement placement, Rectangle[] screens)
        {
            var bounds = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
            foreach (var screen in screens)
            {
                var intersection = Rectangle.Intersect(screen, bounds);
                // Exige uma sobreposição relevante — alguns pixels na borda não bastam
                // para o usuário conseguir alcançar a barra de título.
                if (intersection.Width >= 200 && intersection.Height >= 100)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
