namespace MEAI_GPT_API.Models
{
    public class QueryResponse
    {
        public string Answer { get; set; }
        public bool IsFromCorrection { get; set; }
        public List<string> Sources { get; set; }
        public double Confidence { get; set; }
        public long ProcessingTimeMs { get; set; }
        public List<RelevantChunk> RelevantChunks { get; set; }
        public bool IsFollowUp { get; set; } // New
        public string ContextUsed { get; set; } // New
        public string? SessionId { get; set; } // Add this 
        public Dictionary<string, string> ModelsUsed { get; set; } = new();
        public string Plant { get; set; }
        public bool HasSufficientPolicyCoverage { get; set; } = true;
    }
}
