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
        /// <summary>
        /// Score assigned by the LLM-based reranker (0-1, when parseable).
        /// Kept separate from Similarity so raw cosine similarity is never
        /// overwritten by a reranker output that failed to parse or is
        /// otherwise uncalibrated.
        /// </summary>
        public double? RerankScore { get; set; }

        // ✅ NEW: populated from ChromaDB metadata in ParseSearchResults so
        // API consumers can cite the actual section/annexure a chunk came
        // from, not just the source filename.
        public string? SectionNumber { get; set; }
        public string? SectionTitle { get; set; }
        public string? AnnexureRefs { get; set; } // comma-delimited annexure numbers found in this chunk, if any
        /// <summary>
        /// Resolved server links for each annexure number referenced in this
        /// chunk, keyed by annexure number (e.g. "2" -> "\\SERVERNAME\policies\
        /// ISMS Policy..._Technical_Rev04\Annexure 2.pdf"). Populated by
        /// AnnexureLinkService, which looks up the actual file on disk since
        /// the extension (pdf/doc/docx/xls/xlsx) isn't known in advance.
        /// Null/empty if the feature is disabled, the file wasn't found, or
        /// the server path isn't reachable — never a guessed path.
        /// </summary>
        public Dictionary<string, string>? AnnexureLinks { get; set; }
    }
}