// Models/ConversationModels.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace MEAI_GPT_API.Models
{
    public class ConversationEntry
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string SessionId { get; set; } = string.Empty;

        [Required]
        public string Question { get; set; } = string.Empty;

        [Required]
        public string Answer { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Vector data for semantic search
        public string QuestionEmbeddingJson { get; set; } = string.Empty;
        public string AnswerEmbeddingJson { get; set; } = string.Empty;

        // Enhanced metadata
        public string NamedEntitiesJson { get; set; } = "[]";
        public bool WasAppreciated { get; set; } = false;
        public string? CorrectedAnswer { get; set; }
        public string? TopicTag { get; set; }
        public int? FollowUpToId { get; set; }
        public string? Plant { get; set; }

        // Model information
        [MaxLength(100)]
        public string GenerationModel { get; set; } = string.Empty;

        [MaxLength(100)]
        public string EmbeddingModel { get; set; } = string.Empty;

        // Confidence and performance metrics
        public double Confidence { get; set; }
        public long ProcessingTimeMs { get; set; }
        public int RelevantChunksCount { get; set; }

        // Additional context
        public string SourcesJson { get; set; } = "[]";
        public bool IsFromCorrection { get; set; } = false;

        // Navigation properties
        [ForeignKey("FollowUpToId")]
        public virtual ConversationEntry? ParentConversation { get; set; }

        public virtual ICollection<ConversationEntry> FollowUps { get; set; } = new List<ConversationEntry>();

        // Helper methods for JSON serialization
        [NotMapped]
        public List<float> QuestionEmbedding
        {
            get => string.IsNullOrEmpty(QuestionEmbeddingJson)
                ? new List<float>()
                : JsonSerializer.Deserialize<List<float>>(QuestionEmbeddingJson) ?? new List<float>();
            set => QuestionEmbeddingJson = JsonSerializer.Serialize(value);
        }

        [NotMapped]
        public List<float> AnswerEmbedding
        {
            get => string.IsNullOrEmpty(AnswerEmbeddingJson)
                ? new List<float>()
                : JsonSerializer.Deserialize<List<float>>(AnswerEmbeddingJson) ?? new List<float>();
            set => AnswerEmbeddingJson = JsonSerializer.Serialize(value);
        }

        [NotMapped]
        public List<string> NamedEntities
        {
            get => string.IsNullOrEmpty(NamedEntitiesJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(NamedEntitiesJson) ?? new List<string>();
            set => NamedEntitiesJson = JsonSerializer.Serialize(value);
        }

        [NotMapped]
        public List<string> Sources
        {
            get => string.IsNullOrEmpty(SourcesJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(SourcesJson) ?? new List<string>();
            set => SourcesJson = JsonSerializer.Serialize(value);
        }
    }

    public class ConversationSession
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string SessionId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

        public int ConversationCount { get; set; } = 0;
        public string? LastTopicTag { get; set; }
        public string LastTopicAnchor { get; set; } = string.Empty;

        // User context
        [MaxLength(50)]
        public string? UserId { get; set; }

        [MaxLength(100)]
        public string? UserPlant { get; set; }

        // Session metadata
        public string MetadataJson { get; set; } = "{}";

        // Navigation property
        public virtual ICollection<ConversationEntry> Conversations { get; set; } = new List<ConversationEntry>();

        [NotMapped]
        public Dictionary<string, object> Metadata
        {
            get => string.IsNullOrEmpty(MetadataJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(MetadataJson) ?? new Dictionary<string, object>();
            set => MetadataJson = JsonSerializer.Serialize(value);
        }
    }

    public class ConversationSearchResult
    {
        public ConversationEntry Entry { get; set; } = null!;
        public double Similarity { get; set; }
        public string MatchType { get; set; } = string.Empty; // "question", "answer", "both"
    }

    public class ConversationStats
    {
        public int TotalConversations { get; set; }
        public int TotalSessions { get; set; }
        public int AppreciatedAnswers { get; set; }
        public int CorrectedAnswers { get; set; }
        public Dictionary<string, int> TopicDistribution { get; set; } = new();
        public Dictionary<string, int> ModelUsage { get; set; } = new();
        public double AverageConfidence { get; set; }
        public long AverageProcessingTime { get; set; }
        public DateTime OldestConversation { get; set; }
        public DateTime NewestConversation { get; set; }
    }
}