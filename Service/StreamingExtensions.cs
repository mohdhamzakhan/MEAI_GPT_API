namespace MEAI_GPT_API.Service
{
    public class StreamingExtensions
    {
    }
    public class SectionQuery
    {
        public string SectionNumber { get; set; } = "";
        public string DocumentType { get; set; } = "";
        public string OriginalQuery { get; set; } = "";
    }
    // Supporting classes for diagnostics
    public class DiagnosticInfo
    {
        public string Question { get; set; } = "";
        public string Plant { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public int PolicyFilesFound { get; set; }
        public List<string> PolicyFiles { get; set; } = new();
        public string EmbeddingModel { get; set; } = "";
        public string CollectionId { get; set; } = "";
        public int TotalEmbeddings { get; set; }
        public int ChunksFound { get; set; }
        public List<ChunkDiagnostic> ChunkDetails { get; set; } = new();
        public bool HasSufficientCoverage { get; set; }
        public string? Error { get; set; }
    }
    public class ChunkDiagnostic
    {
        public string Source { get; set; } = "";
        public double Similarity { get; set; }
        public string TextPreview { get; set; } = "";
    }
    public class NonMeaiConversationStats
    {
        public int TotalNonMeaiConversations { get; set; }
        public int NonMeaiCorrections { get; set; }
        public int NonMeaiAppreciated { get; set; }
        public double AverageProcessingTime { get; set; }
        public Dictionary<string, int> TopGeneralTopics { get; set; } = new();
    }
}
