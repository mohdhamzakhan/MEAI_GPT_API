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

        /// <summary>
        /// Set by GetRelevantChunksWithExpansionAsync's SelectWithGuaranteedAnchors
        /// when this chunk was reserved a slot because it matched a topic anchor,
        /// policy trigger, or document-router selection -- i.e. a high-confidence
        /// signal this document IS the right one for the query, found upstream of
        /// pure similarity ranking. RerankerService.RerankAsync must respect this
        /// flag and re-reserve a slot after its own re-scoring, or the guarantee
        /// given here gets silently undone by reranking (this happened in
        /// production: a correctly-anchored Settlement Policy chunk survived
        /// retrieval's guarantee only to be cut by the reranker's independent
        /// cross-encoder judgment moments later).
        /// </summary>
        public bool IsAnchorGuaranteed { get; set; } = false;

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