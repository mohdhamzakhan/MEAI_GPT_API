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
        // ─────────────────────────────────────────────────────────────────
        // Grade/position clarification (Supervisor & above vs. below
        // Supervisor / Direct vs. Indirect category).
        //
        // Several MEAI policies (e.g. the Settlement Policy's Annexure-I
        // "Indirect Category, Jr. Supervisor & above" vs Annexure-II
        // "Direct Category, below Jr. Supervisor") define materially
        // different provisions per employee grade. Answering with only one
        // grade's provisions (or silently blending both) risks giving a
        // Direct-category employee the wrong entitlement/process. Instead
        // of guessing, DynamicRagService uses these two methods to detect
        // when retrieved chunks span both grades and pause to ask the user
        // which one applies, before generating the actual answer.
        // ─────────────────────────────────────────────────────────────────

        private static readonly (string Label, string[] Patterns)[] GradeTiers = new[]
        {
            ("Supervisor and above", new[] {
                @"jr\.?\s*supervisor\s*(&|and)\s*above",
                @"supervisor\s*(&|and)\s*above",
                @"indirect\s*category",
            }),
            ("Below Supervisor", new[] {
                @"below\s*jr\.?\s*supervisor",
                @"below\s*supervisor",
                @"direct\s*category",
            }),
        };

        /// <summary>
        /// True when the retrieved chunks contain provisions for BOTH grade
        /// tiers (e.g. a chunk mentioning "Jr. Supervisor & above" AND
        /// another mentioning "below Jr. Supervisor"/"Direct category") --
        /// i.e. the answer genuinely depends on which grade the user is,
        /// not just background noise from an unrelated chunk.
        /// </summary>
        public bool HasGradeSpecificContent(List<RelevantChunk> chunks)
        {
            if (chunks == null || !chunks.Any()) return false;

            var combinedText = string.Join(" ", chunks.Select(c => c.Text)).ToLowerInvariant();

            bool tierAPresent = GradeTiers[0].Patterns.Any(p => Regex.IsMatch(combinedText, p, RegexOptions.IgnoreCase));
            bool tierBPresent = GradeTiers[1].Patterns.Any(p => Regex.IsMatch(combinedText, p, RegexOptions.IgnoreCase));

            return tierAPresent && tierBPresent;
        }

        /// <summary>
        /// Attempts to read an employee grade out of free text. Returns null
        /// if no grade is recognizable, so the caller knows to keep asking
        /// (or move on) rather than guessing.
        /// </summary>
        /// <param name="text">The user's message.</param>
        /// <param name="allowNumberedOptions">
        /// Only pass true when this text is a direct reply to the "1.
        /// Supervisor and above / 2. Below Supervisor" clarification
        /// question we actually asked. When false (e.g. scanning a fresh,
        /// unprompted question for a volunteered grade), a bare "1" or "2"
        /// is NOT treated as a grade answer -- otherwise any question that
        /// happens to start with a digit (or a user just typing "1" with no
        /// clarification pending) would silently lock in a guessed grade
        /// and then get answered using that digit as if it were the actual
        /// question.
        /// </param>
        public string? TryResolveGradeAnswer(string text, bool allowNumberedOptions = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();

            if (allowNumberedOptions)
            {
                if (Regex.IsMatch(trimmed, @"^\s*1\b")) return GradeTiers[0].Label;
                if (Regex.IsMatch(trimmed, @"^\s*2\b")) return GradeTiers[1].Label;
            }

            var lower = trimmed.ToLowerInvariant();
            foreach (var tier in GradeTiers)
            {
                if (tier.Patterns.Any(p => Regex.IsMatch(lower, p, RegexOptions.IgnoreCase)))
                    return tier.Label;
            }

            // Plain "supervisor" (without "below"/"and above") on its own is
            // ambiguous on purpose -- do NOT guess a tier from it. Only the
            // more specific patterns above should resolve a grade.
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // SELF-RECOGNITION STRUCTURE (generic, topic-independent)
        //
        // Earlier version of this used one hand-authored ScenarioDefinition
        // per topic (Death, Illness, Childbirth...) with its own patterns
        // and section titles. That doesn't scale to 131 policies -- it'd
        // mean authoring a new entry every time the same self-vs-family
        // confusion shows up under a different policy topic.
        //
        // Generalized instead: the underlying question is never really
        // "which policy topic is this" -- it's always the same two generic
        // questions, independent of topic:
        //   1. Does the QUESTION refer to the employee themselves, or to a
        //      named family member? (DetectQuerySubjectScope)
        //   2. Does a given CHUNK's content refer to the employee
        //      themselves, or to a named family member? (ClassifyChunkSubjectScope)
        // A chunk is only dropped when both are confidently known AND they
        // disagree -- e.g. Bereavement Leave content (family-scoped) showing
        // up for "what if I die" (self-scoped). No per-topic authoring
        // needed; this applies uniformly across all 131 policies.
        // ─────────────────────────────────────────────────────────────────

        public enum SubjectScope { Self, FamilyMember }

        // Generic family-member vocabulary -- not tied to any one policy
        // topic. Add a term here once and it applies everywhere (death,
        // illness, marriage, education assistance, travel, etc.), rather
        // than once per scenario.
        private static readonly string[] FamilyMemberNouns = new[]
        {
            "father", "mother", "dad", "mom", "wife", "husband", "spouse",
            "son", "daughter", "child", "children", "kids", "parent", "parents",
            "brother", "sister", "sibling", "siblings", "in-law", "in law",
            "dependent", "dependents", "family member", "family members",
            "next of kin", "relative", "relatives", "guardian",
        };

        private static string FamilyNounPattern(string noun) => Regex.Escape(noun).Replace(@"\ ", @"\s+");

        /// <summary>
        /// Does this free text (a question, or a chunk's content) refer to
        /// the employee themselves, or to a named family member? Purely
        /// generic/lexical -- works the same regardless of WHAT event or
        /// policy topic is being discussed.
        /// </summary>
        /// <param name="text">Question text or chunk text/section title.</param>
        /// <param name="isQuery">
        /// True when classifying the user's own question (uses first-person
        /// phrasing like "if I..."); false when classifying policy chunk
        /// content (uses third-person phrasing like "the employee...").
        /// </param>
        public SubjectScope? DetectSubjectScope(string text, bool isQuery)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lower = text.ToLowerInvariant();

            // A named family member, possessively tied to the employee
            // ("my father", "the employee's spouse", "his wife") is a
            // strong, topic-independent signal this is about that family
            // member's event, not the employee's own -- checked first since
            // it's more specific than a bare first-person pronoun.
            bool familyMemberNamed = FamilyMemberNouns.Any(n =>
                Regex.IsMatch(lower, $@"\b(my|his|her|their|the\s+employee'?s?)\s+{FamilyNounPattern(n)}\b"));
            if (familyMemberNamed) return SubjectScope.FamilyMember;

            // Standalone words already common to bereavement/family-event
            // provisions regardless of exact phrasing.
            if (Regex.IsMatch(lower, @"\bbereavement\b|\blast\s+rites\b"))
                return SubjectScope.FamilyMember;

            if (isQuery)
            {
                // First-person phrasing referring to something happening TO
                // the speaker, without a family member named alongside it.
                if (Regex.IsMatch(lower, @"\bif\s+i\b|\bwhen\s+i\b|\bafter\s+i\b|\bmy\s+own\b|\bi\s+(die|resign|retire|pass\s+away|am\s+hospitalized|get\s+married)\b|\bmyself\b|\bmy\s+death\b"))
                    return SubjectScope.Self;
            }
            else
            {
                // Third-person phrasing describing the employee's own event
                // in policy language.
                if (Regex.IsMatch(lower, @"\bemployee'?s\s+own\b|\bdeath\s+in\s+service\b|\bthe\s+employee\s+(dies|resigns|retires)\b|\bdeceased\s+employee\b|\bemployee\s+himself\b|\bemployee\s+herself\b"))
                    return SubjectScope.Self;
            }

            return null; // no confident signal either way -- leave untouched
        }

        /// <summary>
        /// Classifies a retrieved chunk's subject scope from its section
        /// title and content (third-person policy language).
        /// </summary>
        public SubjectScope? ClassifyChunkSubjectScope(RelevantChunk chunk)
        {
            var combined = $"{chunk.SectionTitle} {chunk.Text}";
            return DetectSubjectScope(combined, isQuery: false);
        }

        /// <summary>
        /// Removes chunks whose content is confidently classified as the
        /// OTHER subject scope than the question's (self vs. family member)
        /// -- e.g. Bereavement Leave content surfacing on "what if I die".
        /// Generic across every policy topic; no per-scenario configuration.
        /// A chunk is only ever dropped when BOTH sides are confidently
        /// classified and they disagree -- anything ambiguous is left in,
        /// since over-filtering loses real answers while under-filtering
        /// just leaves one extra (ideally prompt-caught) chunk in context.
        /// </summary>
        public List<RelevantChunk> FilterScenarioMismatchedChunks(List<RelevantChunk> chunks, string question)
        {
            if (chunks == null || !chunks.Any()) return chunks ?? new();

            var queryScope = DetectSubjectScope(question, isQuery: true);
            if (queryScope == null) return chunks; // question doesn't clearly say whose event it is -- don't touch anything

            var kept = new List<RelevantChunk>();
            foreach (var c in chunks)
            {
                var chunkScope = ClassifyChunkSubjectScope(c);
                bool mismatched = chunkScope != null && chunkScope != queryScope;
                if (mismatched)
                {
                    _logger.LogInformation(
                        "🪦 Dropping chunk with section '{Title}' from {Source} — classified as {ChunkScope}, but question is about {QueryScope}",
                        c.SectionTitle, c.Source, chunkScope, queryScope);
                }
                else
                {
                    kept.Add(c);
                }
            }

            return kept;
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