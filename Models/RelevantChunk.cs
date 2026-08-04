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
        /// <summary>
        /// Boosted relevance score used purely for ranking/ordering.
        /// May exceed 1.0 due to policy-match and plant-specific boosts.
        /// Never exposed to the user or used for threshold comparisons.
        /// </summary>
        public double RelevanceScore { get; set; }
    }
}
