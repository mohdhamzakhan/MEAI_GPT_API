using MEAI_GPT_API.Models;
using System.Text.Json;

namespace MEAI_GPT_API.Services.Agent
{
    public class SelfVerifier
    {
        private readonly HttpClient _ollamaClient;
        private readonly AgentDecisionLogger _decisionLogger;
        private readonly ILogger<SelfVerifier> _logger;

        public SelfVerifier(
            IHttpClientFactory httpClientFactory,
            AgentDecisionLogger decisionLogger,
            ILogger<SelfVerifier> logger)
        {
            _ollamaClient = httpClientFactory.CreateClient("OllamaAPI");
            _decisionLogger = decisionLogger;
            _logger = logger;
        }

        public async Task<VerificationResult> VerifyResponseAsync(
            string query,
            string response,
            List<RelevantChunk> sources,
            bool checkFactuality = true)
        {
            var result = new VerificationResult
            {
                Query = query,
                Response = response,
                Timestamp = DateTime.Now
            };

            try
            {
                // 1. Check response completeness
                result.IsComplete = await CheckCompletenessAsync(query, response);

                // 2. Check factual grounding (for MEAI queries)
                if (checkFactuality && sources.Any())
                {
                    result.IsGrounded = await CheckGroundingAsync(response, sources);
                }
                else
                {
                    result.IsGrounded = true; // Skip for general queries
                }

                // 3. Check for hallucination markers
                result.HasHallucinations = DetectHallucinations(response, sources);

                // 4. Calculate overall confidence
                result.OverallConfidence = CalculateConfidence(result, sources);

                // 5. Determine if reprocessing is needed
                result.NeedsReprocessing = result.OverallConfidence < 0.7 ||
                                          result.HasHallucinations;

                _decisionLogger.LogDecision(new AgentDecision
                {
                    Phase = "SelfVerification",
                    DecisionMade = result.NeedsReprocessing ? "RETRY_NEEDED" : "APPROVED",
                    Reasoning = $"Confidence: {result.OverallConfidence:P0}, Complete: {result.IsComplete}, Grounded: {result.IsGrounded}",
                    Confidence = result.OverallConfidence
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Verification failed");

                result.VerificationError = ex.Message;
                result.NeedsReprocessing = true;

                return result;
            }
        }

        private async Task<bool> CheckCompletenessAsync(string query, string response)
        {
            // Check basic completeness criteria
            if (string.IsNullOrWhiteSpace(response) || response.Length < 20)
                return false;

            // Check if response actually addresses the query
            var prompt = $@"Does this response adequately answer the question?

Question: {query}
Response: {response}

Answer with ONLY 'yes' or 'no'.";

            try
            {
                var llmResponse = await CallLLMAsync(prompt, "llama3.2:1b");
                return llmResponse.ToLowerInvariant().Contains("yes");
            }
            catch
            {
                // Fallback: simple heuristic
                return response.Length > 50;
            }
        }

        private async Task<bool> CheckGroundingAsync(string response, List<RelevantChunk> sources)
        {
            var sourceText = string.Join("\n\n", sources.Take(3).Select(s => s.Text));

            var prompt = $@"Is the following response factually grounded in the source material?

Source Material:
{sourceText}

Response:
{response}

Answer with ONLY 'yes' or 'no'. The response should not make claims that aren't supported by the sources.";

            try
            {
                var llmResponse = await CallLLMAsync(prompt, "llama3.2:1b");
                return llmResponse.ToLowerInvariant().Contains("yes");
            }
            catch
            {
                // Conservative: assume it's grounded if we can't verify
                return true;
            }
        }

        private bool DetectHallucinations(string response, List<RelevantChunk> sources)
        {
            var hallucinationMarkers = new[]
            {
                "I don't have access",
                "I cannot verify",
                "I'm not sure",
                "I apologize, but I don't know",
                "this information is not available"
            };

            var responseLower = response.ToLowerInvariant();

            // If response contains uncertainty markers AND we have sources, it's suspicious
            if (sources.Any() && hallucinationMarkers.Any(m => responseLower.Contains(m)))
            {
                return true;
            }

            return false;
        }

        private double CalculateConfidence(VerificationResult result, List<RelevantChunk> sources)
        {
            double confidence = 1.0;

            if (!result.IsComplete) confidence *= 0.5;
            if (!result.IsGrounded) confidence *= 0.6;
            if (result.HasHallucinations) confidence *= 0.4;

            // Factor in source quality
            if (sources.Any())
            {
                var avgSourceConfidence = sources.Average(s => s.Similarity);
                confidence *= (0.5 + (avgSourceConfidence * 0.5));
            }

            return Math.Max(0.0, Math.Min(1.0, confidence));
        }

        private async Task<string> CallLLMAsync(string prompt, string model)
        {
            var requestData = new
            {
                model = model,
                prompt = prompt,
                stream = false,
                options = new { temperature = 0.0, num_predict = 10 }
            };

            var response = await _ollamaClient.PostAsJsonAsync("/api/generate", requestData);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("response").GetString() ?? "";
        }
    }

    public class VerificationResult
    {
        public string Query { get; set; } = "";
        public string Response { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsComplete { get; set; }
        public bool IsGrounded { get; set; }
        public bool HasHallucinations { get; set; }
        public double OverallConfidence { get; set; }
        public bool NeedsReprocessing { get; set; }
        public string? VerificationError { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}