namespace MEAI_GPT_API.Models
{
    public class RelevantChunk
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public double Similarity { get; set; }
        public string PolicyType { get; set; } = "";
        public List<float>? Embedding { get; set; } // NEW
        public double Bm25Score { get; set; }    // BM25 score
    }
}
