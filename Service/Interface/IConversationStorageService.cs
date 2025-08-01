using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Service.Interface
{
    public interface IConversationStorageService
    {
        Task SaveConversationAsync(ConversationEntry entry);
        Task<ConversationEntry?> GetConversationAsync(int id);
        Task<List<ConversationEntry>> GetSessionConversationsAsync(string sessionId, int limit = 50);
        Task<ConversationSession> GetOrCreateSessionAsync(string sessionId, string? userId = null, string? plant = null);
        Task UpdateSessionAsync(ConversationSession session);
        Task MarkAsAppreciatedAsync(int conversationId);
        Task SaveCorrectionAsync(int conversationId, string correctedAnswer);
        Task<List<ConversationSearchResult>> SearchSimilarConversationsAsync(List<float> queryEmbedding,string plant, double threshold = 0.7, int limit = 10);
        Task<List<ConversationEntry>> GetAppreciatedAnswersAsync(string? topicTag = null, int limit = 100);
        Task<ConversationStats> GetConversationStatsAsync();
        Task CleanupOldSessionsAsync(TimeSpan maxAge);
        Task<List<ConversationEntry>> GetFollowUpChainAsync(int parentId);
        Task AssignTopicTagAsync(int conversationId, string topicTag);
        Task<Dictionary<string, List<ConversationEntry>>> GroupConversationsByTopicAsync(string sessionId);
    }
}
