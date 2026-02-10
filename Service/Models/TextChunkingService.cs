using MEAI_GPT_API.Services;
using System.Text;
using System.Text.RegularExpressions;
using static Microsoft.Extensions.Logging.EventSource.LoggingEventSource;

namespace MEAI_GPT_API.Service.Models
{
    public class TextChunkingService
    {
        private readonly StringProcessingService _stringProcessor;
        private readonly ILogger<TextChunkingService> _logger;
        private readonly CompliancePolicyDetector _policyDetector;

        // Enhanced patterns for various document types including ISMS subtypes
        private readonly Dictionary<string, string[]> _documentTypePatterns = new()
        {
            ["ISMS_GENERAL"] = new[]
            {
                "ISMS", "Information Security", "Management System", "Policy", "Procedure",
                "Employee", "Access", "Control", "Security", "Management", "Protection",
                "Compliance", "Audit", "Risk", "Asset", "Incident", "Classification",
                "governance", "framework", "strategy", "awareness", "training",
                "organization", "roles", "responsibilities", "committee"
            },
            ["ISMS_TECHNICAL"] = new[]
            {
                "ISMS", "Information Security", "Technical", "Implementation", "Configuration",
                "Network", "Firewall", "Encryption", "Authentication", "Authorization",
                "Database", "Server", "Application", "Vulnerability", "Penetration",
                "Monitoring", "Logging", "Backup", "Recovery", "Patch", "Update",
                "Technical Controls", "Security Controls", "System", "Infrastructure"
            },
            ["POLICY"] = new[]
            {
                "ISMS", "Information Security", "Management System", "Policy", "Procedure",
                "Employee", "Access", "Control", "Security", "Management", "Protection",
                "Compliance", "Audit", "Risk", "Asset", "Incident", "Classification"
            },
            ["HR"] = new[]
            {
                "Employee", "Human Resources", "Leave", "Attendance", "Performance",
                "Training", "Recruitment", "Onboarding", "Appraisal", "Benefits",
                "Compensation", "Disciplinary", "Grievance", "Code of Conduct"
            },
            ["FINANCE"] = new[]
            {
                "Finance", "Accounting", "Budget", "Expense", "Revenue", "Invoice",
                "Payment", "Procurement", "Purchase", "Vendor", "Contract", "Cost"
            },
            ["OPERATIONS"] = new[]
            {
                "Operations", "Process", "Workflow", "Standard Operating Procedure",
                "Quality", "Production", "Manufacturing", "Supply Chain", "Logistics"
            },
            ["LEGAL"] = new[]
            {
                "Legal", "Contract", "Agreement", "Terms", "Conditions", "Compliance",
                "Regulatory", "Statutory", "License", "Intellectual Property"
            }
        };

        public TextChunkingService(ILogger<TextChunkingService> logger, StringProcessingService stringProcessing)
        {
            _logger = logger;
            _stringProcessor = stringProcessing;
            _policyDetector = new CompliancePolicyDetector();
        }

