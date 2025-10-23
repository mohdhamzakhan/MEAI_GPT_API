using MEAI_GPT_API.Models;
using MEAI_GPT_API.Services;
using System.Runtime.CompilerServices;
using static MEAI_GPT_API.Services.DynamicRagService;

public interface IRAGService
{
    Task ApplyCorrectionAsync(string sessionId, string question, string correctedAnswer, string model);
    Task<bool> DeleteCorrectionAsync(string id);
    Task<List<ConversationEntry>> GetAppreciatedAnswersAsync(string? topicTag = null);
    Task<List<ModelConfiguration>> GetAvailableModelsAsync();
    Task<ConversationStats> GetConversationStatsAsync();
    Task<List<CorrectionEntry>> GetRecentCorrections(int limit = 50);
    Task<SystemStatus> GetSystemStatusAsync();
    Task InitializeAsync();
    Task<bool> IsHealthy();
    Task LoadHistoricalAppreciatedAnswersAsync();
    Task MarkAppreciatedAsync(string sessionId, string question);
    Task<QueryResponse> ProcessQueryAsync(string question, string plant, string? generationModel = null, string? embeddingModel = null, int maxResults = 15, bool meaiInfo = true, string? sessionId = null, bool useReRanking = true);
    Task ProcessUploadedPolicyAsync(Stream fileStream, string fileName, string model);
    Task RefreshEmbeddingsAsync(string model = "mistral:latest");
    Task SaveCorrectionAsync(string question, string correctAnswer, string model);
    Task SaveCorrectionToDatabase(string sessionId, string question, string correctedAnswer);
    Task DeleteModelDataFromChroma(string modelName);
    Task WarmUpEmbeddingsAsync();
    IAsyncEnumerable<StreamChunk> ProcessQueryStreamAsync(string question, string plant, string? generationModel = null, string? embeddingModel = null, int maxResults = 10, bool meaiInfo = true, string? sessionId = null, bool useReRanking = true, CancellationToken cancellationToken = default);
}