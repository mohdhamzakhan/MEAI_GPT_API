

namespace MEAI_GPT_API.Models
{
    //public record EmbeddingData(string Text, List<float> Vector, string SourceFile, DateTime LastModified);

    public class QueryRequest
    {
        public string Question { get; set; } = "";
        public int MaxResults { get; set; } = 50;
        public string model { get; set; }
        public bool meai_info { get; set; } =true;
        public string sessionId { get; set; }

        public string? GenerationModel { get; set; }  // Replaces old 'model' parameter
        public string? EmbeddingModel { get; set; }   // New parameter
        public bool? useReRanking { get; set; } = true;
    }

    public record EmbeddingData(string Text, List<float> Vector, string SourceFile, DateTime LastModified, string model)
    {
        public double Similarity { get; set; }
    }
}
