using System.Net;

namespace GatewayAPI.Middleware
{
    /// <summary>
    /// Restringe quem pode chamar o gateway pela rede. Só é aplicada quando Gateway:IpsPermitidos
    /// está configurada (lista separada por vírgula, ex.: "192.168.1.250"; no servidor, pela variável
    /// de ambiente Gateway__IpsPermitidos). Chamadas da própria máquina (localhost) e /health ficam
    /// sempre livres. Cada recusa é registrada no log com o IP, para facilitar liberar quem faltar.
    /// </summary>
    public class IpsPermitidosMiddleware
    {
        public const string Configuracao = "Gateway:IpsPermitidos";

        private readonly RequestDelegate _next;
        private readonly ILogger<IpsPermitidosMiddleware> _logger;

        public IpsPermitidosMiddleware(RequestDelegate next, ILogger<IpsPermitidosMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext contexto, IConfiguration configuracao)
        {
            var permitidos = LerLista(configuracao[Configuracao]);
            if (permitidos.Count == 0 || contexto.Request.Path.StartsWithSegments("/health"))
            {
                await _next(contexto);
                return;
            }

            var ip = contexto.Connection.RemoteIpAddress;
            if (ip != null && ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (ip != null && (IPAddress.IsLoopback(ip) || permitidos.Contains(ip)))
            {
                await _next(contexto);
                return;
            }

            _logger.LogWarning("🚫 [GATEWAY] Chamada recusada de {Ip} para {Metodo} {Caminho} (fora de {Configuracao})",
                ip?.ToString() ?? "desconhecido", contexto.Request.Method, contexto.Request.Path, Configuracao);
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await contexto.Response.WriteAsJsonAsync(new
            {
                sucesso = false,
                mensagem = "Este endereço não tem permissão para chamar o gateway"
            });
        }

        private static HashSet<IPAddress> LerLista(string valor)
        {
            var lista = new HashSet<IPAddress>();
            if (string.IsNullOrWhiteSpace(valor))
            {
                return lista;
            }

            foreach (var item in valor.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (IPAddress.TryParse(item, out var ip))
                {
                    lista.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
                }
            }
            return lista;
        }
    }
}
