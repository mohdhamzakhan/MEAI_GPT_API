using MEAI_GPT_API.Models;

public interface IRAGService
{
    Task InitializeAsync();
    Task<QueryResponse> ProcessQueryAsync(
        string question,
        string generationModel,
        int maxResults = 10,
        bool meaiInfo = true,
        string? sessionId = null,
        bool useReRanking = true,
        string? embeddingModel = null);
    Task<bool> IsHealthy();
}