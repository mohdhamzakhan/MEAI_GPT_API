using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Caching.StackExchangeRedis;
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
    client.BaseAddress = new Uri(builder.Configuration["ChromaDB:BaseUrl"]);
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpClient("OllamaAPI", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]);
    client.Timeout = TimeSpan.FromMinutes(10);
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
                        ["timestamp"] = "2025-07-25 05:49:50",
                        ["user"] = "mohdhamzakhan"
                    }))
            .AddMeter("MEAI.RAG.Metrics")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddPrometheusExporter();
    });


builder.Services.AddSingleton<IDocumentProcessor, DocumentProcessor>();
builder.Services.AddSingleton<ICacheManager, CacheManager>();
builder.Services.AddSingleton<IMetricsCollector, MetricsCollector>();
builder.Services.AddScoped<IRAGService, RagService>();


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