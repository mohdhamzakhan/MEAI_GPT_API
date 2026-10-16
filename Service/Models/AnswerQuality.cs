using MEAI_GPT_API.Services.Agent;

namespace MEAI_GPT_API.Service.Models
{
    public class AnswerQuality
    {
        public string Outcome { get; set; } = "answered";   // answered | caveat | declined | clarification
        public string ReasonCode { get; set; } = "OK";
        public string Headline { get; set; } = "";
        public string ConfidenceLevel { get; set; } = "high"; // high | medium | low
        public List<string> Sources { get; set; } = new();
        public List<string> Suggestions { get; set; } = new();
        public Dictionary<string, object?>? Details { get; set; } // admins only, else null
    }

    public static class AnswerQualityBuilder
    {
        public static AnswerQuality Build(VerificationResult? v, bool asked, bool declined,
            string? retrySkipped, IEnumerable<string> sources, string plant, bool admin)
        {
            var q = new AnswerQuality { Sources = sources.Distinct().Take(6).ToList() };
            var c = v?.OverallConfidence ?? 0.7;
            q.ConfidenceLevel = c >= 0.75 ? "high" : c >= 0.6 ? "medium" : "low";

            if (asked)
            {
                q.Outcome = "clarification"; q.ReasonCode = "NEEDS_CLARIFICATION";
                q.Headline = "I need one more detail to answer this reliably.";
            }
            else if (!declined)
            {
                q.Outcome = q.ConfidenceLevel == "high" ? "answered" : "caveat";
                if (q.Outcome == "caveat") q.Headline = "Matched your policy documents, but please double-check the details that matter.";
            }
            else if (v?.IsSourceConflict == true)
            {
                q.Outcome = "declined"; q.ReasonCode = "SOURCE_CONFLICT";
                q.Headline = "Two policy documents say different things here, so I couldn't give one answer.";
                q.Suggestions.Add("Ask HR which policy applies to you");
            }
            else if (v?.IsGrounded == false)
            {
                q.Outcome = "declined"; q.ReasonCode = "NOT_VERIFIED";
                q.Headline = $"I couldn't verify my answer against {plant}'s policy text, so I held it back instead of guessing.";
                q.Suggestions.AddRange(new[] { "Rephrase with the policy name", "Contact HR to confirm" });
            }
            else
            {
                q.Outcome = "declined"; q.ReasonCode = "INCOMPLETE";
                q.Headline = "I found related information, but it may not cover everything the policies say.";
                q.Suggestions.Add("Try a more specific question");
            }

            if (admin) q.Details = new()
            {
                ["groundingReason"] = v?.Metadata.GetValueOrDefault("grounding_reason"),
                ["retrySkippedReason"] = retrySkipped,
                ["confidence"] = c
            };
            return q;
        }
    }
}
