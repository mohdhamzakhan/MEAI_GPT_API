using System.Collections.Concurrent;

namespace MEAI_GPT_API.Models
{
    public class Conversation
    {
        private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();

        public class ConversationContext
        {
            public List<List<ConversationTurn>> TopicHistory { get; set; } = new();
            public List<ConversationTurn> CurrentTopic { get; set; } = new();//
            public List<EmbeddingData> RelevantChunks { get; set; } = new();
            public List<ConversationTurn> History { get; set; } = new();
            public DateTime LastAccessed { get; set; }
            public string SessionId { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; } = DateTime.Now;
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
