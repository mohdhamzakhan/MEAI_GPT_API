using MEAI_GPT_API.Models;
using MEAI_GPT_API.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;
using static MEAI_GPT_API.Services.DynamicRagService;

namespace MEAI_GPT_API.Service.Models
{
    public class PolicyAnalysisService
    {
        private readonly ILogger<DynamicRagService> _logger;
        private readonly DynamicRAGConfiguration _config;

        public PolicyAnalysisService(ILogger<DynamicRagService> logger, IOptions<DynamicRAGConfiguration> config)
        {
            _logger = logger;
            _config = config.Value;
        }
        public bool HasSectionReference(string text)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(text,
                @"\b(section|clause|part|paragraph)\s+\d+|^\d+\.\d+|\b\d+\s+(introduction|scope|definitions|context|leadership|physical|planning|operation|performance|improvement)");
        }
        public string ExtractSectionReference(string text)
        {
            // Try different patterns
            var patterns = new[]
            {
        @"(section|clause|part|paragraph)\s+(\d+(?:\.\d+)?)",
        @"(\d+\.\d+(?:\.\d+)?)",
        @"section\s*(\d+)",
        @"(\d+)\s+(introduction|scope|definitions|context|leadership|physical|planning|operation|performance|improvement)"
    };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (match.Groups.Count > 2)
                        return $"{match.Groups[1].Value} {match.Groups[2].Value}";
                    else
                        return $"Section {match.Groups[1].Value}";
                }
            }

            return "";
        }
        public async Task<SectionQuery?> DetectAndParseSection(string query)
        {
            var lowerQuery = query.ToLower();

            // Pattern 1: "section X of [document type]" 
            var match1 = Regex.Match(lowerQuery, @"section\s+(\d+(?:\.\d+)*)\s+of\s+(\w+)");
            if (match1.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match1.Groups[1].Value,
                    DocumentType = match1.Groups[2].Value.ToUpper(),
                    OriginalQuery = query
                };
            }

            // Pattern 2: "[Document type] section X"
            var match2 = Regex.Match(lowerQuery, @"(\w+)\s+section\s+(\d+(?:\.\d+)*)");
            if (match2.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match2.Groups[2].Value,
                    DocumentType = match2.Groups[1].Value.ToUpper(),
                    OriginalQuery = query
                };
            }

            // Pattern 3: "section X" (any number - will search across all policy types)
            var match3 = Regex.Match(lowerQuery, @"section\s+(\d+(?:\.\d+)*)");
            if (match3.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match3.Groups[1].Value,
                    DocumentType = DetectDocumentTypeFromContext(lowerQuery), // Dynamic detection
                    OriginalQuery = query
                };
            }

            // Pattern 4: Just number references like "what is 5.2"
            // Note: "clause" intentionally NOT matched here anymore — it now
            // flows through the generic ReferenceTypes loop below, which
            // gives it the same document-hint capture and exact-match
            // retrieval that Annexure already gets, instead of only the
            // basic topic-based DocumentType detection it had before.
            var match4 = Regex.Match(lowerQuery, @"(?:what\s+is\s+|tell\s+me\s+about\s+)?section\s+(\d+(?:\.\d+)*)(?!\s*(?:working\s+days|days|hours|months|years)\b)");
            if (match4.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match4.Groups[1].Value,
                    DocumentType = DetectDocumentTypeFromContext(lowerQuery),
                    OriginalQuery = query
                };
            }

            // ✅ NEW: generic, config-driven detection for any reference type
            // configured under DynamicRAG:ReferenceTypes in appsettings.json
            // (e.g. Annexure, Clause, Form, Exhibit). Adding a new type there
            // needs no code change — it automatically gets the same
            // exact-match retrieval treatment originally built specifically
            // for Annexure.
            foreach (var refType in _config.ReferenceTypes ?? new List<ReferenceTypeOptions>())
            {
                if (string.IsNullOrWhiteSpace(refType.Pattern))
                    continue;

                var refMatch = Regex.Match(lowerQuery, refType.Pattern, RegexOptions.IgnoreCase);
                if (!refMatch.Success || refMatch.Groups.Count < 2)
                    continue;

                // Capture an explicit document-name hint from "<type> N of
                // <hint>" phrasing, e.g. "annexure 2 of ISMS Technical" ->
                // "isms technical". Without this, two documents that both
                // legitimately contain the same reference number (e.g. two
                // ISMS documents each with their own "Annexure 2") can't be
                // told apart. Position-based rather than a rebuilt regex, so
                // it works the same way regardless of which type matched.
                var afterMatch = lowerQuery.Substring(refMatch.Index + refMatch.Length).TrimStart();
                string docHint = "";
                bool hasExplicitHint = false;

                if (afterMatch.StartsWith("of "))
                {
                    docHint = Regex.Replace(afterMatch.Substring(3), @"\s+policy\s*$", "", RegexOptions.IgnoreCase).Trim();
                    hasExplicitHint = !string.IsNullOrWhiteSpace(docHint);
                }

                if (!hasExplicitHint)
                {
                    docHint = DetectDocumentTypeFromContext(lowerQuery);
                }

                return new SectionQuery
                {
                    SectionNumber = refMatch.Groups[1].Value,
                    DocumentType = docHint,
                    ReferenceType = refType.Name,
                    OriginalQuery = query,
                    HasExplicitDocumentHint = hasExplicitHint
                };
            }

            // Pattern 5: Topic-based dynamic section detection - NOW AWAITED
            //var topicBasedSection = await DetectSectionByTopicDynamic(lowerQuery);
            //if (topicBasedSection != null)
            //{
            //    return topicBasedSection;
            //}

            return null;
        }
        public string DetectDocumentTypeFromContext(string query)
        {
            var lowerQuery = query.ToLowerInvariant();

            // Explicit mentions
            if (lowerQuery.Contains("isms")) return "ISMS";
            if (lowerQuery.Contains("hr") || lowerQuery.Contains("human resource")) return "HR";
            if (lowerQuery.Contains("safety") || lowerQuery.Contains("ehs")) return "Safety";
            if (lowerQuery.Contains("quality") || lowerQuery.Contains("qms")) return "Quality";
            if (lowerQuery.Contains("environment")) return "Environment";
            if (lowerQuery.Contains("security")) return "Security";

            // Content-based detection
            if (lowerQuery.Contains("leave") || lowerQuery.Contains("attendance") ||
                lowerQuery.Contains("payroll") || lowerQuery.Contains("employee"))
                return "HR";

            if (lowerQuery.Contains("information") || lowerQuery.Contains("data") ||
                lowerQuery.Contains("access control") || lowerQuery.Contains("cyber"))
                return "ISMS";

            if (lowerQuery.Contains("accident") || lowerQuery.Contains("hazard") ||
                lowerQuery.Contains("incident") || lowerQuery.Contains("emergency"))
                return "Safety";

            return ""; // Search all types if no specific type detected
        }
        public string DetermineDocumentType(string sourceFile)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourceFile).ToLower();

            if (fileName.Contains("isms")) return "ISMS";
            if (fileName.Contains("hr")) return "HR Policy";
            if (fileName.Contains("safety")) return "Safety Policy";
            if (fileName.Contains("security")) return "Security Policy";
            if (fileName.Contains("employee")) return "Employee Handbook";
            if (fileName.Contains("general")) return "General Policy";

            return "Policy Document";
        }
        public string DeterminePolicyType(JsonElement metadata, string sourceFile, string currentPlant)
        {
            var fileName = sourceFile.ToLowerInvariant();

            // Check metadata first
            if (metadata.TryGetProperty("is_context", out var isContext) && isContext.GetBoolean())
            {
                return "Context Information";
            }

            if (metadata.TryGetProperty("is_centralized", out var isCentralized) && isCentralized.GetBoolean())
            {
                return "Centralized Policy";
            }

            if (metadata.TryGetProperty("plant", out var plantProperty))
            {
                var plantValue = plantProperty.GetString()?.ToLowerInvariant() ?? "";

                if (plantValue == "context")
                    return "Context Information";
                if (plantValue == "centralized" || plantValue == "general")
                    return "Centralized Policy";
                if (plantValue == currentPlant.ToLowerInvariant())
                    return $"{currentPlant.ToTitleCase()} Specific Policy";
                if (plantValue != currentPlant.ToLowerInvariant() && !string.IsNullOrEmpty(plantValue))
                    return $"{plantValue.ToTitleCase()} Policy (Cross-Reference)";
            }

            // Fallback to file name analysis
            if (fileName.Contains("abbreviation") || fileName.Contains("context"))
                return "Context Information";
            if (fileName.Contains("centralized") || fileName.Contains("general"))
                return "Centralized Policy";
            if (fileName.Contains(currentPlant.ToLowerInvariant()))
                return $"{currentPlant.ToTitleCase()} Specific Policy";

            return "General Policy";
        }
        public List<string> GetDynamicSectionTopics(string sectionNumber, string documentType)
        {
            var topics = new List<string>();

            // Define section mappings per policy type dynamically
            var policySpecificMappings = new Dictionary<string, Dictionary<string, string[]>>
            {
                ["ISMS"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Introduction", "Purpose", "Scope", "Overview" },
                    ["2"] = new[] { "Scope", "Application", "Boundaries" },
                    ["3"] = new[] { "Definitions", "Terms", "Abbreviations" },
                    ["4"] = new[] { "Context", "Organization", "Stakeholders" },
                    ["5"] = new[] { "Leadership", "Management Commitment", "Policy" },
                    ["6"] = new[] { "Physical Security", "Secure Areas", "Equipment Protection" },
                    ["7"] = new[] { "Planning", "Risk Assessment", "Treatment" },
                    ["8"] = new[] { "Operation", "Operational Controls", "Implementation" },
                    ["9"] = new[] { "Performance", "Evaluation", "Monitoring", "Audit" },
                    ["10"] = new[] { "Improvement", "Nonconformity", "Corrective Action" }
                },
                ["HR"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Introduction", "Purpose", "Employee Handbook" },
                    ["2"] = new[] { "Scope", "Applicability", "Coverage" },
                    ["3"] = new[] { "Recruitment", "Selection", "Hiring Process" },
                    ["4"] = new[] { "Leave Policy", "Annual Leave", "Sick Leave", "Casual Leave" },
                    ["5"] = new[] { "Attendance", "Working Hours", "Punctuality" },
                    ["6"] = new[] { "Performance", "Appraisal", "Review Process" },
                    ["7"] = new[] { "Grievance", "Complaint", "Resolution" },
                    ["8"] = new[] { "Disciplinary", "Misconduct", "Actions" },
                    ["9"] = new[] { "Benefits", "Compensation", "Welfare" },
                    ["10"] = new[] { "Termination", "Resignation", "Exit Process" }
                },
                ["Safety"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Introduction", "Safety Policy", "Commitment" },
                    ["2"] = new[] { "Scope", "Applicability", "Coverage" },
                    ["3"] = new[] { "Hazard Identification", "Risk Assessment" },
                    ["4"] = new[] { "Emergency Procedures", "Response", "Evacuation" },
                    ["5"] = new[] { "Incident Reporting", "Investigation", "Analysis" },
                    ["6"] = new[] { "Training", "Competency", "Awareness" },
                    ["7"] = new[] { "PPE Requirements", "Personal Protective Equipment" },
                    ["8"] = new[] { "Contractor Safety", "Vendor Management" },
                    ["9"] = new[] { "Audit", "Inspection", "Monitoring" },
                    ["10"] = new[] { "Review", "Improvement", "Management Review" }
                },
                ["Quality"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Scope", "Quality Manual", "QMS" },
                    ["2"] = new[] { "References", "Standards", "Documentation" },
                    ["3"] = new[] { "Definitions", "Terms", "Quality Terms" },
                    ["4"] = new[] { "Quality System", "QMS Requirements" },
                    ["5"] = new[] { "Management Responsibility", "Leadership" },
                    ["6"] = new[] { "Resource Management", "Human Resources" },
                    ["7"] = new[] { "Product Realization", "Process Management" },
                    ["8"] = new[] { "Measurement", "Analysis", "Customer Satisfaction" },
                    ["9"] = new[] { "Improvement", "Corrective Action", "Preventive Action" }
                }
            };

            if (policySpecificMappings.ContainsKey(documentType) &&
                policySpecificMappings[documentType].ContainsKey(sectionNumber))
            {
                topics.AddRange(policySpecificMappings[documentType][sectionNumber]);
            }

            // Add generic section topics if no specific mapping found
            if (!topics.Any())
            {
                topics.AddRange(new[]
                {
            $"section {sectionNumber} content",
            $"policy section {sectionNumber}",
            $"{documentType} requirements section {sectionNumber}"
        });
            }

            return topics;
        }
        public bool CheckPolicyCoverage(List<RelevantChunk> chunks, string question)
        {
            if (!chunks.Any())
            {
                _logger.LogWarning($"⚠️ No relevant chunks found for question: {question}");
                return false;
            }

            var veryHighQuality = chunks.Where(c => c.Similarity >= 0.7).ToList();
            var highQualityChunks = chunks.Where(c => c.Similarity >= 0.4).ToList();
            var mediumQualityChunks = chunks.Where(c => c.Similarity >= 0.25).ToList();

            var questionWords = question
                .ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .ToHashSet();

            bool HasTopicalOverlap(RelevantChunk c)
            {
                if (!questionWords.Any()) return false;
                var chunkWords = c.Text.ToLowerInvariant();
                var matchCount = questionWords.Count(qw => chunkWords.Contains(qw));
                return matchCount >= Math.Min(2, questionWords.Count);
            }

            var hasSufficientCoverage =
                veryHighQuality.Any() ||
                highQualityChunks.Any(HasTopicalOverlap) ||
                mediumQualityChunks.Count(HasTopicalOverlap) >= 2;

            if (!hasSufficientCoverage)
            {
                _logger.LogWarning($"⚠️ Insufficient policy coverage for question: {question}. " +
                                  $"Very High: {veryHighQuality.Count}, High: {highQualityChunks.Count}, " +
                                  $"Medium: {mediumQualityChunks.Count}");

                foreach (var chunk in chunks.Take(3))
                {
                    _logger.LogInformation($"📄 Chunk: {chunk.Source} | Similarity: {chunk.Similarity:F3} | Text: {chunk.Text.Substring(0, Math.Max(100, chunk.Text.Length - 1))}");
                }
            }

            return hasSufficientCoverage;
        }
        //public bool CheckPolicyCoverage(List<RelevantChunk> chunks, string question)
        //{
        //    if (!chunks.Any())
        //    {
        //        _logger.LogWarning($"⚠️ No relevant chunks found for question: {question}");
        //        return false;
        //    }

        //    var veryHighQuality = chunks.Where(c => c.Similarity >= 0.65).ToList();
        //    var highQualityChunks = chunks.Where(c => c.Similarity >= 0.45).ToList();
        //    var mediumQualityChunks = chunks.Where(c => c.Similarity >= 0.30).ToList();

        //    // ✅ CHANGED: require actual word overlap between the QUESTION and the chunk text,
        //    // not just presence of generic policy vocabulary anywhere in the corpus.
        //    var questionWords = question
        //        .ToLowerInvariant()
        //        .Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
        //        .Where(w => w.Length > 3)
        //        .ToHashSet();

        //    bool HasTopicalOverlap(RelevantChunk c)
        //    {
        //        if (!questionWords.Any()) return false;
        //        var chunkWords = c.Text.ToLowerInvariant();
        //        var matchCount = questionWords.Count(qw => chunkWords.Contains(qw));
        //        // Require at least 2 meaningful question words to actually appear in the chunk,
        //        // or 1 if the question is very short.
        //        return matchCount >= Math.Min(2, questionWords.Count);
        //    }

        //    var hasSufficientCoverage =
        //        veryHighQuality.Any() ||
        //        (highQualityChunks.Count >= 1 && highQualityChunks.Any(HasTopicalOverlap)) ||
        //        (mediumQualityChunks.Count >= 2 && mediumQualityChunks.Count(HasTopicalOverlap) >= 2);

        //    if (!hasSufficientCoverage)
        //    {
        //        _logger.LogWarning($"⚠️ Insufficient policy coverage for question: {question}. " +
        //                          $"VeryHigh: {veryHighQuality.Count}, High: {highQualityChunks.Count}, " +
        //                          $"Medium: {mediumQualityChunks.Count}");

        //        foreach (var chunk in chunks.Take(3))
        //        {
        //            _logger.LogInformation($"📄 Chunk: {chunk.Source} | Similarity: {chunk.Similarity:F3} | Overlap: {HasTopicalOverlap(chunk)}");
        //        }
        //    }

        //    return hasSufficientCoverage;
        //}
    }
}