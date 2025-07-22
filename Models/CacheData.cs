namespace MEAI_GPT_API.Models
{
    public class CacheData
    {
        public DateTime GeneratedAt { get; set; }
        public EmbeddingEntry[] Embeddings { get; set; } = Array.Empty<EmbeddingEntry>();
    }
}
