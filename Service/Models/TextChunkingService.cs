using MEAI_GPT_API.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service.Models
{
    public class TextChunkingService
    {
        private readonly StringProcessingService _stringProcessor;
        private readonly ILogger<TextChunkingService> _logger;

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
        }

        public List<(string Text, string SourceFile, string SectionId, string Title)> ChunkText(
           string text, string sourceFile, int maxTokens = 2500)
        {
            text = _stringProcessor.CleanCopyPasteText(text);

            // ENHANCED: Comprehensive section patterns for ALL document types
            var sectionPatterns = new[]
            {
                // Standard section formats
                @"^Section\s+(\d+(?:\.\d+)*)\s*[–\-—\u2013\u2014\u002D]?\s*(.+)$",
                @"^Section\s*(\d+(?:\.\d+)*)\s*[:\-]?\s*(.*)$",
                @"^SECTION\s+(\d+(?:\.\d+)*)\s*[:\-]?\s*(.*)$",
                
                // Numbered sections
                @"^(\d+)\.\s+(.+)$",
                @"^(\d+)\s+(.+)$",
                @"^(\d+\.\d+)\s+(.+)$",
                @"^(\d+\.\d+\.\d+)\s+(.+)$",
                @"^(\d+)\s*[:\-\.]\s*(.+)$",
                
                // Chapter formats
                @"^Chapter\s+(\d+)\s*[:\-]?\s*(.*)$",
                @"^CHAPTER\s+(\d+)\s*[:\-]?\s*(.*)$",
                
                // Article formats
                @"^Article\s+(\d+)\s*[:\-]?\s*(.*)$",
                @"^ARTICLE\s+(\d+)\s*[:\-]?\s*(.*)$",
                
                // Appendix and Annexure formats
                @"^Appendix\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                @"^APPENDIX\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                @"^Annexure\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                @"^ANNEXURE\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                @"^Annex\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                @"^ANNEX\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                
                // Schedule formats
                @"^Schedule\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                @"^SCHEDULE\s+([A-Z\d]+)\s*[:\-]?\s*(.*)$",
                
                // Part formats
                @"^Part\s+([IVX\d]+)\s*[:\-]?\s*(.*)$",
                @"^PART\s+([IVX\d]+)\s*[:\-]?\s*(.*)$",
                
                // Title formats
                @"^Title\s+(\d+)\s*[:\-]?\s*(.*)$",
                @"^TITLE\s+(\d+)\s*[:\-]?\s*(.*)$",
                
                // Clause formats
                @"^Clause\s+(\d+(?:\.\d+)*)\s*[:\-]?\s*(.*)$",
                @"^CLAUSE\s+(\d+(?:\.\d+)*)\s*[:\-]?\s*(.*)$",
                
                // Generic heading patterns (catch-all)
                @"^([A-Z][A-Z\s]{3,})\s*[:\-]?\s*$", // ALL CAPS headings
                @"^([A-Z][a-z\s]{10,})\s*[:\-]?\s*$" // Title Case long headings
            };

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

                // Check for section headers with improved detection
                foreach (var pattern in sectionPatterns)
                {
                    var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        foundSection = true;

                        // Determine section type and format appropriately
                        if (trimmed.ToLowerInvariant().Contains("section"))
                        {
                            detectedSectionId = $"Section {match.Groups[1].Value}";
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else if (trimmed.ToLowerInvariant().Contains("appendix") ||
                                trimmed.ToLowerInvariant().Contains("annexure") ||
                                trimmed.ToLowerInvariant().Contains("annex"))
                        {
                            string prefix = trimmed.Split(' ')[0];
                            detectedSectionId = $"{prefix} {match.Groups[1].Value}";
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else if (trimmed.ToLowerInvariant().Contains("chapter"))
                        {
                            detectedSectionId = $"Chapter {match.Groups[1].Value}";
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else if (trimmed.ToLowerInvariant().Contains("schedule"))
                        {
                            detectedSectionId = $"Schedule {match.Groups[1].Value}";
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else if (match.Groups.Count >= 3)
                        {
                            detectedSectionId = match.Groups[1].Value;
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else
                        {
                            detectedSectionId = match.Groups[1].Value;
                            detectedTitle = "";
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

            // Detect document type from content
            string documentType = DetectDocumentType(sectionId, title, content);

            // Add comprehensive header
            chunkBuilder.AppendLine($"DOCUMENT SECTION: {sectionId}");
            chunkBuilder.AppendLine($"SECTION TITLE: {title}");
            chunkBuilder.AppendLine($"DOCUMENT TYPE: {documentType}");
            chunkBuilder.AppendLine("===== CONTENT =====");

            // Add any pending content from previous processing
            if (!string.IsNullOrEmpty(pendingContent))
            {
                chunkBuilder.AppendLine(pendingContent);
            }

            // Add main content
            chunkBuilder.AppendLine(content);

            // Add searchable keywords - ENHANCED
            chunkBuilder.AppendLine("===== KEYWORDS =====");
            var keywords = GenerateSearchableKeywords(sectionId, title, content);
            chunkBuilder.AppendLine(string.Join(", ", keywords));

            // Add document type specific keywords
            chunkBuilder.AppendLine("===== DOCUMENT_TYPE_KEYWORDS =====");
            var typeKeywords = GetDocumentTypeKeywords(documentType);
            chunkBuilder.AppendLine(string.Join(", ", typeKeywords));

            return chunkBuilder.ToString();
        }

        public List<string> GenerateSearchableKeywords(string sectionId, string title, string content)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Clean inputs first to remove system artifacts
            sectionId = CleanTextForKeywords(sectionId ?? "");
            title = CleanTextForKeywords(title ?? "");
            content = CleanTextForKeywords(content ?? "");

            // Extract keywords from section ID
            if (!string.IsNullOrEmpty(sectionId))
            {
                keywords.Add(sectionId.ToLower());

                // Add section number variations
                var sectionNumber = ExtractSectionNumber(sectionId);
                if (!string.IsNullOrEmpty(sectionNumber))
                {
                    keywords.Add($"section {sectionNumber}");
                    keywords.Add($"section{sectionNumber}");
                    keywords.Add(sectionNumber);
                }

                // Handle special section types
                if (sectionId.ToLower().Contains("annexure") ||
                    sectionId.ToLower().Contains("appendix") ||
                    sectionId.ToLower().Contains("annex"))
                {
                    keywords.Add("annexure");
                    keywords.Add("appendix");
                    keywords.Add("attachment");
                    keywords.Add("supplement");
                }

                if (sectionId.ToLower().Contains("schedule"))
                {
                    keywords.Add("schedule");
                    keywords.Add("table");
                    keywords.Add("list");
                }
            }

            // Extract keywords from title - CLEANED
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = Regex.Split(title, @"[\s\-_/\\,;:()]+")
                    .Where(w => !string.IsNullOrWhiteSpace(w) && w.Length > 2 && IsValidKeyword(w))
                    .Select(w => w.Trim().ToLower());

                foreach (var word in titleWords)
                {
                    keywords.Add(word);
                }

                // Add important phrases from title
                var titlePhrases = ExtractImportantPhrases(title);
                foreach (var phrase in titlePhrases)
                {
                    keywords.Add(phrase.ToLower());
                }
            }

            // Extract comprehensive terms from content - CLEANED
            var contentKeywords = ExtractComprehensiveTerms(content)
                .Where(k => IsValidKeyword(k) && !IsSystemArtifact(k))
                .Select(k => k.ToLower());

            foreach (var keyword in contentKeywords)
            {
                keywords.Add(keyword);
            }

            // Add contextual keywords based on document structure
            if (content.ToLower().Contains("policy"))
                keywords.Add("policy document");
            if (content.ToLower().Contains("procedure"))
                keywords.Add("procedure document");

            // Final cleanup and return
            var cleanedKeywords = keywords
                .Where(k => IsValidKeyword(k) && !IsSystemArtifact(k))
                .ToList();

            return cleanedKeywords;
        }
        private string CleanTextForKeywords(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // Remove file extensions and system artifacts
            input = Regex.Replace(input, @"\.(?:pdf|doc|docx|txt|xlsx?)$", "", RegexOptions.IgnoreCase);

            // Remove file paths and GUIDs
            input = Regex.Replace(input, @"[A-Fa-f0-9]{8}-?[A-Fa-f0-9]{4}-?[A-Fa-f0-9]{4}-?[A-Fa-f0-9]{4}-?[A-Fa-f0-9]{12}", "");
            input = Regex.Replace(input, @"_[A-F0-9]{8}_\d+_[A-F0-9]{8}", ""); // Your specific pattern

            // Remove revision numbers and version info
            input = Regex.Replace(input, @"_Rev\d+_V\d+", "", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\(\d+\)_", "");

            // Clean whitespace
            input = Regex.Replace(input, @"\s+", " ").Trim();

            return input;
        }

        private string ExtractSectionNumber(string sectionId)
        {
            var match = Regex.Match(sectionId, @"(\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : "";
        }

        private bool IsValidKeyword(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2) return false;

            // Filter out common stop words and system artifacts
            var invalidPatterns = new[]
            {
                @"^System\.Collections\.Generic\.List",
                @"^System\.",
                @"^\d+$", // Pure numbers only
                @"^[A-Fa-f0-9]{8,}$", // Long hex strings (GUIDs)
                @"^\W+$" // Only special characters
            };

            return !invalidPatterns.Any(pattern => Regex.IsMatch(keyword, pattern));
        }

        private bool IsSystemArtifact(string text)
        {
            var systemArtifacts = new[]
            {
                "System.Collections.Generic.List",
                "System.Single",
                ".pdf", ".doc", ".docx", ".txt"
            };

            return systemArtifacts.Any(artifact => text.Contains(artifact, StringComparison.OrdinalIgnoreCase));
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
            var combinedText = $"{sectionId} {title} {content}".ToLower();

            // ENHANCED: Check for ISMS subtypes FIRST (more specific)
            if (combinedText.Contains("isms") || combinedText.Contains("information security"))
            {
                // Check for technical indicators
                var technicalIndicators = new[]
                {
                    "technical", "implementation", "configuration", "network", "firewall",
                    "encryption", "database", "server", "application", "system",
                    "infrastructure", "monitoring", "logging", "patch", "vulnerability",
                    "penetration", "technical controls", "security controls", "backup",
                    "recovery", "authentication", "authorization", "technical implementation"
                };

                // Check for general/governance indicators
                var generalIndicators = new[]
                {
                    "general", "governance", "framework", "strategy", "awareness",
                    "training", "organization", "roles", "responsibilities", "committee",
                    "management", "overview", "introduction", "scope", "objectives",
                    "policy statement", "governance structure", "organizational"
                };

                bool hasTechnical = technicalIndicators.Any(indicator => combinedText.Contains(indicator));
                bool hasGeneral = generalIndicators.Any(indicator => combinedText.Contains(indicator));

                // Determine ISMS subtype
                if (hasTechnical && !hasGeneral)
                    return "ISMS Technical Policy";
                else if (hasGeneral && !hasTechnical)
                    return "ISMS General Policy";
                else if (hasTechnical && hasGeneral)
                    return "ISMS Comprehensive Policy"; // Contains both
                else
                    return "ISMS Policy"; // Default ISMS
            }

            // Check for other document types
            if (combinedText.Contains("human resource") || combinedText.Contains("hr policy"))
                return "HR Policy";
            if (combinedText.Contains("finance") || combinedText.Contains("accounting"))
                return "Finance Policy";
            if (combinedText.Contains("operations") || combinedText.Contains("operational"))
                return "Operations Policy";
            if (combinedText.Contains("legal") || combinedText.Contains("compliance"))
                return "Legal Policy";
            if (combinedText.Contains("safety") || combinedText.Contains("health"))
                return "Safety Policy";
            if (combinedText.Contains("quality"))
                return "Quality Policy";
            if (combinedText.Contains("procurement") || combinedText.Contains("purchase"))
                return "Procurement Policy";

            // Default fallback
            return "Policy Document";
        }

        private List<string> GetDocumentTypeKeywords(string documentType)
        {
            var keywords = new List<string>();

            // Handle ISMS subtypes
            if (documentType.Contains("ISMS"))
            {
                // Common ISMS keywords
                keywords.AddRange(_documentTypePatterns["ISMS_GENERAL"].Select(k => k.ToLower()));

                if (documentType.Contains("Technical"))
                {
                    // Add technical-specific keywords
                    keywords.AddRange(_documentTypePatterns["ISMS_TECHNICAL"].Select(k => k.ToLower()));
                    keywords.Add("isms technical");
                    keywords.Add("technical policy");
                    keywords.Add("technical implementation");
                    keywords.Add("technical controls");
                }
                else if (documentType.Contains("General"))
                {
                    // Add general-specific keywords  
                    keywords.Add("isms general");
                    keywords.Add("general policy");
                    keywords.Add("governance");
                    keywords.Add("framework");
                    keywords.Add("strategy");
                }

                // Always add base ISMS keywords
                keywords.Add("isms");
                keywords.Add("information security management system");
                return keywords.Distinct().ToList();
            }

            // Handle other document types
            var typeKey = documentType.ToUpper().Split(' ')[0];
            if (_documentTypePatterns.ContainsKey(typeKey))
            {
                return _documentTypePatterns[typeKey].Select(k => k.ToLower()).ToList();
            }

            return new List<string> { "policy", "document", "procedure" };
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