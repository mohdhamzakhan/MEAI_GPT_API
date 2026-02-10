// Services/QueryIntentAnalyzer.cs
using MEAI_GPT_API.Service.Models;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Services
{
    public class QueryIntentAnalyzer
    {
        private readonly CompliancePolicyDetector _policyDetector;
        private readonly ILogger<QueryIntentAnalyzer> _logger;

        public QueryIntentAnalyzer(
            CompliancePolicyDetector policyDetector,
            ILogger<QueryIntentAnalyzer> logger)
        {
            _policyDetector = policyDetector;
            _logger = logger;
        }

        /// <summary>
        /// Detects which specific policy the user is asking about
        /// </summary>
        public PolicyIntent DetectPolicyIntent(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new PolicyIntent
                {
                    IntendedPolicyType = null,
                    Confidence = 0,
                    Query = query
                };
            }

            var lowerQuery = query.ToLowerInvariant();
            var policyTypeScores = new Dictionary<string, int>();

            // Get all policy patterns from CompliancePolicyDetector
            var allPatterns = _policyDetector.GetAllPolicyPatterns();

            foreach (var (policyName, pattern) in allPatterns)
            {
                int score = 0;

                // ✅ Check if query is asking FOR this specific policy
                if (IsAskingForPolicy(lowerQuery, policyName.ToLowerInvariant()))
                {
                    score += 50; // MASSIVE boost for direct policy requests
                }

                // Check primary keywords in query
                foreach (var keyword in pattern.PrimaryKeywords)
                {
                    var lowerKeyword = keyword.ToLowerInvariant();

                    // Exact match gets higher score
                    if (lowerQuery.Contains(lowerKeyword))
                    {
                        // Word boundary check - "ethics policy" vs "ethics" in middle of word
                        if (IsWordBoundaryMatch(lowerQuery, lowerKeyword))
                        {
                            score += 15; // Strong match
                        }
                        else
                        {
                            score += 10; // Partial match
                        }
                    }
                }

                // Check secondary keywords (lower weight)
                foreach (var keyword in pattern.SecondaryKeywords)
                {
                    if (lowerQuery.Contains(keyword.ToLowerInvariant()))
                    {
                        score += 1;
                    }
                }

                if (score > 0)
                {
                    policyTypeScores[policyName] = score;
                }
            }

            // Return top match if confident enough
            if (policyTypeScores.Any())
            {
                var bestMatch = policyTypeScores.OrderByDescending(kv => kv.Value).First();

                // Calculate confidence
                double confidence = CalculateConfidence(bestMatch.Value, policyTypeScores.Values.ToList());

                if (bestMatch.Value >= 10) // Minimum threshold
                {
                    _logger.LogInformation(
                        $"🎯 Policy intent detected: {bestMatch.Key} " +
                        $"(score: {bestMatch.Value}, confidence: {confidence:F2})");

                    return new PolicyIntent
                    {
                        IntendedPolicyType = bestMatch.Key,
                        Confidence = confidence,
                        Query = query,
                        Score = bestMatch.Value
                    };
                }
            }

            _logger.LogDebug($"No strong policy intent detected for query: {query}");

            return new PolicyIntent
            {
                IntendedPolicyType = null,
                Confidence = 0,
                Query = query
            };
        }

        /// <summary>
        /// Detects if query is directly asking about a policy
        /// Examples: "what is the ethics policy", "show me hr policy", "tell me about whistle blower"
        /// </summary>
        private bool IsAskingForPolicy(string lowerQuery, string policyName)
        {
            var askingPatterns = new[]
            {
                $"what is the {policyName}",
                $"what's the {policyName}",
                $"show me {policyName}",
                $"tell me about {policyName}",
                $"explain {policyName}",
                $"describe {policyName}",
                $"{policyName} policy",
                $"about {policyName}",
                $"regarding {policyName}",
                $"{policyName} information"
            };

            return askingPatterns.Any(pattern => lowerQuery.Contains(pattern));
        }

        /// <summary>
        /// Checks if keyword appears at word boundaries (not in middle of another word)
        /// </summary>
        private bool IsWordBoundaryMatch(string text, string keyword)
        {
            // Simple word boundary check
            var pattern = $@"\b{Regex.Escape(keyword)}\b";
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Calculate confidence based on score distribution
        /// </summary>
        private double CalculateConfidence(int bestScore, List<int> allScores)
        {
            if (allScores.Count == 1)
            {
                // Only one match - high confidence if score is good
                return bestScore >= 50 ? 0.95 :
                       bestScore >= 15 ? 0.85 :
                       bestScore >= 10 ? 0.70 : 0.50;
            }

            // Multiple matches - check separation
            var sortedScores = allScores.OrderByDescending(s => s).ToList();
            var secondBest = sortedScores[1];

            // Gap between best and second best
            var gap = bestScore - secondBest;

            if (gap >= 40) return 0.95; // Very clear winner
            if (gap >= 20) return 0.85; // Clear winner
            if (gap >= 10) return 0.75; // Moderate confidence
            if (gap >= 5) return 0.65;  // Some confidence

            return 0.50; // Ambiguous
        }

        /// <summary>
        /// Checks if the query is asking for a specific section of a policy
        /// </summary>
        public bool IsAskingForSection(string query, out string? sectionNumber)
        {
            sectionNumber = null;
            var lowerQuery = query.ToLowerInvariant();

            var sectionPatterns = new[]
            {
                @"section\s+(\d+(?:\.\d+)*)",
                @"section\s*(\d+)",
                @"clause\s+(\d+)",
                @"part\s+(\d+)"
            };

            foreach (var pattern in sectionPatterns)
            {
                var match = Regex.Match(lowerQuery, pattern);
                if (match.Success)
                {
                    sectionNumber = match.Groups[1].Value;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Represents the detected intent of a policy-related query
    /// </summary>
    public class PolicyIntent
    {
        /// <summary>
        /// The specific policy the user is asking about (e.g., "Company Ethics", "Anti-Bribery Policy")
        /// </summary>
        public string? IntendedPolicyType { get; set; }

        /// <summary>
        /// Confidence level (0.0 - 1.0)
        /// </summary>
        public double Confidence { get; set; }

        /// <summary>
        /// Original query
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Raw score (for debugging)
        /// </summary>
        public int Score { get; set; }

        /// <summary>
        /// Whether this is a strong match (confidence >= 0.7)
        /// </summary>
        public bool IsStrongMatch => Confidence >= 0.7;

        /// <summary>
        /// Whether this is a moderate match (0.5 <= confidence < 0.7)
        /// </summary>
        public bool IsModerateMatch => Confidence >= 0.5 && Confidence < 0.7;
    }
}