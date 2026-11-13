using System.Text;

namespace GatewayAPI.Endpoints
{
    /// <summary>
    /// Entrada do gateway dedicada ao AribaSourcing. O programa de origem envia para esta entrada
    /// (Gateway:Ariba:EntradaUrl, padrão http://localhost:5001) e o gateway repassa ao AribaSourcing
    /// (Gateway:Ariba:ServicoUrl, padrão http://localhost:6005), devolvendo a resposta como veio.
    ///   POST /api/ariba/iniciar  { "codigoTarefa": "...", "dados": { "prioridade": ["UNIÃO", "ALIANÇA"] } }
    ///   GET  /api/ariba/status
    /// </summary>
    public static class AribaEndpoints
    {
        public const string NomeHttpClient = "AribaService";
        public const string EntradaUrlPadrao = "http://localhost:5001";
        public const string ServicoUrlPadrao = "http://localhost:6005";

        public static void MapAribaEndpoints(this WebApplication app, string entradaUrl)
        {
            // As rotas só respondem na porta da entrada do Ariba
            var grupo = app.MapGroup("/api/ariba").RequireHost($"*:{new Uri(entradaUrl).Port}");

            grupo.MapPost("/iniciar", (HttpContext contexto, IHttpClientFactory fabrica, ILoggerFactory logs) =>
                EncaminharAsync(contexto, fabrica, logs, HttpMethod.Post, "api/ariba/iniciar"));

            grupo.MapGet("/status", (HttpContext contexto, IHttpClientFactory fabrica, ILoggerFactory logs) =>
                EncaminharAsync(contexto, fabrica, logs, HttpMethod.Get, "api/ariba/status"));
        }

        private static async Task<IResult> EncaminharAsync(
            HttpContext contexto, IHttpClientFactory fabrica, ILoggerFactory logs, HttpMethod metodo, string caminho)
        {
            var logger = logs.CreateLogger("GatewayAPI.Ariba");
            var cliente = fabrica.CreateClient(NomeHttpClient);
            var destino = new Uri(cliente.BaseAddress!, caminho);

            try
            {
                using var requisicao = new HttpRequestMessage(metodo, caminho);

                if (metodo == HttpMethod.Post)
                {
                    using var leitor = new StreamReader(contexto.Request.Body, Encoding.UTF8);
                    var corpo = await leitor.ReadToEndAsync();
                    logger.LogInformation("📥 [GATEWAY-ARIBA] Recebido na entrada do Ariba: {Corpo}", corpo);
                    requisicao.Content = new StringContent(corpo, Encoding.UTF8, "application/json");
                }

                logger.LogInformation("🔀 [GATEWAY-ARIBA] Encaminhando {Metodo} para {Destino}", metodo, destino);

                using var resposta = await cliente.SendAsync(requisicao, contexto.RequestAborted);
                var conteudo = await resposta.Content.ReadAsStringAsync();

                logger.LogInformation("📤 [GATEWAY-ARIBA] Resposta do AribaSourcing: {Status}", (int)resposta.StatusCode);

                return Results.Content(conteudo, "application/json; charset=utf-8", statusCode: (int)resposta.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "❌ [GATEWAY-ARIBA] AribaSourcing indisponível em {Destino}", destino);
                return Results.Json(new
                {
                    sucesso = false,
                    mensagem = "AribaSourcing indisponível",
                    servicoDestino = destino.ToString(),
                    detalhes = ex.Message
                }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (TaskCanceledException ex) when (!contexto.RequestAborted.IsCancellationRequested)
            {
                logger.LogError(ex, "⏰ [GATEWAY-ARIBA] AribaSourcing não respondeu a tempo em {Destino}", destino);
                return Results.Json(new
                {
                    sucesso = false,
                    mensagem = "Tempo limite excedido aguardando o AribaSourcing",
                    servicoDestino = destino.ToString()
                }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        }
    }
}
