using MEAI_GPT_API.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MEAI_GPT_API.Services.Agent
{
    public class SelfVerifier
    {
        private readonly HttpClient _ollamaClient;
        private readonly AgentDecisionLogger _decisionLogger;
        private readonly ILogger<SelfVerifier> _logger;

        // Model used for the cheap yes/no verification calls. Previously
        // hardcoded to "llama3.2:1b", which returned 404s against this
        // deployment's Ollama server (that model isn't pulled there) — every
        // verification call silently failed and fell back to the "assume
        // it's fine" branch below, so hallucination checking was effectively
        // disabled without any visible error. Default to the same model the
        // rest of the app already uses successfully (DefaultGenerationModel),
        // which is known to be available, while still allowing a smaller/
        // faster dedicated verifier model to be configured explicitly.
        private readonly string _verifierModel;

        public SelfVerifier(
            IHttpClientFactory httpClientFactory,
            AgentDecisionLogger decisionLogger,
            ILogger<SelfVerifier> logger,
            IOptions<DynamicRAGConfiguration> config)
        {
            _ollamaClient = httpClientFactory.CreateClient("OllamaAPI");
            _decisionLogger = decisionLogger;
            _logger = logger;

            var cfg = config.Value;
            _verifierModel = !string.IsNullOrWhiteSpace(cfg.VerifierModel)
                ? cfg.VerifierModel
                : (!string.IsNullOrWhiteSpace(cfg.DefaultGenerationModel)
                    ? cfg.DefaultGenerationModel
                    : "llama3.1:8b"); // last-resort fallback if config is empty — not the 1B model, see DynamicRagService.AutoSelectGenerationModel for why
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
                    var (isGrounded, reason) = await CheckGroundingAsync(response, sources);
                    result.IsGrounded = isGrounded;
                    if (!isGrounded && reason != null)
                    {
                        // Stashed on Metadata (rather than a new top-level
                        // field) so it flows through to the "metadata"
                        // StreamChunk already sent to the client without
                        // touching DynamicRagService's serialization code —
                        // makes a false "ungrounded" verdict debuggable from
                        // the frontend console instead of only server logs.
                        result.Metadata["grounding_reason"] = reason;
                    }
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
                // num_predict raised from the original 10 — too tight even
                // for a plain yes/no once `think:false` is set, since some
                // Ollama/model combinations still emit a few tokens of
                // preamble. 40 gives headroom without much latency cost.
                var llmResponse = await CallLLMAsync(prompt, _verifierModel, numPredict: 40);

                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    // Inconclusive (e.g. truncated mid-<think>), not a real
                    // "no" — don't let a verifier hiccup masquerade as an
                    // incomplete-answer verdict.
                    _logger.LogWarning("Completeness check returned no usable verdict (model '{Model}'); falling back to length heuristic", _verifierModel);
                    return response.Length > 50;
                }

                return llmResponse.ToLowerInvariant().Contains("yes");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Completeness check failed (model '{Model}'); falling back to length heuristic", _verifierModel);
                // Fallback: simple heuristic
                return response.Length > 50;
            }
        }

        private async Task<(bool IsGrounded, string? Reason)> CheckGroundingAsync(string response, List<RelevantChunk> sources)
        {
            // Previously only the top 3 chunks were shown to the verifier
            // (sources.Take(3)), while generation itself sees the full
            // retrieved set. If the specific fact the answer relies on came
            // from chunk #4+, the verifier would correctly-but-wrongly flag
            // a factually accurate answer as "ungrounded". Widen this to the
            // top 6 to better match what generation actually saw, while
            // still keeping the prompt bounded.
            var sourceText = string.Join("\n\n", sources.Take(6).Select(s => s.Text));

            // Ask for one short reason alongside the verdict so failures are
            // debuggable from logs instead of being an opaque yes/no. The
            // reason is parsed out for logging only — the verdict is still
            // just the leading yes/no token.
            var prompt = $@"Is the following response factually grounded in the source material?

Source Material:
{sourceText}

Response:
{response}

Reply on the first line with ONLY 'yes' or 'no'. On the second line, give one short sentence explaining why. The response should not make claims that aren't supported by the sources.";

            try
            {
                // num_predict raised from 60 to 100 — the model needs room
                // for both the yes/no line and the reason line even with
                // thinking disabled.
                var llmResponse = await CallLLMAsync(prompt, _verifierModel, numPredict: 100);

                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    // Inconclusive (e.g. truncated mid-<think>) — same
                    // reasoning as CheckCompletenessAsync: don't let a
                    // verifier hiccup register as "ungrounded".
                    _logger.LogWarning("Grounding check returned no usable verdict (model '{Model}'); conservatively assuming grounded", _verifierModel);
                    return (true, null);
                }

                var lines = llmResponse.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var firstLine = lines.Length > 0 ? lines[0] : "";
                var isGrounded = firstLine.ToLowerInvariant().Contains("yes");
                var reason = lines.Length > 1 ? lines[1] : null;

                if (!isGrounded)
                {
                    _logger.LogWarning(
                        "Grounding check returned 'no' (model '{Model}'). Verifier reasoning: {Reasoning}",
                        _verifierModel, llmResponse);
                }

                return (isGrounded, reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Grounding check failed (model '{Model}'); conservatively assuming grounded — this means hallucination checking did NOT run for this response", _verifierModel);
                // Conservative: assume it's grounded if we can't verify
                return (true, null);
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

        private async Task<string> CallLLMAsync(string prompt, string model, int numPredict = 10)
        {
            var requestData = new
            {
                model = model,
                prompt = prompt,
                stream = false,
                // Qwen3-family models (e.g. qwen3.5:9b, used as VerifierModel)
                // default to an internal "thinking" pass — they emit a
                // <think>...</think> reasoning block BEFORE the actual
                // yes/no answer, even through the raw /api/generate
                // completion endpoint, not just the chat endpoint. With
                // num_predict capped at 10-60 for these cheap verifier
                // calls, the model was getting cut off mid-thought and
                // never producing "yes"/"no" at all — which silently
                // registered as both "incomplete" and "ungrounded" here,
                // exactly matching the false-refusal symptom. `think: false`
                // is Ollama's documented top-level switch (supported since
                // 0.9+ for reasoning-capable models) to skip that pass
                // entirely. Non-reasoning models simply ignore the field.
                think = false,
                options = new { temperature = 0.0, num_predict = numPredict }
            };

            var response = await _ollamaClient.PostAsJsonAsync("/api/generate", requestData);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var rawText = doc.RootElement.GetProperty("response").GetString() ?? "";

            // Belt-and-braces: if a <think> block slipped through anyway
            // (older Ollama version that ignores the flag, or a model that
            // doesn't honor it), strip it so downstream yes/no parsing looks
            // at the actual answer rather than the reasoning trace.
            return StripThinkBlock(rawText);
        }

        private static string StripThinkBlock(string text)
        {
            var closeTag = "</think>";
            var closeIdx = text.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
            if (closeIdx >= 0)
            {
                return text[(closeIdx + closeTag.Length)..].Trim();
            }

            // No closing tag: if there's an opening <think> with nothing
            // after it, the response was cut off mid-reasoning (num_predict
            // too small) rather than actually answering. Signal that
            // distinctly (empty string) so callers can tell "no verdict
            // reached" apart from "model said no" instead of the empty/
            // no-yes text being silently read as a negative answer.
            var openIdx = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                return "";
            }

            return text.Trim();
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