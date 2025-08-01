using MEAI_GPT_API.Models;

public interface IRAGService
{
    Task InitializeAsync();
    Task<QueryResponse> ProcessQueryAsync(
    string question,
    string plant,
    string? generationModel = null,
    string? embeddingModel = null,
    int maxResults = 15,
    bool meaiInfo = true,
    string? sessionId = null,
    bool useReRanking = true
);

    Task<bool> IsHealthy();
    //Task SaveCorrectionAsync(string question, string correctAnswer, string model);
    //SystemStatus GetSystemStatus();
    //Task<bool> RefreshEmbeddingsAsync(string model = "default");
    //List<CorrectionEntry> GetRecentCorrections(int limit);
    //Task<bool> DeleteCorrectionAsync(string id);
    //Task ProcessUploadedPolicyAsync(Stream fileStream, string fileName, string model);

    Task<List<ModelConfiguration>> GetAvailableModelsAsync();
    Task MarkAppreciatedAsync(string sessionId, string question);
    Task ApplyCorrectionAsync(string sessionId,string question, string correctedAnswer, string model);
}