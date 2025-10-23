using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace MEAI_GPT_API.Services
{
    public class InitializationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public ModelConfiguration? GenerationModel { get; set; }
        public ModelConfiguration? EmbeddingModel { get; set; }
        public string UserId { get; set; } = string.Empty; // Use string instead of SessionInfo

        public static InitializationResult CreateSuccess(
            string sessionId,
            string userId,
            ModelConfiguration genModel,
            ModelConfiguration embModel)
        {
            return new InitializationResult
            {
                Success = true,
                SessionId = sessionId,
                UserId = userId,
                GenerationModel = genModel,
                EmbeddingModel = embModel
            };
        }

        public static InitializationResult CreateFailure(string error)
        {
            return new InitializationResult
            {
                Success = false,
                ErrorMessage = error
            };
        }
    }

    public class InitializationProgressUpdate
    {
        public string Phase { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Progress { get; set; }
        public bool IsComplete { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? ErrorMessage { get; set; }
    }

    public class DynamicRAGInitializationService
    {
        private readonly IRAGService _ragService;
        private readonly IModelManager _modelManager;
        private readonly IConversationStorageService _conversationStorage;
        private readonly Conversation _conversation;
        private readonly DynamicRAGConfiguration _config;
        private readonly ILogger<DynamicRAGInitializationService> _logger;

        private readonly List<InitializationProgressUpdate> _progressUpdates = new();
        private readonly object _progressLock = new object();

        public DynamicRAGInitializationService(
            IRAGService ragService,
            IModelManager modelManager,
            IConversationStorageService conversationStorage,
            Conversation conversation,
            IOptions<DynamicRAGConfiguration> config,
            ILogger<DynamicRAGInitializationService> logger)
        {
            _ragService = ragService;
            _modelManager = modelManager;
            _conversationStorage = conversationStorage;
            _conversation = conversation;
            _config = config.Value;
            _logger = logger;
        }

        public async Task<InitializationResult> InitializeUserSessionAsync(string userId, string? sessionId = null)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"🚀 Starting user session initialization for user: {userId}");
                AddProgressUpdate("Starting", "Initializing user session...", 0);

                // Step 1: Ensure RAG system is initialized
                if (!await IsSystemReadyAsync())
                {
                    AddProgressUpdate("System Check", "RAG system not ready, initializing...", 10);
                    await _ragService.InitializeAsync();
                }
                AddProgressUpdate("System Check", "RAG system ready", 20);

                // Step 2: Get or create session
                var finalSessionId = sessionId ?? Guid.NewGuid().ToString();
                AddProgressUpdate("Session", "Creating user session...", 30);

                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(finalSessionId, userId);
                AddProgressUpdate("Session", "User session created", 40);

                // Step 3: Initialize conversation context
                AddProgressUpdate("Context", "Setting up conversation context...", 50);
                var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);
                AddProgressUpdate("Context", "Conversation context ready", 60);

                // Step 4: Validate and get models
                AddProgressUpdate("Models", "Loading AI models...", 70);
                var (genModel, embModel) = await GetValidatedModelsAsync();
                AddProgressUpdate("Models", "AI models loaded", 80);

                // Step 5: Pre-warm embeddings if needed
                AddProgressUpdate("Optimization", "Optimizing system for user...", 90);
                await PreWarmUserContextAsync(userId, finalSessionId);
                AddProgressUpdate("Complete", "Initialization complete", 100, true);

                stopwatch.Stop();
                _logger.LogInformation($"✅ User session initialized successfully in {stopwatch.ElapsedMilliseconds}ms");

                return InitializationResult.CreateSuccess(finalSessionId, userId, genModel, embModel);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, $"❌ Failed to initialize user session for {userId}");
                AddProgressUpdate("Error", $"Initialization failed: {ex.Message}", 0, false, ex.Message);
                return InitializationResult.CreateFailure(ex.Message);
            }
        }

        public async Task<List<InitializationProgressUpdate>> GetInitializationProgressAsync()
        {
            // Return progress updates without yield (avoiding try-catch with yield issue)
            lock (_progressLock)
            {
                return new List<InitializationProgressUpdate>(_progressUpdates);
            }
        }

        public async Task<bool> IsSystemReadyAsync()
        {
            try
            {
                // Check if RAG service is initialized
                if (!await _ragService.IsHealthy())
                {
                    return false;
                }

                // Check if models are available
                var models = await _ragService.GetAvailableModelsAsync();
                var hasEmbeddingModel = models.Any(m => m.Type == "embedding" || m.Type == "both");
                var hasGenerationModel = models.Any(m => m.Type == "generation" || m.Type == "both");

                // Check system health
                var isHealthy = await _ragService.IsHealthy();

                return hasEmbeddingModel && hasGenerationModel && isHealthy;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check system readiness");
                return false;
            }
        }

        public async Task<InitializationResult> EnsureUserContextAsync(string userId, string sessionId)
        {
            try
            {
                _logger.LogInformation($"🔧 Ensuring user context for {userId}, session {sessionId}");

                // Get or create session
                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(sessionId, userId);

                // Ensure conversation context exists
                var context = _conversation.GetOrCreateConversationContext(sessionId);

                // Get validated models
                var (genModel, embModel) = await GetValidatedModelsAsync();

                _logger.LogInformation($"✅ User context ensured for {userId}");
                return InitializationResult.CreateSuccess(sessionId, userId, genModel, embModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to ensure user context for {userId}");
                return InitializationResult.CreateFailure(ex.Message);
            }
        }

        private async Task<(ModelConfiguration GenerationModel, ModelConfiguration EmbeddingModel)> GetValidatedModelsAsync()
        {
            var generationModelName = _config.DefaultGenerationModel;
            var embeddingModelName = _config.DefaultEmbeddingModel;

            if (string.IsNullOrEmpty(generationModelName) || string.IsNullOrEmpty(embeddingModelName))
            {
                // Auto-configure if not set
                var availableModels = await _ragService.GetAvailableModelsAsync();

                if (string.IsNullOrEmpty(generationModelName))
                {
                    var genModel = availableModels.FirstOrDefault(m => m.Type == "generation" || m.Type == "both");
                    generationModelName = genModel?.Name ?? throw new InvalidOperationException("No generation model available");
                }

                if (string.IsNullOrEmpty(embeddingModelName))
                {
                    var embModel = availableModels.FirstOrDefault(m => m.Type == "embedding" || m.Type == "both");
                    embeddingModelName = embModel?.Name ?? throw new InvalidOperationException("No embedding model available");
                }
            }

            var generationModel = await _modelManager.GetModelAsync(generationModelName)
                ?? throw new InvalidOperationException($"Generation model {generationModelName} not found");

            var embeddingModel = await _modelManager.GetModelAsync(embeddingModelName)
                ?? throw new InvalidOperationException($"Embedding model {embeddingModelName} not found");

            return (generationModel, embeddingModel);
        }

        private async Task PreWarmUserContextAsync(string userId, string sessionId)
        {
            try
            {
                // Load user's recent conversations for context
                var recentConversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);

                // Warm up models if needed
                await _ragService.WarmUpEmbeddingsAsync();

                _logger.LogDebug($"Pre-warmed context for user {userId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to pre-warm context for user {userId}");
                // Don't throw - this is optimization, not critical
            }
        }

        private void AddProgressUpdate(string phase, string message, int progress, bool isComplete = false, string? errorMessage = null)
        {
            lock (_progressLock)
            {
                _progressUpdates.Add(new InitializationProgressUpdate
                {
                    Phase = phase,
                    Message = message,
                    Progress = progress,
                    IsComplete = isComplete,
                    ErrorMessage = errorMessage,
                    Timestamp = DateTime.UtcNow
                });

                // Keep only last 50 updates to prevent memory issues
                if (_progressUpdates.Count > 50)
                {
                    _progressUpdates.RemoveRange(0, _progressUpdates.Count - 50);
                }
            }

            _logger.LogInformation($"📊 {phase}: {message} ({progress}%)");
        }
    }
}
