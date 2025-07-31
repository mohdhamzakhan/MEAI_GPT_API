using System.Collections.Concurrent;

namespace MEAI_GPT_API.Models
{
    public class Conversation
    {
        private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();

        public ConversationContext GetOrCreateConversationContext(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = $"temp_{Guid.NewGuid():N}";
            }

            var context = _sessionContexts.GetOrAdd(sessionId, _ => new ConversationContext
            {
                SessionId = sessionId,
                CreatedAt = DateTime.Now,
                LastAccessed = DateTime.Now
            });

            context.LastAccessed = DateTime.Now;
            return context;
        }

        public class ConversationContext
        {
            public List<List<ConversationTurn>> TopicHistory { get; set; } = new();
            public List<ConversationTurn> CurrentTopic { get; set; } = new();//
            public List<EmbeddingData> RelevantChunks { get; set; } = new();
            public List<ConversationTurn> History { get; set; } = new();
            public List<string> NamedEntities { get; set; } = new(); // NEW
            public DateTime LastAccessed { get; set; }
            public string SessionId { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public string LastTopicAnchor { get; set; } = ""; // 🆕 track root of current topic
        }

        public class ConversationTurn
        {
            public string Question { get; set; }
            public string Answer { get; set; }
            public DateTime Timestamp { get; set; }
            public List<string> Sources { get; set; }
            public string SessionId { get; set; }
        }
    }
}
