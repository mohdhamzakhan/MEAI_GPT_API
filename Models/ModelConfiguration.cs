namespace MEAI_GPT_API.Models
{
    public class ModelConfiguration
    {
        public string Name { get; set; } = string.Empty;
        public int EmbeddingDimension { get; set; }
        public bool IsAvailable { get; set; }
        public bool SupportsBatchEmbedding {get; set; } = false;
        public string Type { get; set; } = string.Empty; // "embedding", "generation", "both"
        public Dictionary<string, object> ModelOptions { get; set; } = new();
        public int MaxContextLength { get; set; } = 4096;
        public double Temperature { get; set; } = 0.1;
    }
}
