using MEAI_GPT_API.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service.Models
{
    public class EntityExtractionService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<EntityExtractionService> _logger;
        private readonly DynamicRAGConfiguration _config;

        public EntityExtractionService(IHttpClientFactory httpClientFactory, ILogger<EntityExtractionService> logger, IOptions<DynamicRAGConfiguration> config)
        {
            _httpClient = httpClientFactory.CreateClient("OllamaAPI");
            _logger = logger;
            _config = config.Value;
        }
        public async Task<List<string>> ExtractEntitiesAsync(string text)
        {
            // ✅ Fast regex-based extraction (no LLM call needed)
            var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Extract capitalized words/phrases (proper nouns)
                var capitalizedPattern = @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b";
                var capitalizedMatches = Regex.Matches(text, capitalizedPattern);
                foreach (Match match in capitalizedMatches)
                {
                    var entity = match.Value.Trim();
                    if (entity.Length > 2 && !IsCommonWord(entity))
                    {
                        entities.Add(entity);
                    }
                }

                // 2. Extract all-caps words (acronyms, organizations)
                var acronymPattern = @"\b[A-Z]{2,}\b";
                var acronymMatches = Regex.Matches(text, acronymPattern);
                foreach (Match match in acronymMatches)
                {
                    entities.Add(match.Value);
                }

                // 3. Extract titles + names (e.g., "CEO John Smith", "Dr. Jane Doe")
                var titlePattern = @"\b(Mr|Ms|Mrs|Dr|Prof|CEO|CTO|CFO|President|Director|Manager|Engineer)\b\.?\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*";
                var titleMatches = Regex.Matches(text, titlePattern, RegexOptions.IgnoreCase);
                foreach (Match match in titleMatches)
                {
                    entities.Add(match.Value.Trim());
                }

                // 4. Extract company suffixes (e.g., "Acme Corp", "XYZ Inc")
                var companyPattern = @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\s+(Corp|Inc|Ltd|LLC|Company|Corporation|Industries|Systems|Technologies|Solutions)\b";
                var companyMatches = Regex.Matches(text, companyPattern);
                foreach (Match match in companyMatches)
                {
                    entities.Add(match.Value.Trim());
                }

                // 5. Extract quoted names/terms
                var quotedPattern = @"""([^""]+)""|'([^']+)'";
                var quotedMatches = Regex.Matches(text, quotedPattern);
                foreach (Match match in quotedMatches)
                {
                    var quoted = match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : match.Groups[2].Value;
                    if (quoted.Length > 2)
                    {
                        entities.Add(quoted.Trim());
                    }
                }

                _logger.LogDebug($"✅ Fast entity extraction found {entities.Count} entities");

                return entities
                    .Where(e => e.Length > 1)
                    .Take(15) // Limit to prevent excessive entities
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract entities with regex");
                return new List<string>();
            }
        }

        // Helper method to filter common words
        private bool IsCommonWord(string word)
        {
            var commonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "Those", "When", "Where", "What", "Which",
        "Who", "Why", "How", "Can", "Could", "Would", "Should", "May", "Might",
        "Must", "Will", "Shall", "Have", "Has", "Had", "Does", "Did", "Done",
        "Here", "There", "About", "After", "Before", "During", "Since", "Until"
    };

            return commonWords.Contains(word);
        }

        public List<string> ExtractTopicsFromQuery(string query)
        {
            var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lowerQuery = query.ToLowerInvariant();

            // Split query into words and filter meaningful terms
            var words = lowerQuery
                .Split(new char[] { ' ', ',', '.', '?', '!', ':', ';', '-', '_', '(', ')', '[', ']' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length > 2 && !TextUtils.IsCommonWord(word))
                .ToList();

            // Add significant words as topics
            topics.UnionWith(words);

            // Extract compound terms (phrases)
            var compoundTerms = ExtractCompoundTerms(lowerQuery);
            topics.UnionWith(compoundTerms);

            // Extract policy-specific terms
            var policyTerms = ExtractPolicySpecificTerms(lowerQuery);
            topics.UnionWith(policyTerms);

            return topics.Take(10).ToList(); // Limit to most relevant topics
        }
        public List<string> ExtractCompoundTerms(string query)
        {
            var compounds = new List<string>();

            // Common HR/Policy compound terms
            var compoundPatterns = new[]
            {
        @"\b(casual\s+leave)\b",
        @"\b(sick\s+leave)\b",
        @"\b(earned\s+leave)\b",
        @"\b(maternity\s+leave)\b",
        @"\b(paternity\s+leave)\b",
        @"\b(compensatory\s+off)\b",
        @"\b(working\s+hours)\b",
        @"\b(overtime\s+policy)\b",
        @"\b(performance\s+appraisal)\b",
        @"\b(disciplinary\s+action)\b",
        @"\b(grievance\s+procedure)\b",
        @"\b(exit\s+interview)\b",
        @"\b(probation\s+period)\b",
        @"\b(notice\s+period)\b",
        @"\b(physical\s+security)\b",
        @"\b(information\s+security)\b",
        @"\b(access\s+control)\b",
        @"\b(risk\s+assessment)\b",
        @"\b(management\s+system)\b",
        @"\b(quality\s+management)\b",
        @"\b(safety\s+procedure)\b",
        @"\b(emergency\s+response)\b"
    };

            foreach (var pattern in compoundPatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(query, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    compounds.Add(match.Groups[1].Value);
                }
            }

            return compounds;
        }
        public List<string> ExtractPolicySpecificTerms(string query)
        {
            var policyTerms = new List<string>();
            var lowerQuery = query.ToLowerInvariant();

            // Policy domain keywords
            var domainMappings = new Dictionary<string[], string[]>
            {
                // HR Policy terms
                [new[] { "leave", "attendance", "payroll", "salary" }] = new[] { "hr policy", "employee handbook", "leave management" },

                // Security Policy terms  
                [new[] { "security", "access", "password", "data" }] = new[] { "information security", "isms policy", "access control" },

                // Safety Policy terms
                [new[] { "safety", "emergency", "hazard", "incident" }] = new[] { "safety policy", "emergency procedure", "risk management" },

                // Quality Policy terms
                [new[] { "quality", "audit", "compliance", "standard" }] = new[] { "quality management", "qms policy", "compliance procedure" }
            };

            foreach (var mapping in domainMappings)
            {
                if (mapping.Key.Any(keyword => lowerQuery.Contains(keyword)))
                {
                    policyTerms.AddRange(mapping.Value);
                }
            }

            // Common abbreviations expansion
            var abbreviations = new Dictionary<string, string[]>
            {
                ["cl"] = new[] { "casual leave", "leave policy" },
                ["sl"] = new[] { "sick leave", "medical leave" },
                ["el"] = new[] { "earned leave", "privilege leave" },
                ["ml"] = new[] { "maternity leave", "parental leave" },
                ["isms"] = new[] { "information security", "management system" },
                ["hr"] = new[] { "human resources", "employee policy" },
                ["ehs"] = new[] { "environment health safety", "safety policy" },
                ["qms"] = new[] { "quality management system", "quality policy" }
            };

            var queryWords = lowerQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in queryWords)
            {
                if (abbreviations.ContainsKey(word))
                {
                    policyTerms.AddRange(abbreviations[word]);
                }
            }

            return policyTerms.Distinct().ToList();
        }
        public string ExtractDocumentTypeFromMetadata(JsonElement metadata)
        {
            // Try document_type field first
            if (metadata.TryGetProperty("document_type", out var docType))
            {
                return docType.GetString() ?? "";
            }

            // Fallback: Extract from source file
            if (metadata.TryGetProperty("source_file", out var sourceFile))
            {
                var fileName = Path.GetFileNameWithoutExtension(sourceFile.GetString() ?? "").ToLowerInvariant();

                if (fileName.Contains("isms")) return "ISMS";
                if (fileName.Contains("hr")) return "HR";
                if (fileName.Contains("safety")) return "Safety";
                if (fileName.Contains("quality")) return "Quality";
                if (fileName.Contains("environment")) return "Environment";
            }

            return "General";
        }
        public string ExtractSectionIdFromMetadata(JsonElement metadata)
        {
            if (metadata.TryGetProperty("section_id", out var sectionId))
            {
                return sectionId.GetString() ?? "";
            }

            if (metadata.TryGetProperty("section_number", out var sectionNum))
            {
                return sectionNum.GetString() ?? "";
            }

            return "";
        }
        public string ExtractSectionTitleFromMetadata(JsonElement metadata)
        {
            if (metadata.TryGetProperty("section_title", out var title))
            {
                return title.GetString() ?? "";
            }

            return "";
        }
        public Dictionary<string, string> ExtractAbbreviationsFromQuery(string query, List<RelevantChunk> chunks)
        {
            var abbreviations = new Dictionary<string, string>();
            var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Check if query contains common abbreviations
            var commonAbbrevs = new Dictionary<string, string>
            {
                ["cl"] = "Casual Leave",
                ["sl"] = "Sick Leave",
                ["coff"] = "Compensatory Off",
                ["el"] = "Earned Leave",
                ["pl"] = "Privilege Leave",
                ["ml"] = "Maternity Leave",
                ["isms"] = "Information Security Management System",
                ["hr"] = "Human Resources",
                ["ehs"] = "Environment Health Safety",
                ["sop"] = "Standard Operating Procedure"
            };

            foreach (var word in queryWords)
            {
                if (commonAbbrevs.ContainsKey(word))
                {
                    abbreviations[word.ToUpper()] = commonAbbrevs[word];
                }
            }

            return abbreviations;
        }
        public bool HasManyAbbreviations(string query)
        {
            var abbreviations = new[] { "cl", "sl", "coff", "el", "pl", "ml", "hr", "isms", "ehs", "sop" };
            var queryLower = query.ToLowerInvariant();
            return abbreviations.Count(abbr => queryLower.Contains(abbr)) >= 2;
        }
        public string FormatAbbreviations(Dictionary<string, string> abbreviations)
        {
            if (!abbreviations.Any()) return "";

            return string.Join("\n", abbreviations.Select(kvp => $"• {kvp.Key} = {kvp.Value}"));
        }
    }
}
