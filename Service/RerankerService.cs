using MEAI_GPT_API.Models;
using MEAIGPTAPI.Services;
using System.Text.Json;

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
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var output = doc.RootElement
                    .GetProperty("response")
                    .GetString();

                if (!double.TryParse(output, out var score))
                    score = 0; // fallback safety

                scored.Add((i, score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .Select(s =>
                {
                    chunks[s.Index].Similarity = s.Score;
                    return chunks[s.Index];
                })
                .ToList();
        }

    }
}
