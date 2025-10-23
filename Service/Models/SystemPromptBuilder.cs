using MEAI_GPT_API.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service.Models
{
    public class SystemPromptBuilder
    {
        private readonly PolicyAnalysisService _policyAnalysis;
        private readonly EntityExtractionService _entityExtraction;

        public SystemPromptBuilder(PolicyAnalysisService policyAnalysis, EntityExtractionService entityExtraction)
        {
            _policyAnalysis = policyAnalysis;
            _entityExtraction = entityExtraction;
        }
        public async Task<string> BuildMeaiSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            // Check if this is a section query first
            if (HasSectionReference(query))
            {
                return await BuildDynamicSectionSystemPrompt(plant, chunks, query);
            }
            else if (_entityExtraction.HasManyAbbreviations(query))
            {
                var variables = new Dictionary<string, object>
                {
                    ["plant"] = plant,
                    ["abbreviations"] = _entityExtraction.FormatAbbreviations(_entityExtraction.ExtractAbbreviationsFromQuery(query, chunks))
                };
                return BuildSystemPromptFromTemplate(plant, "abbreviation_heavy", variables);
            }
            else
            {
                return BuildContextAwareSystemPrompt(plant, chunks, query);
            }
        }
        public string BuildGeneralSystemPrompt()
        {
            return @"You are a helpful AI assistant.

                    INSTRUCTIONS:
                    1. Provide accurate, complete responses
                    2. Be conversational and natural
                    3. Structure responses clearly
                    4. If you don't know something, say so
                    5. Use only provided context when available

                    Be helpful, friendly, and informative.";
        }
        public async Task<string> BuildDynamicSectionSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var sectionQuery = await _policyAnalysis.DetectAndParseSection(query);
            if (sectionQuery == null) return BuildContextAwareSystemPrompt(plant, chunks, query);

            var sectionRef = $"Section {sectionQuery.SectionNumber}";
            var docType = string.IsNullOrEmpty(sectionQuery.DocumentType)
                ? "Policy"
                : sectionQuery.DocumentType;

            // Detect what sections are actually available in the chunks
            var availableSections = DetectAvailableSections(chunks, sectionQuery.SectionNumber);

            return $@"You are MEAI Policy Assistant for {plant}.

**PRIMARY OBJECTIVE**: Answer questions using ONLY the provided policy context, regardless of topic domain.


🎯 USER IS ASKING ABOUT: {sectionRef} of {docType}

CRITICAL INSTRUCTIONS FOR DYNAMIC SECTION QUERIES:
1. **POLICY-SPECIFIC APPROACH**: Different policies have different section structures
   - ISMS policies may have different {sectionRef} content than HR policies
   - Safety policies may structure sections differently than Quality policies
   - Always specify which policy type you're referencing

2. **AVAILABLE CONTENT ANALYSIS**:
{BuildAvailableContentSummary(availableSections, sectionRef)}

3. **COMPREHENSIVE COVERAGE**: 
   - Look for ""{sectionRef}"" in ALL provided policy contexts
   - Include content from {docType} policies specifically
   - Cover all subsections (e.g., {sectionQuery.SectionNumber}.1, {sectionQuery.SectionNumber}.2, etc.)

4. **MULTI-POLICY HANDLING**:
   - If {sectionRef} exists in multiple policy types, clearly separate them
   - Format: ""## {sectionRef} in ISMS Policy"", ""## {sectionRef} in HR Policy"", etc.
   - Highlight differences between policy types

5. **STRUCTURE YOUR RESPONSE**:
   - Start with policy type identification
   - Main section overview with exact section title
   - All relevant subsections with full content
   - Procedures and requirements specific to that policy type

6. **CONTEXT VALIDATION**:
   - Always mention which document/policy type contains the information
   - If section doesn't exist in a particular policy, clearly state it
   - Cite sources with policy type: ""[{docType} Policy - {sectionRef}: filename]""

7. **COMPLETENESS**: Provide COMPLETE and DETAILED information for the specific policy type
   - Don't mix content from different policy types
   - If multiple policies have the same section, clearly separate them

8. **CRITICAL RULES**:
    8.1. **READ CAREFULLY**: The context contains actual policy content - use it completely
    8.2. **BE COMPREHENSIVE**: If policy content exists, provide COMPLETE details
    8.3. **STAY FACTUAL**: Base answers ONLY on provided context
    8.4. **QUOTE DIRECTLY**: Use exact wording from policies when possible
    8.5. **CITE SOURCES**: Always mention document names

**RESPONSE APPROACH**:
- If context contains relevant information → Provide detailed, complete answer
- If context has partial information → Use what's available and note limitations  
- If no relevant context → State clearly that information is not available

**FORMATTING**:
- Use clear headings and structure
- Quote exact policy text when applicable
- Always cite: [Source: Document Name]
- Be thorough - don't summarize if full details are available

**REMEMBER**: You handle ALL policy domains - HR, Safety, Quality, ISMS, Environment, etc.
Your job is to extract and present policy information accurately, regardless of the topic.

Check the provided context thoroughly before responding.

Remember: Section numbers may represent completely different topics across policy types!
Current context contains: {string.Join(", ", chunks.Select(c => DeterminePolicyTypeFromSource(c.Source)).Distinct())} policies.";
        }
        public string BuildContextAwareSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine($"You are MEAI Policy Assistant for {plant}.");
            prompt.AppendLine();
            prompt.AppendLine("🎯 CRITICAL INSTRUCTIONS:");
            prompt.AppendLine("1. **READ ALL PROVIDED CONTEXT CAREFULLY** - The context contains actual policy content");
            prompt.AppendLine("2. **USE EXACT CONTENT**: Base answers ONLY on the provided policy context");
            prompt.AppendLine("3. **COMPREHENSIVE ANSWERS**: When content exists, provide complete details");
            prompt.AppendLine("4. **ACCURATE CITATIONS**: Always cite source documents");
            prompt.AppendLine();

            // Add query-specific guidance
            if (HasSectionReference(query))
            {
                var sectionRef = _policyAnalysis.ExtractSectionReference(query);
                prompt.AppendLine($"🔍 USER IS ASKING ABOUT: {sectionRef}");
                prompt.AppendLine("- Look carefully for this specific section in the context");
                prompt.AppendLine("- Include all subsections and details if found");
                prompt.AppendLine("- If section exists in context, provide complete information");
                prompt.AppendLine();
            }

            // Add document-specific guidance based on found content
            var policyTypes = chunks.Select(c => DetermineDocumentType(c.Source)).Distinct().ToList();
            if (policyTypes.Any())
            {
                prompt.AppendLine("📋 AVAILABLE POLICY INFORMATION:");
                foreach (var policyType in policyTypes)
                {
                    prompt.AppendLine($"• {policyType}");
                }
                prompt.AppendLine();
            }

            // Add common abbreviations found in context
            var abbreviations = _entityExtraction.ExtractAbbreviationsFromQuery(query, chunks);
            if (abbreviations.Any())
            {
                prompt.AppendLine("📖 RELEVANT ABBREVIATIONS:");
                foreach (var abbrev in abbreviations)
                {
                    prompt.AppendLine($"• {abbrev.Key} = {abbrev.Value}");
                }
                prompt.AppendLine();
            }

            prompt.AppendLine("ANSWER FORMAT:");
            prompt.AppendLine("- Use clear headings and bullet points");
            prompt.AppendLine("- Cite sources as [DocumentName: filename]");
            prompt.AppendLine("- Be specific about section numbers");
            prompt.AppendLine("- Provide complete information when available");
            prompt.AppendLine();
            prompt.AppendLine("Remember: Check the context thoroughly before saying any section or information doesn't exist.");

            return prompt.ToString();
        }
        public string BuildSystemPromptFromTemplate(string plant, string templateType, Dictionary<string, object> variables)
        {
            var templates = new Dictionary<string, string>
            {
                ["section_query"] = @"You are MEAI Policy Assistant for {plant}.

🎯 USER IS ASKING ABOUT: {section_reference}

INSTRUCTIONS:
1. Look for ""{section_reference}"" in the provided context
2. If found, provide ALL details from that section including subsections
3. Include exact content, procedures, and requirements
4. Cite the source document name
5. If not found in context, clearly state it's not available

{abbreviations}

Be thorough and accurate in your response.",

                ["general_policy"] = @"You are MEAI Policy Assistant for {plant}.

🎯 DETECTED POLICIES: {policy_types}

INSTRUCTIONS:
1. Use ONLY the provided policy context to answer
2. Provide comprehensive information when available
3. Cite source documents clearly
4. Structure answers with clear headings

{abbreviations}

Check context thoroughly before saying information doesn't exist.",

                ["abbreviation_heavy"] = @"You are MEAI Policy Assistant for {plant}.

🎯 ABBREVIATION-HEAVY QUERY DETECTED

KEY DEFINITIONS:
{abbreviations}

INSTRUCTIONS:
1. Use the above definitions when interpreting the query
2. Look for both abbreviated and full forms in context
3. Provide comprehensive policy information
4. Always cite source documents

Be thorough in checking for all variations of terms."
            };

            var template = templates.GetValueOrDefault(templateType, templates["general_policy"]);

            // Replace variables
            foreach (var variable in variables)
            {
                template = template.Replace($"{{{variable.Key}}}", variable.Value?.ToString() ?? "");
            }

            return template;
        }
        public bool HasSectionReference(string text)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(text,
                @"\b(section|clause|part|paragraph)\s+\d+|^\d+\.\d+|\b\d+\s+(introduction|scope|definitions|context|leadership|physical|planning|operation|performance|improvement)");
        }
        private List<string> DetectAvailableSections(List<RelevantChunk> chunks, string targetSection)
        {
            var availableSections = new List<string>();

            foreach (var chunk in chunks)
            {
                // Look for section patterns in the text
                var sectionMatches = System.Text.RegularExpressions.Regex.Matches(
                    chunk.Text,
                    @"(?i)(section\s+\d+(?:\.\d+)*|\d+\.\d+(?:\.\d+)*)",
                    RegexOptions.IgnoreCase);

                foreach (Match match in sectionMatches)
                {
                    var section = match.Groups[1].Value;
                    if (!availableSections.Contains(section, StringComparer.OrdinalIgnoreCase))
                    {
                        availableSections.Add(section);
                    }
                }
            }

            return availableSections.OrderBy(s => s).ToList();
        }
        private string BuildAvailableContentSummary(List<string> availableSections, string targetSection)
        {
            if (!availableSections.Any())
            {
                return $"   - ⚠️ No clear section structure detected in provided context";
            }

            var hasTargetSection = availableSections.Any(s =>
                s.Contains(targetSection.Replace("Section ", ""), StringComparison.OrdinalIgnoreCase));

            var summary = new StringBuilder();
            summary.AppendLine($"   - Available sections in context: {string.Join(", ", availableSections.Take(10))}");

            if (hasTargetSection)
            {
                summary.AppendLine($"   - ✅ {targetSection} content appears to be available");
            }
            else
            {
                summary.AppendLine($"   - ⚠️ {targetSection} may not be explicitly available in current context");
            }

            return summary.ToString();
        }
        private string DeterminePolicyTypeFromSource(string source)
        {
            var lowerSource = source.ToLowerInvariant();
            if (lowerSource.Contains("isms")) return "ISMS";
            if (lowerSource.Contains("hr")) return "HR";
            if (lowerSource.Contains("safety")) return "Safety";
            if (lowerSource.Contains("quality")) return "Quality";
            if (lowerSource.Contains("environment")) return "Environment";
            return "General";
        }
        private string DetermineDocumentType(string sourceFile)
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

    }
}
