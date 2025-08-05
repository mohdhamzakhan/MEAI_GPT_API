using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
using MEAI_GPT_API.Data;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

//Add By Hamza
builder.Services.Configure<ChromaDbOptions>(
    builder.Configuration.GetSection("ChromaDB"));

builder.Services.AddHttpClient("ChromaDB", client =>
{
    var baseUrl = builder.Configuration["ChromaDB:BaseUrl"] ?? "http://localhost:8000";
    var timeoutMinutes = builder.Configuration.GetValue<int>("ChromaDB:TimeoutMinutes", 5);

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
});

builder.Services.AddHttpClient("OllamaAPI", client =>
{
    var baseUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
    var timeoutMinutes = builder.Configuration.GetValue<int>("Ollama:TimeoutMinutes", 10);

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
});

// Add Redis Cache with fallback to in-memory cache
if (builder.Configuration.GetConnectionString("Redis") != null)
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration.GetConnectionString("Redis");
        options.InstanceName = "MEAI_RAG_";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Add OpenTelemetry
// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService("MEAI_RAG_API")
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["environment"] = builder.Environment.EnvironmentName,
                        ["timestamp"] = DateTime.Now.ToString("O"),
                        ["user"] = Environment.UserName,
                        ["version"] = Environment.Version.ToString(),
                        ["machineName"] = Environment.MachineName
                    }))
            .AddMeter("MEAI.RAG.Metrics")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddPrometheusExporter();
    });

builder.Services.AddDbContext<ConversationDbContext>(options =>
    options.UseSqlite("Data Source=conversations.db"));
builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>();
builder.Services.AddSingleton<ICacheManager, CacheManager>();
builder.Services.AddSingleton<IMetricsCollector, MetricsCollector>();

// FIXED: Keep RAGService as scoped
builder.Services.AddScoped<IRAGService, DynamicRagService>();
builder.Services.Configure<DynamicRAGConfiguration>(
    builder.Configuration.GetSection("DynamicRAG"));
builder.Services.AddScoped<IConversationStorageService, ConversationStorageService>();

// FIXED: Modified hosted service to use IServiceScopeFactory
builder.Services.AddHostedService<RagInitializationService>();

builder.Services.AddSingleton<Conversation>();

// Register the service
builder.Services.AddSingleton<AbbreviationExpansionService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<AbbreviationExpansionService>>();
    var abbreviationPath = Path.Combine("context", "abbreviations.txt");
    return new AbbreviationExpansionService(logger, abbreviationPath);
});
builder.Services.Configure<PlantSettings>(options =>
{
    options.Plants = builder.Configuration.GetSection("Plant").Get<Dictionary<string, string>>()!;
});

// Keep ModelManager as scoped
builder.Services.AddScoped<IModelManager>(provider =>
{
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("OllamaAPI");
    var logger = provider.GetRequiredService<ILogger<ModelManager>>();
    var configOptions = provider.GetRequiredService<IOptions<DynamicRAGConfiguration>>();

    return new ModelManager(httpClient, logger, configOptions);
});

builder.Services.AddSingleton<DynamicCollectionManager>(provider =>
{
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    var chromaClient = httpClientFactory.CreateClient("ChromaDB");
    var chromaOptions = provider.GetRequiredService<IOptions<ChromaDbOptions>>().Value;
    var logger = provider.GetRequiredService<ILogger<DynamicCollectionManager>>();
    return new DynamicCollectionManager(chromaClient, chromaOptions, logger);
});


builder.Host.UseSerilog((context, config) =>
{
    config
        .MinimumLevel.Information()
        .WriteTo.File("Logs/server-log.txt", rollingInterval: RollingInterval.Day);
});

builder.Services.Configure<ChromaDbOptions>(
    builder.Configuration.GetSection("ChromaDB"));

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ConversationDbContext>();
    context.Database.EnsureCreated();
}

// FIXED: This manual initialization is now handled by the hosted service
// Remove this manual initialization since RagInitializationService will handle it
// using (var scope = app.Services.CreateScope())
// {
//     var ragService = scope.ServiceProvider.GetRequiredService<IRAGService>();
//     await ragService.InitializeAsync();
// }

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Add Prometheus metrics endpoint
app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.UseCors("AllowAll");
app.UseRouting();
app.MapControllers();
app.UseMetrics();

app.Run();