        private static readonly Regex[] CompiledSectionPatterns = new[]
        {
            // Standard sections
            new Regex(@"^(?:SECTION|Section)\s+(\d+(?:\.\d+)*)\s*[:\-]?\s*(.*)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            
            // NEW: Boxed content patterns
            new Regex(@"^(?:Questions?|Checklist|Guidelines?|Requirements?|Steps?|Procedures?)\s+(?:to|for)\s+(.+)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            
            // NEW: All-caps standalone headings (common in boxes)
            new Regex(@"^([A-Z][A-Z\s]{10,})$",
                RegexOptions.Compiled),
            
            // NEW: Introduction phrases
            new Regex(@"^(?:The following|Below are|These are)\s+(.+):$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            
            // Chapters, Appendices, etc.
            new Regex(@"^(?:CHAPTER|Chapter)\s+(\d+)\s*[:\-]?\s*(.*)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"^(?:APPENDIX|Appendix|ANNEXURE|Annexure)\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            
            // Generic numbered sections
            new Regex(@"^(\d+(?:\.\d+)*)\s*[:\-\.]?\s*(.+)$",
                RegexOptions.Compiled),
        };


        public List<(string Text, string SourceFile, string SectionId, string Title)> ChunkText(
            string text, string sourceFile, int maxTokens = 2500)
        {
            text = _stringProcessor.CleanCopyPasteText(text);

            var chunks = new List<(string Text, string SourceFile, string SectionId, string Title)>();
            var lines = text.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            var currentChunk = new StringBuilder();
            string currentSectionId = "";
            string currentTitle = "";
            int tokenCount = 0;
            string previousLine = "";
            bool inListContext = false;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Check for section header
                var (foundSection, detectedSectionId, detectedTitle) = DetectSectionHeader(trimmed);

                if (foundSection)
                {
                    // Save previous chunk
                    if (currentChunk.Length > 0)
                    {
                        var contentType = DetectContentType(currentTitle, currentChunk.ToString());
                        var completeChunk = BuildComprehensiveChunk(
                            currentSectionId, currentTitle, currentChunk.ToString(), contentType);
                        chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));

                        _logger.LogDebug($"Saved chunk: {currentSectionId} ({contentType})");
                    }

                    currentSectionId = detectedSectionId;
                    currentTitle = detectedTitle;
                    currentChunk.Clear();
                    currentChunk.AppendLine($"=== {currentSectionId}: {currentTitle} ===");
                    tokenCount = _stringProcessor.EstimateTokenCount($"{currentSectionId}: {currentTitle}");
                    inListContext = false;

                    _logger.LogDebug($"New section: {currentSectionId} - {currentTitle}");
                    previousLine = trimmed;
                    continue;
                }

                // ✅ CHECK: Is this a list item (not a new section)?
                if (IsListItem(trimmed, previousLine))
                {
                    inListContext = true;
                    _logger.LogTrace($"List item detected: {trimmed.Substring(0, Math.Min(50, trimmed.Length))}...");

                    // Add to current chunk without creating new section
                    currentChunk.AppendLine(trimmed);
                    tokenCount += _stringProcessor.EstimateTokenCount(trimmed);
                    previousLine = trimmed;
                    continue;
                }

                // Normal line processing
                int lineTokens = _stringProcessor.EstimateTokenCount(trimmed);

                // Smart splitting - avoid breaking lists
                if (ShouldSplitChunk(currentChunk, trimmed, tokenCount + lineTokens, maxTokens))
                {
                    // If in list, allow slightly larger chunk to keep list together
                    if (inListContext && tokenCount + lineTokens < maxTokens * 1.3)
                    {
                        _logger.LogDebug("Allowing larger chunk to preserve list integrity");
                        currentChunk.AppendLine(trimmed);
                        tokenCount += lineTokens;
                        previousLine = trimmed;
                        continue;
                    }

                    var contentType = DetectContentType(currentTitle, currentChunk.ToString());
                    var completeChunk = BuildComprehensiveChunk(
                        currentSectionId, currentTitle, currentChunk.ToString(), contentType);
                    chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));

                    currentChunk.Clear();
                    currentChunk.AppendLine($"=== {currentSectionId}: {currentTitle} (continued) ===");
                    tokenCount = _stringProcessor.EstimateTokenCount($"{currentSectionId}: {currentTitle} (continued)");
                    inListContext = false;
                }

                currentChunk.AppendLine(trimmed);
                tokenCount += lineTokens;
                previousLine = trimmed;
            }

