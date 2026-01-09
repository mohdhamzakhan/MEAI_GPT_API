using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Services
{
    /// <summary>
    /// Lightweight BM25 implementation for hybrid retrieval.
    /// Designed to work AFTER vector retrieval.
    /// </summary>
    public class Bm25Service
    {
        // Standard BM25 parameters (tuned for documents, not short text)
        private const double K1 = 1.5;
        private const double B = 0.75;

        // Stopwords to reduce noise
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","is","are","a","an","and","or","of","to","in","on","for","with",
            "that","this","as","by","from","at","be","it","was","were","will","can"
        };

        /// <summary>
        /// Re-ranks vector-retrieved chunks using BM25.
        /// Does NOT replace vector search — complements it.
        /// </summary>
        public List<RelevantChunk> Rank(
            string query,
            List<RelevantChunk> chunks,
            int topK = 10)
        {
            if (string.IsNullOrWhiteSpace(query) || chunks == null || chunks.Count == 0)
                return chunks ?? new List<RelevantChunk>();

            // Tokenize query
            var queryTerms = Tokenize(query);
            if (!queryTerms.Any())
                return chunks;

            // Precompute document statistics
            var docLengths = chunks.ToDictionary(c => c.Id, c => Tokenize(c.Text).Count);
            var avgDocLength = docLengths.Values.DefaultIfEmpty(1).Average();

            // Document frequency for each term
            var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in queryTerms)
            {
                docFreq[term] = chunks.Count(c =>
                    Tokenize(c.Text).Contains(term));
            }

            // Compute BM25 score for each chunk
            foreach (var chunk in chunks)
            {
                var tokens = Tokenize(chunk.Text);
                var tokenCounts = tokens
                    .GroupBy(t => t)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                double score = 0.0;
                foreach (var term in queryTerms)
                {
                    if (!tokenCounts.TryGetValue(term, out var tf))
                        continue;

                    var df = docFreq.TryGetValue(term, out var d) ? d : 0;
                    if (df == 0) continue;

                    // IDF
                    var idf = Math.Log(1 + (chunks.Count - df + 0.5) / (df + 0.5));

                    // BM25 formula
                    var numerator = tf * (K1 + 1);
                    var denominator = tf + K1 * (1 - B + B * (docLengths[chunk.Id] / avgDocLength));

                    score += idf * (numerator / denominator);
                }

                chunk.Bm25Score = score;
            }

            return chunks
                .OrderByDescending(c => c.Bm25Score)
                .Take(topK)
                .ToList();
        }

        // ============================
        // TOKENIZATION
        // ============================
        private static List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z0-9]+\b")
                .Select(m => m.Value)
                .Where(t => t.Length > 1 && !StopWords.Contains(t))
                .ToList();
        }
    }
}
