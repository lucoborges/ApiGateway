using GatewayAPI.Configuracao;

namespace GatewayAPI.Middleware
{
    /// <summary>
    /// Envia a chave do HSE-Cadastros (Gateway:ChaveCadastros) no cabeçalho X-Api-Key de toda chamada
    /// que sai para os servidores de produtos, fornecedores e clientes. Os demais destinos não recebem
    /// a chave. Com a chave vazia, nada muda. Vale para todos os clientes HTTP do gateway.
    /// </summary>
    public class ChaveCadastrosHandler : DelegatingHandler
    {
        public const string Configuracao = "Gateway:ChaveCadastros";

        private readonly string _chave;
        private readonly HashSet<string> _destinos = new(StringComparer.OrdinalIgnoreCase);

        public ChaveCadastrosHandler(IConfiguration configuracao, ServicosDestino servicos)
        {
            _chave = (configuracao[Configuracao] ?? string.Empty).Trim();
            foreach (var endereco in new[] { servicos.Produto, servicos.Fornecedor, servicos.Cliente })
            {
                if (Uri.TryCreate(endereco, UriKind.Absolute, out var uri))
                {
                    _destinos.Add(uri.Authority);
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancelamento)
        {
            if (_chave.Length > 0 && requisicao.RequestUri != null && _destinos.Contains(requisicao.RequestUri.Authority))
            {
                // Remove antes: a chave de quem chamou o gateway nunca segue para o destino
                requisicao.Headers.Remove(ApiKeyMiddleware.Cabecalho);
                requisicao.Headers.TryAddWithoutValidation(ApiKeyMiddleware.Cabecalho, _chave);
            }

            return base.SendAsync(requisicao, cancelamento);
        }
    }
}
