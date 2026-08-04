using System;
using System.Diagnostics;
using System.IO;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Abre o Explorer do Windows a partir da árvore de arquivos. Vive num serviço (e não
    /// no componente) porque só o processo hospedeiro pode disparar o shell — o WebView2
    /// não abre nada fora dele.
    /// </summary>
    public class ExploradorArquivos
    {
        /// <summary>
        /// Revela o caminho no Explorer: arquivo aparece selecionado dentro da pasta; se ele
        /// já não existir (log rotacionado, pasta de rede fora do ar), cai para abrir a
        /// pasta. Devolve <c>false</c> quando não há o que mostrar — cabe a quem chama
        /// avisar o usuário.
        /// </summary>
        public bool Revelar(string? caminho)
        {
            if (string.IsNullOrWhiteSpace(caminho)) return false;

            try
            {
                string argumentos;
                if (File.Exists(caminho))
                {
                    argumentos = ArgumentosSelecionar(caminho);
                }
                else
                {
                    var pasta = Directory.Exists(caminho) ? caminho : Path.GetDirectoryName(caminho);
                    if (string.IsNullOrEmpty(pasta) || !Directory.Exists(pasta)) return false;
                    argumentos = ArgumentosAbrirPasta(pasta);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = argumentos,
                    UseShellExecute = false,
                });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Não foi possível abrir o Explorer em '{caminho}'", ex);
                return false;
            }
        }

        /// <summary>
        /// Abre um endereço no navegador padrão (usado pelo aviso de versão nova).
        /// </summary>
        /// <remarks>
        /// Só http/https: o endereço chega de uma resposta da API do GitHub, e
        /// <c>UseShellExecute</c> com uma string arbitrária executaria um caminho local
        /// como programa. Recusar o que não é endereço web mantém o shell fora do alcance
        /// de qualquer coisa que venha da rede.
        /// </remarks>
        public bool AbrirUrl(string? url)
        {
            if (!EhEnderecoWeb(url)) return false;

            try
            {
                Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Não foi possível abrir '{url}' no navegador", ex);
                return false;
            }
        }

        public static bool EhEnderecoWeb(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var destino)
            && (destino.Scheme == Uri.UriSchemeHttp || destino.Scheme == Uri.UriSchemeHttps);

        /// <summary>
        /// Linha de comando que abre a pasta com o arquivo já selecionado. As aspas não são
        /// opcionais: sem elas o Explorer quebra o caminho em vírgulas e espaços e acaba
        /// abrindo "Documentos" em vez do arquivo pedido.
        /// </summary>
        public static string ArgumentosSelecionar(string caminho) =>
            $"/select,\"{caminho}\"";

        /// <summary>Linha de comando que apenas abre a pasta.</summary>
        public static string ArgumentosAbrirPasta(string pasta) =>
            $"\"{pasta}\"";
    }
}
