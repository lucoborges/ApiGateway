using GatewayAPI.Configuracao;
using GatewayAPI.Endpoints;
using GatewayAPI.Interfaces;
using GatewayAPI.Middleware;
using GatewayAPI.Models;
using GatewayAPI.Services;
using Microsoft.AspNetCore.Http.Json;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;
using System.Text.Json;
using System.Text.Json.Serialization;

var iniciadoEm = DateTime.Now;

// Em produção o gateway roda como serviço do Windows ("GatewayAPI"); fora dele, roda como console normal
var emServico = WindowsServiceHelpers.IsWindowsService();
if (!emServico)
{
    try { Console.Title = "GATEWAY - ROTEADOR DE PROGRAMAS"; } catch { /* sem console (ex.: testes, CI) */ }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Um serviço do Windows inicia em System32; a configuração fica ao lado do executável
    ContentRootPath = emServico ? AppContext.BaseDirectory : null
});
builder.Host.UseWindowsService(opcoes => opcoes.ServiceName = "GatewayAPI");

// Log em arquivo com rotação diária (30 dias). Como serviço, a pasta fica fora da pasta do app,
// para sobreviver às trocas de versão do deploy.
var pastaLogs = builder.Configuration.GetValue<string>("Gateway:Logs:Pasta");
if (string.IsNullOrWhiteSpace(pastaLogs))
{
    pastaLogs = emServico
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"))
        : Path.Combine(builder.Environment.ContentRootPath, "logs");
}
builder.Host.UseSerilog((contexto, log) => log
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(pastaLogs, "gateway-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true));

// Endereços dos programas de destino (Gateway:Servicos no appsettings.json)
var servicos = new ServicosDestino(builder.Configuration);
builder.Services.AddSingleton(servicos);

// Entrada dedicada ao AribaSourcing (origem -> localhost:5001 -> gateway -> AribaSourcing em localhost:6005)
var aribaEntradaUrl = builder.Configuration.GetValue<string>("Gateway:Ariba:EntradaUrl") ?? AribaEndpoints.EntradaUrlPadrao;
var aribaServicoUrl = servicos.Ariba;

builder.WebHost.UseUrls("http://0.0.0.0:5000", aribaEntradaUrl);

// 1. Configurar JSON serialization
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// 2. Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// 3. Learn more about configuring Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "API Gateway",
        Version = "v1",
        Description = "Gateway centralizado para comunicação com múltiplos programas"
    });
});
static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(int timeoutMinutes = 60)
{
    return Policy.TimeoutAsync<HttpResponseMessage>(
        TimeSpan.FromMinutes(timeoutMinutes),
        TimeoutStrategy.Pessimistic,
        onTimeoutAsync: (context, timespan, task) =>
        {
            Console.WriteLine($"⏰ Timeout após {timespan.TotalMinutes} minutos");
            return Task.CompletedTask;
        }
    );
}

// 4. HTTP Client Configuration
// Toda chamada para o HSE-Cadastros (produtos, fornecedores, clientes) leva a chave Gateway:ChaveCadastros
builder.Services.AddTransient<ChaveCadastrosHandler>();
builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<ChaveCadastrosHandler>());
var timeoutMinutes = builder.Configuration.GetValue<int>("Gateway:Timeout:HttpClientTimeoutMinutes", 60);

