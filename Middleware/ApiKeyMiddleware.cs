using System.Security.Cryptography;
using System.Text;

namespace GatewayAPI.Middleware
{
    /// <summary>
    /// Autenticação opcional por chave. Só é exigida quando Gateway:ApiKey está configurada
    /// (de preferência pela variável de ambiente Gateway__ApiKey, nunca no repositório).
    /// Com a chave configurada, toda chamada precisa do cabeçalho X-Api-Key; /health e /swagger ficam livres.
    /// </summary>
    public class ApiKeyMiddleware
    {
        public const string Cabecalho = "X-Api-Key";

        private readonly RequestDelegate _next;

        public ApiKeyMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext contexto, IConfiguration configuracao)
        {
            var chave = configuracao["Gateway:ApiKey"];

            if (string.IsNullOrEmpty(chave)
                || contexto.Request.Path.StartsWithSegments("/health")
                || contexto.Request.Path.StartsWithSegments("/swagger"))
            {
                await _next(contexto);
                return;
            }

            var recebida = contexto.Request.Headers[Cabecalho].ToString();
            var confere = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(recebida), Encoding.UTF8.GetBytes(chave));

            if (!confere)
            {
                contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await contexto.Response.WriteAsJsonAsync(new
                {
                    sucesso = false,
                    mensagem = $"Chave de API ausente ou inválida (cabeçalho {Cabecalho})"
                });
                return;
            }

            await _next(contexto);
        }
    }
}
