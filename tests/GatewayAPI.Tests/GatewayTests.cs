using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GatewayAPI.Configuracao;
using GatewayAPI.Endpoints;
using GatewayAPI.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

        private static WebApplicationFactory<Program> CriarGateway(DestinoFalso destino, string apiKey = null, string chaveCadastros = null,
            string ipsPermitidos = null, string ambiente = null, string chaveAriba = null)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                if (chaveAriba != null)
                {
                    host.UseSetting(ChavesDestinoHandler.ConfiguracaoAriba, chaveAriba);
                }
                if (ambiente != null)
                {
                    host.UseEnvironment(ambiente);
                }
                if (ipsPermitidos != null)
                {
                    host.UseSetting(IpsPermitidosMiddleware.Configuracao, ipsPermitidos);
                }
                if (chaveCadastros != null)
                {
                    host.UseSetting(ChavesDestinoHandler.ConfiguracaoCadastros, chaveCadastros);
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
        [InlineData("TRANSPORTADORA", "http://localhost:6006/api/transportadoras/cadastrar")]
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
        [InlineData("TRANSPORTADORA", "chave-dos-cadastros")]
        [InlineData("DATASHEET", null)]
        [InlineData("ARIBA", null)]
        public async Task Chave_dos_cadastros_so_e_enviada_para_o_HSE_Cadastros(string tipoTarefa, string chaveEsperada)
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

        [Theory]
        [InlineData("ARIBA", "chave-do-ariba")]
        [InlineData("PRODUTO", "chave-dos-cadastros")]
        [InlineData("DATASHEET", null)]
        public async Task Cada_destino_recebe_so_a_propria_chave(string tipoTarefa, string chaveEsperada)
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, chaveCadastros: "chave-dos-cadastros", chaveAriba: "chave-do-ariba");

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
        public async Task Entrada_do_Ariba_envia_a_chave_do_ariba_e_nao_a_de_quem_chamou()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, chaveAriba: "chave-do-ariba");
            var cliente = ClienteNaPorta(gateway, 5001);
            cliente.DefaultRequestHeaders.Add(ApiKeyMiddleware.Cabecalho, "chave-de-quem-chamou");

            var resposta = await cliente.PostAsJsonAsync("/api/ariba/iniciar", new { codigoTarefa = "T-1" });

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            Assert.Equal("chave-do-ariba", Assert.Single(destino.ChavesRecebidas));
        }

        [Fact]
        public async Task Chave_de_quem_chamou_nao_segue_para_destino_sem_chave()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);
            var cliente = ClienteNaPorta(gateway, 5000);
            cliente.DefaultRequestHeaders.Add(ApiKeyMiddleware.Cabecalho, "chave-de-quem-chamou");

            await cliente.PostAsJsonAsync("/api/gateway/tarefa", new { codigoTarefa = "T-1", tipoTarefa = "DATASHEET" });

            Assert.Null(Assert.Single(destino.ChavesRecebidas));
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

        /// <summary>Faz uma chamada simulando que ela veio do IP informado.</summary>
        private static Task<HttpContext> ChamarDe(WebApplicationFactory<Program> gateway, string ip, string metodo, string caminho, string json = null)
        {
            return gateway.Server.SendAsync(contexto =>
            {
                contexto.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                contexto.Request.Method = metodo;
                contexto.Request.Host = new HostString("localhost", 5000);
                contexto.Request.Path = caminho;
                if (json != null)
                {
                    contexto.Request.ContentType = "application/json";
                    contexto.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
                }
            });
        }

        private const string TarefaProduto = "{\"codigoTarefa\":\"T-1\",\"tipoTarefa\":\"PRODUTO\",\"dados\":{}}";

        [Theory]
        [InlineData("192.168.1.250")]
        [InlineData("::ffff:192.168.1.250")]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        public async Task Ips_permitidos_e_localhost_passam(string ip)
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, ipsPermitidos: "192.168.1.250");

            var contexto = await ChamarDe(gateway, ip, "POST", "/api/gateway/tarefa", TarefaProduto);

            Assert.Equal(StatusCodes.Status200OK, contexto.Response.StatusCode);
            Assert.Single(destino.Chamadas);
        }

        [Fact]
        public async Task Ip_fora_da_lista_recebe_403_e_nada_e_encaminhado()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino, ipsPermitidos: "192.168.1.250");

            var contexto = await ChamarDe(gateway, "192.168.1.77", "POST", "/api/gateway/tarefa", TarefaProduto);

            Assert.Equal(StatusCodes.Status403Forbidden, contexto.Response.StatusCode);
            Assert.Empty(destino.Chamadas);
        }

        [Fact]
        public async Task Ip_fora_da_lista_ainda_ve_o_health()
        {
            using var gateway = CriarGateway(new DestinoFalso(), ipsPermitidos: "192.168.1.250");

            var contexto = await ChamarDe(gateway, "192.168.1.77", "GET", "/health");

            Assert.Equal(StatusCodes.Status200OK, contexto.Response.StatusCode);
        }

        [Fact]
        public async Task Sem_lista_configurada_qualquer_ip_passa()
        {
            var destino = new DestinoFalso();
            using var gateway = CriarGateway(destino);

            var contexto = await ChamarDe(gateway, "192.168.1.77", "POST", "/api/gateway/tarefa", TarefaProduto);

            Assert.Equal(StatusCodes.Status200OK, contexto.Response.StatusCode);
        }

        [Fact]
        public async Task Swagger_fica_desligado_em_producao()
        {
            using var gateway = CriarGateway(new DestinoFalso(), ambiente: "Production");

            var resposta = await ClienteNaPorta(gateway, 5000).GetAsync("/swagger/v1/swagger.json");

            Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        }

        [Fact]
        public async Task Swagger_aparece_em_development()
        {
            using var gateway = CriarGateway(new DestinoFalso(), ambiente: "Development");

            var resposta = await ClienteNaPorta(gateway, 5000).GetAsync("/swagger/v1/swagger.json");

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
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
