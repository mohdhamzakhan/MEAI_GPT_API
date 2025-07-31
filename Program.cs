using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Services;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<RagService>();
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


builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>();
builder.Services.AddSingleton<ICacheManager, CacheManager>();
builder.Services.AddSingleton<IMetricsCollector, MetricsCollector>();
builder.Services.AddScoped<IRAGService, DynamicRagService>();
builder.Services.Configure<DynamicRAGConfiguration>(
    builder.Configuration.GetSection("DynamicRAG"));

builder.Services.AddSingleton<Conversation>();
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
    return new DynamicCollectionManager(chromaClient, chromaOptions, logger); // ✅ fixed
});
builder.Services.Configure<ChromaDbOptions>(
    builder.Configuration.GetSection("ChromaDB"));


var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var ragService = scope.ServiceProvider.GetRequiredService<IRAGService>();
    await ragService.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
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