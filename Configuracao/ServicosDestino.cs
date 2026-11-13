namespace GatewayAPI.Configuracao
{
    /// <summary>
    /// Endereços dos programas para onde o gateway encaminha, lidos de Gateway:Servicos no appsettings.json
    /// (ou de variáveis de ambiente, ex.: Gateway__Servicos__Produto). Mover um programa de máquina
    /// passa a ser uma troca de configuração, sem recompilar.
    /// </summary>
    public class ServicosDestino
    {
        public const string ClienteHttpTarefa = "TarefaService";

        public string Produto { get; }
        public string Fornecedor { get; }
        public string Cliente { get; }
        public string Datasheet { get; }
        public string Ariba { get; }

        public ServicosDestino(IConfiguration configuracao)
        {
            Produto = Ler(configuracao, "Gateway:Servicos:Produto", "http://localhost:6001");
            Fornecedor = Ler(configuracao, "Gateway:Servicos:Fornecedor", "http://localhost:6002");
            Cliente = Ler(configuracao, "Gateway:Servicos:Cliente", "http://localhost:6003");
            Datasheet = Ler(configuracao, "Gateway:Servicos:Datasheet", "http://localhost:6004");
            Ariba = Ler(configuracao, "Gateway:Ariba:ServicoUrl", Endpoints.AribaEndpoints.ServicoUrlPadrao);
        }

        /// <summary>URL completa de destino para um tipo de tarefa (PRODUTO, FORNECEDOR, CLIENTE, DATASHEET, ARIBA).</summary>
        public string UrlDaTarefa(string tipoTarefa) => (tipoTarefa ?? string.Empty).ToUpperInvariant() switch
        {
            "PRODUTO" => $"{Produto}/api/produtos/cadastrar",
            "FORNECEDOR" => $"{Fornecedor}/api/fornecedores/cadastrar",
            "CLIENTE" => $"{Cliente}/api/clientes/cadastrar",
            "DATASHEET" => $"{Datasheet}/api/datasheet/processar",
            // AribaSourcing: inicia o processamento / define a prioridade das empresas (dados.prioridade)
            "ARIBA" => $"{Ariba}/api/ariba/iniciar",
            _ => throw new ArgumentException($"Tipo de tarefa inválido: {tipoTarefa}")
        };

        private static string Ler(IConfiguration configuracao, string chave, string padrao)
        {
            var valor = configuracao.GetValue<string>(chave);
            return (string.IsNullOrWhiteSpace(valor) ? padrao : valor).TrimEnd('/');
        }
    }
}
