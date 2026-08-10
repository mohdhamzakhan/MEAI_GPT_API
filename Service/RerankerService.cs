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

            var scored = new List<(int Index, double Score)>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var prompt = $"""
You are a relevance ranking model.
Score the relevance between the query and passage on a scale from 0 to 1.
Return ONLY a single number.

Query:
{query}

Passage:
{chunks[i].Text}
""";

                var request = new
                {
                    model = modelName,
                    prompt = prompt,
                    stream = false
                };

                var response = await _ollama.PostAsJsonAsync("/api/generate", request);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Reranker call failed for chunk {Index}: {Status}", i, response.StatusCode);
                    scored.Add((i, chunks[i].Similarity)); // fall back instead of throwing
                    continue;
                }
                response.EnsureSuccessStatusCode(); // safe now, only reached on success path anyway — can remove

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var output = doc.RootElement
                    .GetProperty("response")
                    .GetString();

                var score = ExtractScore(output, chunks[i].Similarity, i);

                scored.Add((i, score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .Select(s =>
                {
                    // Keep raw cosine Similarity untouched — store the reranker's
                    // opinion separately so downstream confidence/threshold logic
                    // that assumes Similarity == raw cosine still holds.
                    chunks[s.Index].RerankScore = s.Score;
                    return chunks[s.Index];
                })
                .ToList();
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
