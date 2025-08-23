using MEAI_GPT_API.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service.Models
{
    public class TextChunkingService
    {
        private readonly StringProcessingService _stringProcessor;
        private readonly ILogger<TextChunkingService> _logger;
        public TextChunkingService(ILogger<TextChunkingService> logger, StringProcessingService stringProcessing)
        {
            _logger = logger;
            _stringProcessor = stringProcessing;
        }
        public List<(string Text, string SourceFile, string SectionId, string Title)> ChunkText(
           string text, string sourceFile, int maxTokens = 2500)
        {
            text = _stringProcessor.CleanCopyPasteText(text);

            // ENHANCED: Comprehensive section patterns for ALL sections
            var sectionPatterns = new[]
            {
        @"^Section\s+(\d+)\s*[–\-—\u2013\u2014\u002D]?\s*(.+)$", // Standard format
        @"^Section\s*(\d+)\s*[:\-]?\s*(.*)$", // More flexible section format
        @"^SECTION\s+(\d+)\s*[:\-]?\s*(.*)$", // Uppercase section
        @"^(\d+)\.\s+(.+)$", // "1. Introduction", "2. Scope"
        @"^(\d+)\s+(.+)$", // "1 Introduction"
        @"^(\d+\.\d+)\s+(.+)$", // "1.1 Purpose"
        @"^(\d+\.\d+\.\d+)\s+(.+)$", // "1.1.1 Definition"
        @"^(\d+)\s*[:\-\.]\s*(.+)$" // Number with colon/dash/dot
    };

            // Rest of your chunking logic with enhanced logging
            var chunks = new List<(string Text, string SourceFile, string SectionId, string Title)>();
            var lines = text.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            var currentChunk = new StringBuilder();
            string currentSectionId = "";
            string currentTitle = "";
            int tokenCount = 0;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                bool foundSection = false;
                string detectedSectionId = "";
                string detectedTitle = "";

                // Check for section headers
                foreach (var pattern in sectionPatterns)
                {
                    var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        foundSection = true;
                        if (trimmed.ToLowerInvariant().StartsWith("section"))
                        {
                            detectedSectionId = $"Section {match.Groups[1].Value}";
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else
                        {
                            detectedSectionId = match.Groups[1].Value;
                            detectedTitle = match.Groups[2].Value.Trim();
                        }

                        _logger.LogInformation($"✅ Detected section: {detectedSectionId} - {detectedTitle}");
                        break;
                    }
                }

                if (foundSection)
                {
                    // Save previous chunk
                    if (currentChunk.Length > 0)
                    {
                        var completeChunk = BuildComprehensiveChunk(
                            currentSectionId, currentTitle, currentChunk.ToString(), "");
                        chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));

                        // Debug logging for any section
                        _logger.LogInformation($"🔍 STORING CHUNK:");
                        _logger.LogInformation($"Section: {currentSectionId} - {currentTitle}");
                        _logger.LogInformation($"Length: {completeChunk.Length}");
                    }

                    // Start new section
                    currentSectionId = detectedSectionId;
                    currentTitle = detectedTitle;
                    currentChunk.Clear();
                    currentChunk.AppendLine($"=== {currentSectionId}: {currentTitle} ===");
                    tokenCount = _stringProcessor.EstimateTokenCount($"{currentSectionId}: {currentTitle}");
                }

                // Add line to current chunk
                int lineTokens = _stringProcessor.EstimateTokenCount(trimmed);
                if (tokenCount + lineTokens > maxTokens && currentChunk.Length > 0)
                {
                    // Split large sections
                    var completeChunk = BuildComprehensiveChunk(
                        currentSectionId, currentTitle, currentChunk.ToString(), "");
                    chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));

                    currentChunk.Clear();
                    currentChunk.AppendLine($"=== {currentSectionId}: {currentTitle} (continued) ===");
                    tokenCount = _stringProcessor.EstimateTokenCount($"{currentSectionId}: {currentTitle} (continued)");
                }

                currentChunk.AppendLine(trimmed);
                tokenCount += lineTokens;
            }

            // Add final chunk
            if (currentChunk.Length > 0)
            {
                var completeChunk = BuildComprehensiveChunk(
                    currentSectionId, currentTitle, currentChunk.ToString(), "");
                chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));
            }

            _logger.LogInformation($"📄 Created {chunks.Count} enhanced chunks from {sourceFile}");

            // Log all sections found
            var allSections = chunks.Where(c => !string.IsNullOrEmpty(c.SectionId))
                .Select(c => c.SectionId).Distinct().ToList();
            _logger.LogInformation($"🎯 All sections found: {string.Join(", ", allSections)}");

            return chunks;
        }

        public string BuildComprehensiveChunk(string sectionId, string title, string content, string pendingContent)
        {
            var chunkBuilder = new StringBuilder();

            // Add comprehensive header
            chunkBuilder.AppendLine($"DOCUMENT SECTION: {sectionId}");
            chunkBuilder.AppendLine($"SECTION TITLE: {title}");
            chunkBuilder.AppendLine($"DOCUMENT TYPE: ISMS Policy");
            chunkBuilder.AppendLine("===== CONTENT =====");

            // Add any pending content from previous processing
            if (!string.IsNullOrEmpty(pendingContent))
            {
                chunkBuilder.AppendLine(pendingContent);
            }

            // Add main content
            chunkBuilder.AppendLine(content);

            // Add searchable keywords
            chunkBuilder.AppendLine("===== KEYWORDS =====");
            var keywords = GenerateSearchableKeywords(sectionId, title, content);
            chunkBuilder.AppendLine(string.Join(", ", keywords));

            return chunkBuilder.ToString();
        }
        public List<string> GenerateSearchableKeywords(string sectionId, string title, string content)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Section-specific keywords
            if (sectionId.Contains("6"))
            {
                keywords.Add("Section 6");
                keywords.Add("section six");
                keywords.Add("Physical Security");
                keywords.Add("physical security");
                keywords.Add("Secure Areas");
                keywords.Add("secure areas");
                keywords.Add("Area Level");
                keywords.Add("area level");
                keywords.Add("Access Control");
                keywords.Add("access control");
                keywords.Add("Device Protection");
                keywords.Add("device protection");
            }

            // Extract keywords from title
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in titleWords.Where(w => w.Length > 2))
                {
                    keywords.Add(word);
                }
            }

            // Extract important terms from content
            var importantTerms = new[]
            {
        "ISMS", "Information Security", "Management System", "Policy", "Procedure",
        "Employee", "Access", "Control", "Security", "Management", "Protection"
    };

            foreach (var term in importantTerms)
            {
                if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    keywords.Add(term);
                }
            }

            return keywords.Take(20).ToList(); // Limit keywords to prevent bloat
        }
        public List<string> GenerateSearchKeywords(string sectionId, string title, string documentType, string content)
        {
            var keywords = new List<string>();

            // Document type keywords
            keywords.Add(documentType.ToLower());

            // Section keywords
            if (!string.IsNullOrEmpty(sectionId))
            {
                keywords.Add(sectionId.ToLower());
                keywords.Add(sectionId.Replace("Section ", "section ").ToLower());
                keywords.Add(sectionId.Replace(" ", "").ToLower()); // "section6"

                // Extract number
                var match = Regex.Match(sectionId, @"(\d+(?:\.\d+)*)");
                if (match.Success)
                {
                    keywords.Add($"section {match.Groups[1].Value}");
                    keywords.Add($"section{match.Groups[1].Value}");
                }
            }

            // Title keywords
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = title.ToLower()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2);
                keywords.AddRange(titleWords);
            }

            // Content-based keywords
            var contentKeywords = ExtractImportantTerms(content);
            keywords.AddRange(contentKeywords);

            return keywords.Distinct().ToList();
        }
        public List<string> ExtractImportantTerms(string content)
        {
            var terms = new List<string>();
            var lowerContent = content.ToLower();

            // Common policy terms
            var policyTerms = new[]
            {
        "policy", "procedure", "rule", "regulation", "guideline",
        "employee", "management", "security", "information",
        "leave", "attendance", "performance", "training"
    };

            foreach (var term in policyTerms)
            {
                if (lowerContent.Contains(term))
                {
                    terms.Add(term);
                }
            }

            return terms;
        }
    }
}