            // Add final chunk
            if (currentChunk.Length > 0)
            {
                var contentType = DetectContentType(currentTitle, currentChunk.ToString());
                var completeChunk = BuildComprehensiveChunk(
                    currentSectionId, currentTitle, currentChunk.ToString(), contentType);
                chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));
            }

            _logger.LogInformation($"Created {chunks.Count} chunks from {Path.GetFileName(sourceFile)}");
            return chunks;
        }

        private bool IsListItem(string line, string previousLine)
        {
            var trimmed = line.Trim();

            // Pattern: "1", "2", "3" or "1.", "2.", "3." at start
            var match = Regex.Match(trimmed, @"^(\d+)[.\)]?\s+(.+)$");
            if (!match.Success)
                return false;

            var number = int.Parse(match.Groups[1].Value);
            var restOfLine = match.Groups[2].Value;

            // If previous line was also numbered, this is likely a list
            if (!string.IsNullOrEmpty(previousLine))
            {
                var prevMatch = Regex.Match(previousLine.Trim(), @"^(\d+)[.\)]?\s+");
                if (prevMatch.Success)
                {
                    var prevNumber = int.Parse(prevMatch.Groups[1].Value);
                    // Sequential numbering = list
                    if (number == prevNumber + 1)
                        return true;
                }
            }

            // Check if starts with common question/statement words (not section titles)
            var startsWithVerb = Regex.IsMatch(restOfLine,
                @"^(Is|Can|Will|Does|Do|Should|Must|Are|Have|Has|Would|Could|May|Might|Ensure|Verify|Check|Confirm)\b",
                RegexOptions.IgnoreCase);

            if (startsWithVerb)
                return true;

            // Check for common list patterns
            var listPatterns = new[]
            {
                @"^All\s+", @"^Each\s+", @"^Every\s+", @"^Any\s+",
                @"^No\s+", @"^Only\s+", @"^Never\s+", @"^Always\s+",
                @".+\?$", // Ends with question mark
            };

            return listPatterns.Any(p => Regex.IsMatch(restOfLine, p, RegexOptions.IgnoreCase));
        }

        private string DetectContentType(string sectionTitle, string content)
        {
            var combined = $"{sectionTitle} {content}".ToLower();

            // Checklist/Questions
            if (Regex.IsMatch(combined, @"(questions?|checklist|test for|assess|verify|check if)"))
            {
                var hasNumbers = Regex.Matches(content, @"^\d+[.\)]?\s+", RegexOptions.Multiline).Count;
                if (hasNumbers >= 3)
                    return "CHECKLIST";
            }

            // Table
            if (content.Contains("\t\t") || Regex.IsMatch(content, @"\|[^|]+\|[^|]+\|"))
                return "TABLE";

            // Bullet list
            if (Regex.IsMatch(content, @"^[•●○▪▫■□◦‣⁃-]\s+", RegexOptions.Multiline))
                return "BULLET_LIST";

            // Numbered list
            var numberedItems = Regex.Matches(content, @"^\d+[.\)]\s+", RegexOptions.Multiline).Count;
            if (numberedItems >= 3)
                return "NUMBERED_LIST";

            return "TEXT";
        }


        private (bool found, string sectionId, string title) DetectSectionHeader(string line)
        {
            foreach (var regex in CompiledSectionPatterns)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    string sectionId = match.Groups[1].Value;
                    string title = match.Groups.Count > 2 ? match.Groups[2].Value.Trim() : "";

                    // Determine section type
                    if (line.Contains("Section", StringComparison.OrdinalIgnoreCase))
                        return (true, $"Section {sectionId}", title);
                    else if (line.Contains("Chapter", StringComparison.OrdinalIgnoreCase))
                        return (true, $"Chapter {sectionId}", title);
                    else if (line.Contains("Questions", StringComparison.OrdinalIgnoreCase))
                        return (true, $"Questions: {sectionId}", title);
                    else if (line.Contains("Checklist", StringComparison.OrdinalIgnoreCase))
                        return (true, $"Checklist: {sectionId}", title);
                    else if (Regex.IsMatch(line, @"^[A-Z][A-Z\s]{10,}$"))
                        return (true, "Special Section", line.Trim());
                    else
                        return (true, sectionId, title);
                }
            }

            return (false, "", "");
        }

        private bool ShouldSplitChunk(StringBuilder currentChunk, string nextLine, int totalTokens, int maxTokens)
        {
            if (totalTokens < maxTokens * 0.8)
                return false;

            if (totalTokens > maxTokens)
            {
                var chunkText = currentChunk.ToString();
                var lastLines = chunkText.Split('\n').TakeLast(3).ToArray();

                if (lastLines.Any(string.IsNullOrWhiteSpace))
                    return true;

                if (lastLines.Length > 0 && lastLines[^1].TrimEnd().EndsWith('.'))
                    return true;

                return totalTokens > maxTokens * 1.2;
            }

            return false;
        }

        public string BuildComprehensiveChunk(
             string sectionId,
             string title,
             string content,
             string contentType = "TEXT")
        {
            var chunkBuilder = new StringBuilder();

            string documentType = DetectDocumentType(sectionId, title, content);

            // Header with content type
            chunkBuilder.AppendLine($"DOCUMENT SECTION: {sectionId}");
            chunkBuilder.AppendLine($"SECTION TITLE: {title}");
            chunkBuilder.AppendLine($"DOCUMENT TYPE: {documentType}");
            chunkBuilder.AppendLine($"CONTENT TYPE: {contentType}");

            if (contentType == "CHECKLIST" || contentType == "NUMBERED_LIST")
            {
                var itemCount = Regex.Matches(content, @"^\d+[.\)]?\s+", RegexOptions.Multiline).Count;
                chunkBuilder.AppendLine($"LIST ITEMS: {itemCount}");
            }

            chunkBuilder.AppendLine("===== CONTENT =====");
            chunkBuilder.AppendLine(content);

            chunkBuilder.AppendLine("===== KEYWORDS =====");
            var keywords = GenerateSearchableKeywords(sectionId, title, content, contentType);

            // Enhanced keywords based on content type
            chunkBuilder.AppendLine("===== KEYWORDS =====");
            var policyKeywords = _policyDetector.GetPolicyKeywords(documentType);
            keywords.AddRange(policyKeywords.Take(10)); // Add top 10 policy keywords

            chunkBuilder.AppendLine(string.Join(", ", keywords.Distinct()));

            return chunkBuilder.ToString();
        }

        private List<string> GenerateSearchableKeywords(
            string sectionId,
            string title,
            string content,
            string contentType)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Section keywords
            if (!string.IsNullOrEmpty(sectionId))
            {
                keywords.Add(sectionId.ToLower());
                var sectionNum = Regex.Match(sectionId, @"\d+(?:\.\d+)*").Value;
                if (!string.IsNullOrEmpty(sectionNum))
                {
                    keywords.Add($"section {sectionNum}");
                }
            }

            // Title keywords
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !IsStopWord(w));
                keywords.UnionWith(titleWords.Select(w => w.ToLower()));
            }

            // Content-type specific keywords
            switch (contentType)
            {
                case "CHECKLIST":
                    keywords.Add("checklist");
                    keywords.Add("questions");
                    keywords.Add("test");
                    keywords.Add("assessment");
                    keywords.Add("verification");
                    keywords.Add("self-check");
                    break;

                case "NUMBERED_LIST":
                    keywords.Add("list");
                    keywords.Add("items");
                    keywords.Add("points");
                    break;

                case "TABLE":
                    keywords.Add("table");
                    keywords.Add("data");
                    keywords.Add("comparison");
                    break;

                case "BULLET_LIST":
                    keywords.Add("list");
                    keywords.Add("points");
                    keywords.Add("items");
                    break;
            }

            // Extract important terms from content
            var contentKeywords = ExtractImportantTerms(content);
            keywords.UnionWith(contentKeywords.Select(k => k.ToLower()));

            return keywords
                .Where(k => k.Length >= 2)
                .Where(k => !IsStopWord(k))
                .Take(50)
                .ToList();
        }
        private bool IsStopWord(string word)
        {
            var stopWords = new[] {"the", "and", "or", "but", "for", "with", "from", "this", "that",
            "document", "section", "content", "file", "page", "text"};
            return stopWords.Contains(word.ToLower());
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "or", "but", "for", "with", "from", "this", "that",
            "document", "section", "content", "file", "page", "text"
        };
        private void ExtractKeywordsWithScore(string text, Dictionary<string, int> scores, int weight)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var words = Regex.Split(text, @"[\s\-_/\\,;:()]+")
                .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length > 2)
                .Where(w => !StopWords.Contains(w));

            foreach (var word in words)
            {
                var cleanWord = word.Trim().ToLower();
                if (IsValidKeyword(cleanWord))
                {
                    scores[cleanWord] = scores.GetValueOrDefault(cleanWord, 0) + weight;
                }
            }
        }

        private string CleanTextForKeywords(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // COMPREHENSIVE cleaning in one pass
            var cleaningPatterns = new (string pattern, string replacement)[]
            {
                // File extensions
                (@"\.(pdf|doc|docx|txt|xlsx?)$", ""),
                // GUIDs
                (@"[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12}", ""),
                // Your specific file pattern
                (@"_[A-F0-9]{8}_\d+_[A-F0-9]{8}", ""),
                // Revision numbers
                (@"_Rev\d+_V\d+", ""),
                (@"\(\d+\)_", ""),
                // System namespaces
                (@"System\.Collections\.Generic\.\w+", ""),
                (@"System\.\w+", ""),
                // Long hex strings
                (@"\b[A-Fa-f0-9]{16,}\b", "")
            };

            foreach (var (pattern, replacement) in cleaningPatterns)
            {
                input = Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase);
            }

            // Normalize whitespace
            return Regex.Replace(input, @"\s+", " ").Trim();
        }

        private string ExtractSectionNumber(string sectionId)
        {
            var match = Regex.Match(sectionId, @"(\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : "";
        }

        private bool IsValidKeyword(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2)
                return false;

            // COMPREHENSIVE filtering
            var invalidPatterns = new[]
            {
                @"System\.",                              // .NET namespaces
                @"_[A-F0-9]{8}_\d+_[A-F0-9]{8}",         // File patterns
                @"[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}",        // GUIDs
                @"_Rev\d+_V\d+",                         // Versions
                @"^\d+$",                                 // Pure numbers
                @"^[A-Fa-f0-9]{8,}$",                    // Hex strings
                @"^\W+$",                                 // Special chars only
                @"\.(pdf|doc|docx|txt|xlsx?)$",          // Extensions
            };

            if (invalidPatterns.Any(p => Regex.IsMatch(keyword, p, RegexOptions.IgnoreCase)))
                return false;

            // Check against stop words
            if (StopWords.Contains(keyword))
                return false;

            // Must contain at least one letter
            return keyword.Any(char.IsLetter);
        }

        private bool IsSystemArtifact(string text)
        {
            var artifacts = new[]
            {
                "System.Collections", "System.Single", "System.String",
                ".pdf", ".doc", ".docx", ".txt", ".xlsx",
                "List`1", "Int32", "Boolean", "Single"
            };

            return artifacts.Any(a => text.Contains(a, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> ExtractImportantPhrases(string text)
        {
            var phrases = new List<string>();

            // Extract 2-3 word meaningful phrases
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length - 1; i++)
            {
                if (words[i].Length > 2 && words[i + 1].Length > 2)
                {
                    var phrase = $"{words[i]} {words[i + 1]}";
                    if (IsImportantPhrase(phrase))
                    {
                        phrases.Add(phrase);
                    }
                }

                // 3-word phrases
                if (i < words.Length - 2 && words[i + 2].Length > 2)
                {
                    var phrase = $"{words[i]} {words[i + 1]} {words[i + 2]}";
                    if (IsImportantPhrase(phrase))
                    {
                        phrases.Add(phrase);
                    }
                }
            }

            return phrases;
        }

        private bool IsImportantPhrase(string phrase)
        {
            var importantPhrases = new[]
            {
                "physical security", "access control", "secure areas", "device protection",
                "information security", "management system", "technical controls",
                "security controls", "risk management", "incident management"
            };

            return importantPhrases.Any(ip => phrase.ToLower().Contains(ip));
        }

        private string DetectDocumentType(string sectionId, string title, string content)
        {
            return _policyDetector.DetectDocumentType(sectionId, title, content);
        }

        private List<string> GetDocumentTypeKeywords(string documentType)
        {
            var keywords = new List<string>();

            if (documentType.Contains("ISMS"))
            {
                keywords.AddRange(_documentTypePatterns["ISMS_GENERAL"].Select(k => k.ToLower()));

                if (documentType.Contains("Technical"))
                    keywords.AddRange(_documentTypePatterns["ISMS_TECHNICAL"].Select(k => k.ToLower()));

                return keywords.Distinct().ToList();
            }

            var typeKey = documentType.ToUpper().Split(' ')[0];
            if (_documentTypePatterns.ContainsKey(typeKey))
                return _documentTypePatterns[typeKey].Select(k => k.ToLower()).ToList();

            return new List<string> { "policy", "document" };
        }

        public List<string> GenerateSearchKeywords(string sectionId, string title, string documentType, string content)
        {
            var keywords = new List<string>();

            // Document type keywords
            keywords.Add(documentType.ToLower());

            // Extract document type variations
            var docTypeParts = documentType.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            keywords.AddRange(docTypeParts);

            // Section keywords - ENHANCED
            if (!string.IsNullOrEmpty(sectionId))
            {
                keywords.Add(sectionId.ToLower());
                keywords.Add(sectionId.Replace("Section ", "section ").ToLower());
                keywords.Add(sectionId.Replace(" ", "").ToLower());

                // Handle different section types
                if (sectionId.ToLower().Contains("annexure"))
                {
                    keywords.Add("annexure");
                    keywords.Add("attachment");
                    keywords.Add("appendix");
                }
                if (sectionId.ToLower().Contains("appendix"))
                {
                    keywords.Add("appendix");
                    keywords.Add("attachment");
                    keywords.Add("annexure");
                }
                if (sectionId.ToLower().Contains("schedule"))
                {
                    keywords.Add("schedule");
                    keywords.Add("table");
                    keywords.Add("format");
                }

                // Extract numbers and alphanumeric identifiers
                var matches = Regex.Matches(sectionId, @"([A-Z\d]+(?:\.\d+)*)");
                foreach (Match match in matches)
                {
                    keywords.Add($"section {match.Groups[1].Value}");
                    keywords.Add($"section{match.Groups[1].Value}");
                }
            }

            // Title keywords - ENHANCED
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = Regex.Split(title.ToLower(), @"[\s\-_/\\,;:()]+")
                    .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length > 2);
                keywords.AddRange(titleWords);

                // Add title as complete phrase
                keywords.Add(title.ToLower());
            }

            // Content-based keywords - MASSIVELY ENHANCED
            var contentKeywords = ExtractImportantTerms(content);
            keywords.AddRange(contentKeywords);

            return keywords.Distinct().ToList();
        }

        public List<string> ExtractImportantTerms(string content)
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lowerContent = content.ToLower();

            // MASSIVELY EXPANDED keyword extraction
            var allTermCategories = new Dictionary<string, string[]>
            {
                ["General Policy"] = new[]
                {
                    "policy", "procedure", "rule", "regulation", "guideline", "standard",
                    "requirement", "compliance", "mandatory", "optional", "prohibited"
                },

                ["Organizational"] = new[]
                {
                    "employee", "staff", "management", "supervisor", "manager", "director",
                    "department", "organization", "company", "team", "role", "responsibility"
                },

                ["Security & ISMS"] = new[]
                {
                    "security", "information", "isms", "protection", "confidentiality",
                    "integrity", "availability", "access control", "authentication",
                    "authorization", "incident", "breach", "vulnerability", "risk",
                    "threat", "asset", "classification", "backup", "recovery"
                },

                ["HR & Employment"] = new[]
                {
                    "leave", "attendance", "performance", "training", "recruitment",
                    "onboarding", "appraisal", "benefits", "compensation", "salary",
                    "disciplinary", "grievance", "termination", "resignation",
                    "code of conduct", "ethics", "harassment", "discrimination"
                },

                ["Financial"] = new[]
                {
                    "finance", "accounting", "budget", "expense", "revenue", "invoice",
                    "payment", "procurement", "purchase", "vendor", "contract",
                    "cost", "audit", "financial control", "approval", "authorization"
                },

                ["Legal & Compliance"] = new[]
                {
                    "legal", "compliance", "regulatory", "statutory", "license",
                    "agreement", "terms", "conditions", "intellectual property",
                    "copyright", "trademark", "confidentiality agreement"
                },

                ["Operations"] = new[]
                {
                    "operations", "process", "workflow", "procedure", "quality",
                    "production", "manufacturing", "supply chain", "logistics",
                    "standard operating procedure", "sop", "work instruction"
                },

                ["Document Structure"] = new[]
                {
                    "section", "chapter", "article", "clause", "paragraph",
                    "appendix", "annexure", "annex", "schedule", "attachment",
                    "supplement", "addendum", "exhibit"
                }
            };

            // Extract terms from all categories
            foreach (var category in allTermCategories)
            {
                foreach (var term in category.Value)
                {
                    if (lowerContent.Contains(term))
                    {
                        terms.Add(term);

                        // Add variations
                        terms.Add(term.Replace(" ", ""));
                        if (term.Contains(" "))
                        {
                            var parts = term.Split(' ');
                            foreach (var part in parts.Where(p => p.Length > 2))
                            {
                                terms.Add(part);
                            }
                        }
                    }
                }
            }

            // Extract capitalized terms (likely important concepts)
            var capitalizedTerms = Regex.Matches(content, @"\b[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,})*\b")
                .Cast<Match>()
                .Select(m => m.Value.ToLower())
                .Where(t => t.Length > 3);

            foreach (var term in capitalizedTerms)
            {
                terms.Add(term);
            }

            // Extract quoted terms (often important definitions)
            var quotedTerms = Regex.Matches(content, @"""([^""]{3,})""")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.ToLower())
                .Where(t => t.Length > 2);

            foreach (var term in quotedTerms)
            {
                terms.Add(term);
            }

            // Extract numbered items (procedures, steps)
            var numberedItems = Regex.Matches(content, @"(?:^|\n)\s*\d+[\.\)]\s*([^\n]{10,})")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.ToLower().Trim());

            foreach (var item in numberedItems)
            {
                var itemWords = item.Split(' ').Where(w => w.Length > 3).Take(3);
                foreach (var word in itemWords)
                {
                    terms.Add(word);
                }
            }

            // Extract terms in parentheses (often definitions or clarifications)
            var parentheticalTerms = Regex.Matches(content, @"\(([^)]{3,})\)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.ToLower())
                .Where(t => !Regex.IsMatch(t, @"^\d+$")); // Exclude pure numbers

            foreach (var term in parentheticalTerms)
            {
                var words = term.Split(' ').Where(w => w.Length > 2);
                foreach (var word in words)
                {
                    terms.Add(word);
                }
            }

            return terms.Take(100).ToList(); // Increased limit for comprehensive coverage
        }

        public List<string> ExtractComprehensiveTerms(string content)
        {
            // This is the same as ExtractImportantTerms but with a different name
            // to maintain backward compatibility
            return ExtractImportantTerms(content);
        }

        private List<string> ExtractImportantTermsFromContent(string content)
        {
            var terms = new List<string>();
            var lowerContent = content.ToLowerInvariant();

            // Look for policy-specific patterns
            var patterns = new[]
            {
        @"\b(\w+)\s+policy\b",           // "leave policy", "safety policy"
        @"\b(\w+)\s+procedure\b",        // "hiring procedure", "audit procedure"
        @"\b(\w+)\s+management\b",       // "risk management", "performance management"
        @"\b(\w+)\s+requirements?\b",    // "training requirements", "compliance requirement"
        @"\b(\w+)\s+process\b",          // "approval process", "review process"
        @"\b(\w+)\s+guidelines?\b",      // "safety guidelines", "conduct guidelines"
    };

            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(lowerContent, pattern);
                foreach (Match match in matches)
                {
                    var term = match.Groups[1].Value;
                    if (term.Length > 2 && !TextUtils.IsCommonWord(term))
                    {
                        terms.Add(term);
                    }
                }
            }

            // Extract frequently mentioned nouns
            var words = lowerContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3 && !TextUtils.IsCommonWord(w))
                .GroupBy(w => w)
                .Where(g => g.Count() > 2) // Mentioned at least 3 times
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key);

            terms.AddRange(words);

            return terms.Distinct().Take(15).ToList();
        }

        public List<string> ExtractKeywordsFromSectionContent(string content, string sectionTitle)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add title words
            if (!string.IsNullOrEmpty(sectionTitle))
            {
                var titleWords = sectionTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !TextUtils.IsCommonWord(w));
                keywords.UnionWith(titleWords);
            }

            // Extract important nouns and phrases from content
            var importantTerms = ExtractImportantTermsFromContent(content);
            keywords.UnionWith(importantTerms);

            return keywords.Take(10).ToList(); // Limit to most relevant keywords
        }
    }
}