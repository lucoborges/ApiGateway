using GatewayAPI.Configuracao;

namespace GatewayAPI.Middleware
{
    /// <summary>
    /// Envia no cabeçalho X-Api-Key a chave de cada programa de destino, em toda chamada que sai do gateway:
    ///   Gateway:ChaveCadastros -> HSE-Cadastros (produtos, fornecedores e clientes)
    ///   Gateway:ChaveAriba     -> AribaSourcing (pela tarefa ARIBA e pela entrada dedicada da porta 5001)
    /// Os demais destinos não recebem chave. A chave de quem chamou o gateway nunca segue para o destino.
    /// Chave vazia = nada muda para aquele destino. Vale para todos os clientes HTTP do gateway.
    /// </summary>
    public class ChavesDestinoHandler : DelegatingHandler
    {
        public const string ConfiguracaoCadastros = "Gateway:ChaveCadastros";
        public const string ConfiguracaoAriba = "Gateway:ChaveAriba";

        // Endereço (host:porta) do destino -> chave enviada a ele
        private readonly Dictionary<string, string> _chaves = new(StringComparer.OrdinalIgnoreCase);

        public ChavesDestinoHandler(IConfiguration configuracao, ServicosDestino servicos)
        {
            var cadastros = (configuracao[ConfiguracaoCadastros] ?? string.Empty).Trim();
            foreach (var endereco in new[] { servicos.Produto, servicos.Fornecedor, servicos.Cliente })
            {
                Registrar(endereco, cadastros);
            }
            Registrar(servicos.Ariba, (configuracao[ConfiguracaoAriba] ?? string.Empty).Trim());
        }

        private void Registrar(string endereco, string chave)
        {
            if (chave.Length > 0 && Uri.TryCreate(endereco, UriKind.Absolute, out var uri))
            {
                _chaves[uri.Authority] = chave;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancelamento)
        {
            // Remove sempre: a chave de quem chamou o gateway nunca segue para o destino
            requisicao.Headers.Remove(ApiKeyMiddleware.Cabecalho);

            if (requisicao.RequestUri != null && _chaves.TryGetValue(requisicao.RequestUri.Authority, out var chave))
            {
                requisicao.Headers.TryAddWithoutValidation(ApiKeyMiddleware.Cabecalho, chave);
            }

            return base.SendAsync(requisicao, cancelamento);
        }
    }
}
