

namespace MEAI_GPT_API.Models
{
    //public record EmbeddingData(string Text, List<float> Vector, string SourceFile, DateTime LastModified);

    public class QueryRequest
    {
        public string Question { get; set; } = "";
        public int MaxResults { get; set; } = 10;
        public string model { get; set; }
        public bool meai_info { get; set; } =true;
        public string sessionId { get; set; }
    }

    public record EmbeddingData(string Text, List<float> Vector, string SourceFile, DateTime LastModified)
    {
        public double Similarity { get; set; }
    }
}
