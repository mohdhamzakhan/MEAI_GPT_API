using MEAI_GPT_API.Service.Interface;

namespace MEAI_GPT_API.Services
{
    public class RagInitializationService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RagInitializationService> _logger;

        public RagInitializationService(
            IServiceScopeFactory scopeFactory,
            ILogger<RagInitializationService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Starting RAG initialization...");

                using var scope = _scopeFactory.CreateScope();
                var ragService = scope.ServiceProvider.GetRequiredService<IRAGService>();

                await ragService.InitializeAsync();

                _logger.LogInformation("RAG initialization completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize RAG service: {Message}", ex.Message);
                throw; // Re-throw to prevent the application from starting if initialization fails
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RAG initialization service is stopping.");
            return Task.CompletedTask;
        }
    }
}