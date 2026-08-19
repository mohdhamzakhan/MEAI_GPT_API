using MEAI_GPT_API.Models;
using MEAIGPTAPI.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service
{
    public class RerankerService
    {
        private readonly OllamaHttpClient _ollama;
        private readonly ILogger<RerankerService> _logger;

        public RerankerService(OllamaHttpClient ollama, ILogger<RerankerService> logger)
        {
            _ollama = ollama;
            _logger = logger;
        }

        public async Task<List<RelevantChunk>> RerankAsync(
    string query,
    List<RelevantChunk> chunks,
    string modelName,
    int topK = 5)
        {
            if (chunks == null || chunks.Count == 0)
                return chunks;

            // ⚠️ TEMPORARY: qllama/bge-reranker-v2-m3 (both :f16 and :latest tags) was
            // pulled from Ollama's registry with logits computation disabled, so it
            // cannot generate text via /api/generate — every call 500s with
            // "the current context does not logits computation. skipping". This is a
            // property of how the model was packaged, not a prompt/config issue on
            // our side. Skipping reranking entirely until a working reranker model is
            // available (see options below) — falls back to raw vector similarity,
            // which is already producing strong (0.7+) scores post-embedding-fix.
            _logger.LogInformation("Reranking skipped (model unavailable) — using raw similarity ranking");
            return chunks
                .OrderByDescending(c => c.Similarity)
                .Take(topK)
                .ToList();
        }

        private async Task<double> ScoreChunkAsync(string query, RelevantChunk chunk, string modelName, int index)
        {
            var prompt = $"""
You are a relevance ranking model.
Score the relevance between the query and passage on a scale from 0 to 1.
Return ONLY a single number.

Query:
{query}

Passage:
{chunk.Text}
""";

            var request = new { model = modelName, prompt = prompt, stream = false, raw = true };

            try
            {
                var response = await _ollama.PostAsJsonAsync("/api/generate", request);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Reranker call failed for chunk {Index}: {Status}", index, response.StatusCode);
                    return chunk.Similarity; // fall back instead of throwing
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var output = doc.RootElement.GetProperty("response").GetString();

                return ExtractScore(output, chunk.Similarity, index);
            }
            catch (Exception ex)
            {
                // ✅ Previously only IsSuccessStatusCode was checked — a genuine
                // network exception (timeout, connection reset) here would still
                // propagate uncaught and, per the ExecuteRetrievalAsync try/catch,
                // could discard already-retrieved chunks. Fail this one chunk only.
                _logger.LogWarning(ex, "Reranker call threw for chunk {Index}; falling back to vector similarity", index);
                return chunk.Similarity;
            }
        }
        /// <summary>
        /// Pulls the first 0-1 floating point number out of the reranker's raw
        /// text output. The model is asked to return "ONLY a single number" but
        /// in practice it sometimes wraps the number in text (e.g. "Score: 0.85",
        /// a trailing newline, or a short explanation). A strict double.TryParse
        /// on the whole string fails in those cases and used to silently score
        /// the chunk as 0, effectively discarding a possibly-relevant chunk for
        /// a formatting slip rather than a relevance judgement.
        /// On genuine parse failure, fall back to the chunk's existing (vector)
        /// similarity rather than 0, so a formatting hiccup degrades gracefully
        /// to "keep the vector ranking" instead of "bury this chunk".
        /// </summary>
        private double ExtractScore(string? output, double fallbackSimilarity, int chunkIndex)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning(
                    "Reranker returned empty output for chunk {Index}; falling back to vector similarity {Fallback:F3}",
                    chunkIndex, fallbackSimilarity);
                return fallbackSimilarity;
            }

            var match = Regex.Match(output, @"(?<![\d.])(0(?:\.\d+)?|1(?:\.0+)?)(?![\d.])");
            if (match.Success && double.TryParse(match.Value, out var score))
            {
                return Math.Clamp(score, 0.0, 1.0);
            }

            _logger.LogWarning(
                "Reranker output for chunk {Index} was not parseable ('{Output}'); falling back to vector similarity {Fallback:F3}",
                chunkIndex, output, fallbackSimilarity);
            return fallbackSimilarity;
        }

    }
}