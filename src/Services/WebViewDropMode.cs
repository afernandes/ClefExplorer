using System;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Ponte entre a página e o host para o único flag de drop do WebView2.
    ///
    /// <para>O <c>AllowExternalDrop</c> serve a dois recursos que se atrapalham: com ele
    /// desligado, o drop de arquivos chega ao formulário com os caminhos reais (que a API
    /// web não expõe), mas o WebView2 deixa de ser alvo de soltura e o arraste DENTRO da
    /// página morre junto. A página avisa quando um arraste interno começa e o host
    /// inverte o flag só nesse intervalo.</para>
    ///
    /// <para>Estático porque quem precisa mexer no flag é um componente Blazor, e quem tem
    /// o controle é o formulário — sem uma referência natural entre os dois.</para>
    /// </summary>
    public static class WebViewDropMode
    {
        /// <summary>Instalado pelo <c>MainForm</c>; recebe <c>true</c> durante um arraste interno.</summary>
        public static Action<bool>? Apply { get; set; }

        public static void SetInternalDrag(bool interno)
        {
            try
            {
                Apply?.Invoke(interno);
            }
            catch (Exception ex)
            {
                // Um gesto que não conseguiu ajustar o flag não pode derrubar a UI.
                AppLog.Warning("Não foi possível alternar o modo de drop do WebView2", ex);
            }
        }
    }
}
