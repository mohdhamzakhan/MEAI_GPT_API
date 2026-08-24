using MEAI_GPT_API.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MEAI_GPT_API.Service
{
    public class RerankerService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<RerankerService> _logger;

        public RerankerService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<RerankerService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        private class RerankRequestBody
        {
            [JsonPropertyName("query")]
            public string Query { get; set; } = "";

            [JsonPropertyName("documents")]
            public List<string> Documents { get; set; } = new();
        }

        private class RerankResult
        {
            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("score")]
            public double Score { get; set; }

            [JsonPropertyName("document")]
            public string? Document { get; set; }
        }

        private class RerankResponseBody
        {
            [JsonPropertyName("query")]
            public string? Query { get; set; }

            [JsonPropertyName("results")]
            public List<RerankResult>? Results { get; set; }
        }

        // Calls a dedicated cross-encoder reranking microservice
        // (POST {RerankerService:BaseUrl}/rerank, body {query, documents:[...]},
        // returns {results:[{index, score, document}]} sorted by score).
        //
        // This replaces the previous approach of trying to use
        // qllama/bge-reranker-v2-m3 via Ollama's /api/generate, which always
        // 500'd -- that model's quantization has no usable logits head
        // through that endpoint. That is a property of how the model was
        // packaged for Ollama, not something fixable from this side; a
        // separately-hosted microservice sidesteps it entirely and has been
        // confirmed working (see the PowerShell smoke test that motivated
        // this change).
        //
        // `modelName` is kept as a parameter purely for call-site
        // compatibility with DynamicRagService/RerankTool -- the microservice
        // is a single fixed model, so it's unused here, not silently ignored
        // due to a bug.
        public async Task<List<RelevantChunk>> RerankAsync(
    string query,
    List<RelevantChunk> chunks,
    string modelName,
    int topK = 5)
        {
            if (chunks == null || chunks.Count == 0)
                return chunks ?? new List<RelevantChunk>();

            var enabled = _config.GetValue<bool>("RerankerService:Enabled", true);
            if (!enabled)
            {
                _logger.LogInformation("Reranking disabled via config — using raw similarity ranking");
                return FallbackToSimilarity(chunks, topK);
            }

            try
            {
                var client = _httpClientFactory.CreateClient("RerankerAPI");
                var body = new RerankRequestBody
                {
                    Query = query,
                    Documents = chunks.Select(c => c.Text).ToList()
                };

                var response = await client.PostAsJsonAsync("/rerank", body);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Reranker service returned {StatusCode}: {Response} — falling back to raw similarity ranking",
                        response.StatusCode, errorContent);
                    return FallbackToSimilarity(chunks, topK);
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var parsed = JsonSerializer.Deserialize<RerankResponseBody>(
                    responseJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed?.Results == null || parsed.Results.Count == 0)
                {
                    _logger.LogWarning("Reranker service returned no results — falling back to raw similarity ranking");
                    return FallbackToSimilarity(chunks, topK);
                }

                foreach (var result in parsed.Results)
                {
                    if (result.Index < 0 || result.Index >= chunks.Count)
                    {
                        // Defensive: a malformed/out-of-range index from the
                        // service should never crash the request, just be
                        // skipped -- that chunk simply keeps RerankScore null
                        // and falls back to Similarity in the final ordering.
                        _logger.LogWarning("Reranker returned out-of-range index {Index} for {Count} chunks — skipping", result.Index, chunks.Count);
                        continue;
                    }
                    chunks[result.Index].RerankScore = result.Score;
                }

                return SelectWithGuaranteedAnchors(chunks, topK, c => c.RerankScore ?? c.Similarity);
            }
            catch (Exception ex)
            {
                // Network error, timeout, malformed JSON, etc. -- never let a
                // reranker hiccup take down retrieval entirely. Same
                // fail-safe philosophy as the old Ollama-based path.
                _logger.LogWarning(ex, "Reranker call failed — falling back to raw similarity ranking");
                return FallbackToSimilarity(chunks, topK);
            }
        }

        private List<RelevantChunk> FallbackToSimilarity(List<RelevantChunk> chunks, int topK)
        {
            return SelectWithGuaranteedAnchors(chunks, topK, c => c.Similarity);
        }

        // Confirmed in production: a chunk correctly guaranteed a slot
        // upstream (see DynamicRagService's SelectWithGuaranteedAnchors --
        // topic anchor, policy trigger, or document-router match) survived
        // that guarantee only to be cut here moments later, because
        // reranking re-scores and re-selects top-K with zero awareness of
        // what was reserved earlier in the pipeline. This re-applies the
        // same reserve-then-fill principle after reranking, so a guarantee
        // made upstream can't be silently undone by this step.
        private List<RelevantChunk> SelectWithGuaranteedAnchors(
            List<RelevantChunk> chunks, int topK, Func<RelevantChunk, double> scoreSelector)
        {
            // Kept consistent with DynamicRagService's SelectWithGuaranteedAnchors
            // maxTotalGuaranteedSlots -- if these diverge, reranking just
            // re-truncates whatever extra room retrieval granted right back
            // down again, defeating the point of widening it there.
            const int maxGuaranteedSlots = 8;

            var guaranteed = chunks
                .Where(c => c.IsAnchorGuaranteed)
                .OrderByDescending(scoreSelector)
                .Take(Math.Min(maxGuaranteedSlots, topK))
                .ToList();

            var guaranteedIds = new HashSet<string>(guaranteed.Select(c => c.Id));
            var remainingSlots = topK - guaranteed.Count;

            var filler = chunks
                .Where(c => !guaranteedIds.Contains(c.Id))
                .OrderByDescending(scoreSelector)
                .Take(Math.Max(0, remainingSlots))
                .ToList();

            if (guaranteed.Any())
            {
                _logger.LogInformation(
                    "Reranker preserved {Count} anchor-guaranteed chunk(s) post-rerank: {Sources}",
                    guaranteed.Count, string.Join(", ", guaranteed.Select(c => c.Source).Distinct()));
            }

            return guaranteed.Concat(filler).ToList();
        }
    }
}