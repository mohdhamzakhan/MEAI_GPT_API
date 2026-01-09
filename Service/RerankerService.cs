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
            if (!chunks.Any()) return chunks;

            var inputs = chunks.Select(c => new
            {
                query,
                passage = c.Text
            }).ToList();

            var request = new
            {
                model = modelName,
                input = inputs
            };

            var response = await _ollama.PostAsJsonAsync("/api/rerank", request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var scores = doc.RootElement
                .GetProperty("results")
                .EnumerateArray()
                .Select((r, i) => new
                {
                    Index = i,
                    Score = r.GetProperty("relevance_score").GetDouble()
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();

            return scores.Select(s =>
            {
                chunks[s.Index].Similarity = s.Score;
                return chunks[s.Index];
            }).ToList();
        }
    }
}
