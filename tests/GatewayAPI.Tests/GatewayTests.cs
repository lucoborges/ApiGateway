using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GatewayAPI.Configuracao;
using GatewayAPI.Endpoints;
using GatewayAPI.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GatewayAPI.Tests
{
    /// <summary>
    /// Sobe o gateway em memória. Nenhuma chamada sai para a rede: os programas de destino
    /// são substituídos por um destino falso que registra o que recebeu.
    /// </summary>
    public class GatewayTests
    {
        private sealed class DestinoFalso : HttpMessageHandler
        {
            public List<(HttpMethod Metodo, Uri Url, string Corpo)> Chamadas { get; } = new();

            /// <summary>Valor do cabeçalho X-Api-Key de cada chamada recebida (null = sem cabeçalho).</summary>
            public List<string> ChavesRecebidas { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancelamento)
            {
                var corpo = requisicao.Content == null ? "" : await requisicao.Content.ReadAsStringAsync(cancelamento);
                Chamadas.Add((requisicao.Method, requisicao.RequestUri, corpo));
                ChavesRecebidas.Add(requisicao.Headers.TryGetValues(ApiKeyMiddleware.Cabecalho, out var valores)
                    ? string.Join(",", valores)
                    : null);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"recebido\":true}", Encoding.UTF8, "application/json")
                };
            }
        }

        private static WebApplicationFactory<Program> CriarGateway(DestinoFalso destino, string apiKey = null, string chaveCadastros = null)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                if (chaveCadastros != null)
                {
                    host.UseSetting(ChaveCadastrosHandler.Configuracao, chaveCadastros);
                }
                host.UseSetting("Gateway:Logs:Pasta", Path.Combine(Path.GetTempPath(), "gatewayapi-testes-logs"));
                if (apiKey != null)
                {
                    host.UseSetting("Gateway:ApiKey", apiKey);
                }

                host.ConfigureTestServices(servicos =>
                {
                    servicos.AddHttpClient(ServicosDestino.ClienteHttpTarefa).ConfigurePrimaryHttpMessageHandler(() => destino);
                    servicos.AddHttpClient(AribaEndpoints.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => destino);
                });
            });
        }

        private static HttpClient ClienteNaPorta(WebApplicationFactory<Program> gateway, int porta)
        {
            return gateway.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"http://localhost:{porta}")
            });
        }

        [Fact]
        public async Task Health_responde_ok()
        {
            using var gateway = CriarGateway(new DestinoFalso());
            var resposta = await ClienteNaPorta(gateway, 5000).GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
            Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        }

        [Fact]
        public async Task Tarefa_sem_campos_obrigatorios_devolve_400()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            var resposta = await ClienteNaPorta(gateway, 5000)
                .PostAsJsonAsync("/api/gateway/tarefa", new { dados = new { } });

            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            Assert.Empty(destino.Chamadas);
        }

        [Theory]
        [InlineData("PRODUTO", "http://localhost:6001/api/produtos/cadastrar")]
        [InlineData("fornecedor", "http://localhost:6002/api/fornecedores/cadastrar")]
        [InlineData("CLIENTE", "http://localhost:6003/api/clientes/cadastrar")]
        [InlineData("DATASHEET", "http://localhost:6004/api/datasheet/processar")]
        [InlineData("ARIBA", "http://localhost:6005/api/ariba/iniciar")]
        public async Task Tarefa_e_encaminhada_para_o_programa_certo(string tipoTarefa, string urlEsperada)
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            var resposta = await ClienteNaPorta(gateway, 5000).PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-123",
                tipoTarefa,
                dados = new { nome = "teste" }
            });

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var chamada = Assert.Single(destino.Chamadas);
            Assert.Equal(HttpMethod.Post, chamada.Metodo);
            Assert.Equal(urlEsperada, chamada.Url.ToString());
            Assert.Contains("T-123", chamada.Corpo);
        }

        [Fact]
        public async Task Tarefa_com_tipo_invalido_nao_chama_nenhum_programa()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            var resposta = await ClienteNaPorta(gateway, 5000).PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-1",
                tipoTarefa = "INEXISTENTE"
            });

            Assert.False(resposta.IsSuccessStatusCode);
            Assert.Empty(destino.Chamadas);
        }

        [Fact]
        public async Task Entrada_do_Ariba_encaminha_na_porta_5001()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            var resposta = await ClienteNaPorta(gateway, 5001).GetAsync("/api/ariba/status");

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var chamada = Assert.Single(destino.Chamadas);
            Assert.Equal("http://localhost:6005/api/ariba/status", chamada.Url.ToString());
        }

        [Fact]
        public async Task Entrada_do_Ariba_nao_existe_na_porta_5000()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            var resposta = await ClienteNaPorta(gateway, 5000).GetAsync("/api/ariba/status");

            Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
            Assert.Empty(destino.Chamadas);
        }

        [Theory]
        [InlineData("PRODUTO", "chave-dos-cadastros")]
        [InlineData("FORNECEDOR", "chave-dos-cadastros")]
        [InlineData("CLIENTE", "chave-dos-cadastros")]
        [InlineData("DATASHEET", null)]
        [InlineData("ARIBA", null)]
        public async Task Chave_dos_cadastros_so_e_enviada_para_produtos_fornecedores_e_clientes(string tipoTarefa, string chaveEsperada)
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, chaveCadastros: "chave-dos-cadastros");

            var resposta = await ClienteNaPorta(gateway, 5000).PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-1",
                tipoTarefa,
                dados = new { nome = "teste" }
            });

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.Equal(chaveEsperada, Assert.Single(destino.ChavesRecebidas));
        }

        [Fact]
        public async Task Sem_chave_dos_cadastros_nenhum_cabecalho_de_chave_e_enviado()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            await ClienteNaPorta(gateway, 5000).PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-1",
                tipoTarefa = "PRODUTO"
            });

            Assert.Null(Assert.Single(destino.ChavesRecebidas));
        }

        [Fact]
        public async Task Chave_de_quem_chama_o_gateway_nao_e_a_que_segue_para_os_cadastros()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, apiKey: "chave-do-gateway", chaveCadastros: "chave-dos-cadastros");
            var cliente = ClienteNaPorta(gateway, 5000);
            cliente.DefaultRequestHeaders.Add(ApiKeyMiddleware.Cabecalho, "chave-do-gateway");

            var resposta = await cliente.PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-1",
                tipoTarefa = "CLIENTE"
            });

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.Equal("chave-dos-cadastros", Assert.Single(destino.ChavesRecebidas));
        }

        [Fact]
        public async Task Com_chave_configurada_chamada_sem_chave_devolve_401()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, apiKey: "chave-de-teste");

            var resposta = await ClienteNaPorta(gateway, 5000).PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-1",
                tipoTarefa = "PRODUTO"
            });

            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
            Assert.Empty(destino.Chamadas);
        }

        [Fact]
        public async Task Com_chave_configurada_chamada_com_chave_passa_e_health_fica_livre()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, apiKey: "chave-de-teste");
            var cliente = ClienteNaPorta(gateway, 5000);

            var saude = await cliente.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, saude.StatusCode);

            cliente.DefaultRequestHeaders.Add(ApiKeyMiddleware.Cabecalho, "chave-de-teste");
            var resposta = await cliente.PostAsJsonAsync("/api/gateway/tarefa", new
            {
                codigoTarefa = "T-1",
                tipoTarefa = "PRODUTO"
            });

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.Single(destino.Chamadas);
        }
    }
}
