using System.Net.Http.Headers;
using System.Text.Json;

namespace ClefExplorer.Services
{
    /// <summary>
    /// Lê o último release publicado em github.com/afernandes/ClefExplorer.
    /// </summary>
    /// <remarks>
    /// Usa <c>/releases/latest</c>, que a API do GitHub já define como o release mais
    /// recente NÃO marcado como pré-lançamento nem rascunho — assim uma prévia publicada
    /// para testes não vira aviso para todo mundo.
    /// </remarks>
    public sealed class ConsultorReleasesGithub : IConsultorReleases
    {
        public const string UrlApi = "https://api.github.com/repos/afernandes/ClefExplorer/releases/latest";

        private readonly HttpClient _http;

        public ConsultorReleasesGithub(HttpClient http)
        {
            _http = http;
        }

        /// <summary>
        /// Cliente com o que a API do GitHub exige: User-Agent (sem ele a resposta é 403) e
        /// um tempo-limite curto, porque isto roda com o aplicativo já aberto e ninguém
        /// deve esperar por ele.
        /// </summary>
        public static HttpClient CriarCliente()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClefExplorer", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return http;
        }

        public async Task<ReleaseGithub?> UltimoAsync(CancellationToken cancellationToken)
        {
            using var resposta = await _http.GetAsync(UrlApi, cancellationToken).ConfigureAwait(false);
            if (!resposta.IsSuccessStatusCode)
            {
                AppLog.Warning($"O canal oficial respondeu {(int)resposta.StatusCode} ao procurar a última versão.");
                return null;
            }

            await using var conteudo = await resposta.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(conteudo, cancellationToken: cancellationToken).ConfigureAwait(false);

            return Interpretar(json.RootElement);
        }

        /// <summary>
        /// Separado do transporte para o formato do release ser testável sem rede.
        /// </summary>
        public static ReleaseGithub? Interpretar(JsonElement release)
        {
            if (!release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var texto = tag.GetString()!.TrimStart('v', 'V');
            if (!Version.TryParse(texto, out var versao)) return null;

            var url = release.TryGetProperty("html_url", out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString()!
                : ServicoAtualizacao.UrlDosReleases;

            return new ReleaseGithub(versao, url);
        }
    }
}
