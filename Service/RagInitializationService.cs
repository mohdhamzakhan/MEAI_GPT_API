using MEAI_GPT_API.Service.Interface;

namespace MEAI_GPT_API.Services
{
    // RagInitializationService.cs - SINGLE RUN ONLY

    public class RagInitializationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RagInitializationService> _logger;

        // ✅ Static flag to prevent multiple runs
        private static int _executionCount = 0;

        public RagInitializationService(
            IServiceScopeFactory scopeFactory,
            ILogger<RagInitializationService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ✅ CRITICAL: Only allow this to run ONCE
            var count = Interlocked.Increment(ref _executionCount);
            if (count > 1)
            {
                _logger.LogWarning("⚠️ RagInitializationService already executed. Skipping. (Count: {Count})", count);
                return;
            }

            try
            {
                _logger.LogInformation("⏳ Waiting 3 seconds for startup...");
                await Task.Delay(3000, stoppingToken);

                _logger.LogInformation("═══════════════════════════════════════");
                _logger.LogInformation("🎯 TRIGGERING RAG INITIALIZATION");
                _logger.LogInformation("═══════════════════════════════════════");

                using (var scope = _scopeFactory.CreateScope())
                {
                    var ragService = scope.ServiceProvider.GetRequiredService<IRAGService>();

                    // This will only initialize once thanks to static flags in DynamicRagService
                    await ragService.InitializeAsync();

                    _logger.LogInformation("═══════════════════════════════════════");
                    _logger.LogInformation("✅ BACKGROUND INIT TRIGGER COMPLETE");
                    _logger.LogInformation("═══════════════════════════════════════");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Initialization cancelled during startup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Background initialization failed: {Message}", ex.Message);
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RagInitializationService stopping");
            return base.StopAsync(cancellationToken);
        }
    }



}