builder.Services.AddHttpClient("GatewayClient", client =>
{
    client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
    client.DefaultRequestHeaders.Add("User-Agent", "GatewayAPI/1.0");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(GetCircuitBreakerPolicy())
.AddPolicyHandler(GetTimeoutPolicy(timeoutMinutes));

builder.Services.AddHttpClient("ProdutoService", client =>
{
    client.BaseAddress = new Uri(servicos.Produto + "/");
    client.Timeout = TimeSpan.FromSeconds(6000);
});
builder.Services.AddHttpClient("FornecedorService", client =>
{
    client.BaseAddress = new Uri(servicos.Fornecedor + "/");
    client.Timeout = TimeSpan.FromSeconds(6000);
});
builder.Services.AddHttpClient("ClienteService", client =>
{
    client.BaseAddress = new Uri(servicos.Cliente + "/");
    client.Timeout = TimeSpan.FromSeconds(6000);
});
builder.Services.AddHttpClient("DatasheetService", client =>
{
    client.BaseAddress = new Uri(servicos.Datasheet + "/");
    client.Timeout = TimeSpan.FromSeconds(6000); 
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(GetCircuitBreakerPolicy());
// Cliente usado por /api/gateway/tarefa: as tarefas podem levar até 60 minutos
builder.Services.AddHttpClient(ServicosDestino.ClienteHttpTarefa, client =>
{
    client.Timeout = TimeSpan.FromMinutes(60);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "GatewayAPI/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    ConnectTimeout = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    MaxConnectionsPerServer = 20
});
// O AribaSourcing responde na hora (o processamento segue em segundo plano), então o timeout é curto
builder.Services.AddHttpClient(AribaEndpoints.NomeHttpClient, client =>
{
    client.BaseAddress = new Uri(aribaServicoUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "GatewayAPI/1.0");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// 5. Registrar serviços do Gateway
builder.Services.AddSingleton<IRouteRegistry, RouteRegistry>();
builder.Services.AddScoped<IServiceRouter, ServiceRouter>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITrackingService, TrackingService>();

var app = builder.Build();

// 6. Configure the HTTP request pipeline
// O Swagger fica disponível também em produção, a menos que Gateway:Swagger seja false
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Gateway:Swagger", true))
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "API Gateway v1");
        c.RoutePrefix = "swagger";
    });
}
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Exige o cabeçalho X-Api-Key apenas quando Gateway:ApiKey está configurada
app.UseMiddleware<ApiKeyMiddleware>();

app.UseRouting();
app.UseAuthorization();
app.MapControllers();

// Rota de saúde: usada pelo deploy para confirmar que a versão nova subiu
var arquivoVersao = Path.Combine(AppContext.BaseDirectory, "versao.txt");
var versao = File.Exists(arquivoVersao) ? File.ReadAllText(arquivoVersao).Trim() : "dev";
app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    servico = "GatewayAPI",
    versao,
    ambiente = app.Environment.EnvironmentName,
    iniciadoEm
}));
app.MapAribaEndpoints(aribaEntradaUrl);

// 7. ⭐⭐ CONFIGURAR ROTAS CORRETAMENTE ⭐⭐
try
{
    using (var scope = app.Services.CreateScope())
    {
        var routeRegistry = scope.ServiceProvider.GetRequiredService<IRouteRegistry>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        // ⭐⭐ REGISTRO CORRETO DAS ROTAS DO PROGRAMA DE PRODUTOS ⭐⭐
        // O ProgramaProdutos roda na porta 6001 com múltiplos endpoints
        routeRegistry.RegisterService("ProgramaProdutos", servicos.Produto, "/{endpoint}");
        routeRegistry.RegisterService("ProgramaClientes", servicos.Cliente, "/{endpoint}");
        // Log das rotas registradas
        logger.LogInformation("\n📋 Resumo de rotas registradas:");
        foreach (var service in routeRegistry.GetAllServices())
        {
            logger.LogInformation("   {ServiceName}: {PathTemplate} -> {BaseUrl}",
                service.ServiceName, service.PathTemplate, service.BaseUrl);
        }
    }

    Console.WriteLine("\n✅ API Gateway iniciado com sucesso!");
    Console.WriteLine("📍 Escutando em: http://0.0.0.0:5000 (todas as interfaces desta máquina)");
    Console.WriteLine("📚 Swagger: /swagger | Saúde: /health");
    Console.WriteLine($"📄 Logs em: {pastaLogs}");
    Console.WriteLine($"📍 Entrada Ariba: {aribaEntradaUrl}/api/ariba/iniciar -> {aribaServicoUrl}");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Erro ao configurar rotas: {ex.Message}");
    throw;
}

// Os endereços vêm do UseUrls acima (porta 5000 + entrada do Ariba); Run(url) descartaria a segunda entrada
app.Run();

// ⭐⭐ POLICIES PARA RESILIÊNCIA ⭐⭐
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.NotFound)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                Console.WriteLine($"🔄 Tentativa {retryAttempt} após {timespan.TotalSeconds}s");
            });
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, timespan) =>
            {
                Console.WriteLine($"🔴 Circuito aberto por {timespan.TotalSeconds}s");
            },
            onReset: () =>
            {
                Console.WriteLine("🟢 Circuito fechado");
            });
}

// Permite que os testes automatizados subam a aplicação em memória (WebApplicationFactory<Program>)
public partial class Program { }
