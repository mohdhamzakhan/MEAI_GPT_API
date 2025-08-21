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
            var prompt = $"Extract all named entities (people, organizations, titles, etc.) from the following text:\n\n\"{text}\"\n\nEntities:";

            var request = new
            {
                model = _config.DefaultGenerationModel ?? "mistral:latest",
                messages = new[]
                {
            new { role = "user", content = prompt }
        },
                temperature = 0.1,
                stream = false // Ensure non-streaming response
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/chat", request);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Entity extraction failed: {response.StatusCode}");
                    return new List<string>();
                }

                var json = await response.Content.ReadAsStringAsync();

                // Log the raw response for debugging
                _logger.LogDebug($"Entity extraction response: {json}");

                // Handle potential streaming response format
                if (json.Contains("}\n{"))
                {
                    // Split by newlines and take the last valid JSON object
                    var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var lastValidJson = lines.LastOrDefault(line => line.Trim().StartsWith("{") && line.Trim().EndsWith("}"));

                    if (lastValidJson != null)
                    {
                        json = lastValidJson;
                    }
                }

                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("message", out var messageElement) ||
                    !messageElement.TryGetProperty("content", out var contentElement))
                {
                    _logger.LogWarning("Unexpected response format from entity extraction");
                    return new List<string>();
                }

                var content = contentElement.GetString() ?? "";

                return content.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(e => e.Trim().Trim('"', '\'', '-', '*'))
                             .Where(e => e.Length > 1 && !string.IsNullOrWhiteSpace(e))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Take(10) // Limit to prevent excessive entities
                             .ToList();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, $"JSON parsing failed in entity extraction. Raw response might be malformed.");
                return new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract entities");
                return new List<string>();
            }
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
