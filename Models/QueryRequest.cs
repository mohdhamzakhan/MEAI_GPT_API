namespace MEAI_GPT_API.Models
{
    //public record EmbeddingData(string Text, List<float> Vector, string SourceFile, DateTime LastModified);

    public class QueryRequest
    {
        public string Question { get; set; } = "";
        public int MaxResults { get; set; } = 50;
        public string model { get; set; }
        public bool meai_info { get; set; } = true;
        public string sessionId { get; set; }

        public string? GenerationModel { get; set; }  // Replaces old 'model' parameter
        public string? EmbeddingModel { get; set; }   // New parameter
        public bool? useReRanking { get; set; } = true;
        public string Plant { get; set; }
        public string UserId { get; set; }

        // ✅ NEW: previously set in the frontend Settings modal but never
        // sent to the backend — persona shapes the system prompt's tone,
        // temperature overrides the per-model sampling default.
        public string? Persona { get; set; }
        public double? Temperature { get; set; }

    }

    public record EmbeddingData(string Text, List<float> Vector, string SourceFile, DateTime LastModified, string model)
    {
        public double Similarity { get; set; }
    }
}