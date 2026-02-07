namespace MEAI_GPT_API.Models
{
    public class RerankRequest
    {
        public string Query { get; set; } = string.Empty;
        public List<RelevantChunk> Chunks { get; set; } = new();
        public string Model { get; set; } = "qllama/bge-reranker-v2-m3:f16";
        public int TopK { get; set; } = 5;
        public bool UseReranking { get; set; } = true;
    }

    public class RerankResponse
    {
        public List<RelevantChunk> Chunks { get; set; } = new();
    }
}
