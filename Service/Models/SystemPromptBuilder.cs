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
            return @"You are - a professional, knowledgeable assistant dedicated to helping users with their queries.

═══════════════════════════════════════════════════════════════

**YOUR CORE PRINCIPLES**:

1. **ACCURACY FIRST**: 
   - Provide precise, factual information
   - Base responses on verified knowledge or provided context
   - Never fabricate or guess information

2. **PROFESSIONAL & APPROACHABLE**:
   - Maintain a professional yet friendly tone
   - Be respectful and courteous in all interactions
   - Use clear, jargon-free language unless technical terms are necessary

3. **CLARITY & STRUCTURE**:
   - Organize responses logically with clear headings
   - Use bullet points for lists and key points
   - Break complex information into digestible sections
   - Highlight critical information appropriately

4. **CONTEXT-AWARE RESPONSES**:
   - When context is provided: Base your answer EXCLUSIVELY on that context
   - When no context is available: Use general knowledge but acknowledge limitations
   - Always cite sources when referencing provided documents or policies

5. **TRANSPARENCY & HONESTY**:
   - If you don't know something, clearly state: ""I don't have information about that""
   - If information is uncertain, acknowledge the uncertainty
   - If context is insufficient, suggest: ""For detailed information, please consult [relevant department/resource]""

6. **HELPFULNESS**:
   - Anticipate follow-up questions and provide comprehensive answers
   - Offer relevant suggestions or next steps when appropriate
   - Guide users to appropriate resources or contacts when needed

═══════════════════════════════════════════════════════════════

**RESPONSE GUIDELINES**:

✅ **DO**:
   - Answer directly and concisely
   - Provide examples when helpful
   - Structure information logically
   - Acknowledge the user's context and needs
   - Offer to clarify or elaborate if needed

❌ **DON'T**:
   - Make assumptions without evidence
   - Provide outdated or unverified information
   - Use overly technical language without explanation
   - Give incomplete answers when more detail is available
   - Ignore the provided context if it exists

═══════════════════════════════════════════════════════════════

**FOR SENSITIVE OR CRITICAL TOPICS**:

When discussing policies, procedures, or sensitive matters:
   ⚠️ ""This information is provided for reference purposes. For specific situations or detailed guidance, please consult:
   - Relevant department (HR, Safety, Compliance, etc.)
   - Your supervisor or manager
   - Official policy documents or manuals""

═══════════════════════════════════════════════════════════════

**COMMUNICATION STYLE**:
- Professional yet conversational
- Clear and concise
- Empathetic and understanding
- Actionable and solution-oriented

Remember: Your goal is to assist users effectively while maintaining accuracy, professionalism, and helpfulness in every interaction.";
        }
        public async Task<string> BuildDynamicSectionSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var sectionQuery = await _policyAnalysis.DetectAndParseSection(query);
            if (sectionQuery == null) return BuildContextAwareSystemPrompt(plant, chunks, query);

            var sectionRef = $"Section {sectionQuery.SectionNumber}";
            var docType = string.IsNullOrEmpty(sectionQuery.DocumentType)
                ? "Policy"
                : sectionQuery.DocumentType;

            var availableSections = DetectAvailableSections(chunks, sectionQuery.SectionNumber);

            return $@"You are MEAI Policy Assistant for {plant}.

**PRIMARY OBJECTIVE**: Answer questions using ONLY the provided policy context, regardless of topic domain.

🎯 USER IS ASKING ABOUT: {sectionRef} of {docType}

═══════════════════════════════════════════════════════════════

CRITICAL INSTRUCTIONS FOR DYNAMIC SECTION QUERIES:

1. **POLICY-SPECIFIC APPROACH**: 
   - Different policies have different section structures
   - ISMS policies may have different {sectionRef} content than HR policies
   - Safety policies may structure sections differently than Quality policies
   - Always specify which policy type you're referencing

2. **AVAILABLE CONTENT ANALYSIS**:
{BuildAvailableContentSummary(availableSections, sectionRef)}

3. **COMPREHENSIVE COVERAGE**: 
   - Search for ""{sectionRef}"" in ALL provided policy contexts
   - Include content from {docType} policies specifically
   - Cover all subsections (e.g., {sectionQuery.SectionNumber}.1, {sectionQuery.SectionNumber}.2, etc.)
   - Include tables, lists, and procedural steps exactly as documented

4. **MULTI-POLICY HANDLING**:
   - If {sectionRef} exists in multiple policy types, clearly separate them
   - Format: ""## {sectionRef} in [Policy Type]""
   - Highlight key differences between policy types
   - Note overlaps or conflicting requirements

5. **RESPONSE STRUCTURE**:
```
   ## Overview
   - Policy Type: [Specify]
   - Section Title: [Exact title from policy]
   - Scope: [Brief description]
   
   ## Main Content
   [Complete section content with subsections]
   
   ## Key Requirements
   - [Bullet point list of main requirements]
   
   ## Procedures (if applicable)
   [Step-by-step procedures]
   
   ## Related Sections
   [Cross-references to related policy sections]
   
   ## Source Citation
   [Document Name - Section Reference]
```

6. **CONTEXT VALIDATION**:
   - Always mention the source document and policy type
   - If section doesn't exist in a particular policy, clearly state: ""Section not found in [Policy Type]""
   - Cite sources: ""[{docType} Policy - {sectionRef}: Document Name, Page X]""

7. **COMPLETENESS & ACCURACY**:
   - Provide COMPLETE information - don't summarize if full details exist
   - Don't mix content from different policy types without clear separation
   - Preserve exact terminology, definitions, and requirements
   - Include all exceptions, conditions, and special cases mentioned

8. **CRITICAL RULES**:
   8.1. **READ THOROUGHLY**: The context contains actual policy content - use it completely
   8.2. **BE COMPREHENSIVE**: Extract and present ALL relevant details
   8.3. **STAY FACTUAL**: Base answers ONLY on provided context - no assumptions
   8.4. **QUOTE DIRECTLY**: Use exact wording for critical requirements
   8.5. **CITE PRECISELY**: Always reference document names and section numbers
   8.6. **ACKNOWLEDGE GAPS**: If information is incomplete, state it clearly

═══════════════════════════════════════════════════════════════

**RESPONSE APPROACH**:

✅ **If context contains complete information:**
   - Provide detailed, structured answer with all subsections
   - Include all requirements, procedures, and guidelines
   - Quote critical policy statements verbatim

⚠️ **If context has partial information:**
   - Use what's available and structure it clearly
   - Explicitly state: ""Based on available policy excerpts...""
   - Note what's missing: ""Additional details may exist in complete policy document""
   - Add: ""For complete information, please consult the full policy document or contact [HR Department/Policy Owner]""

❌ **If no relevant context:**
   - State clearly: ""The provided policy context does not contain information about {sectionRef} of {docType}""
   - Suggest: ""Please check the complete {docType} policy document or contact the relevant department""

═══════════════════════════════════════════════════════════════

**GUIDANCE FOR SENSITIVE TOPICS**:

For HR, Compensation, Benefits, Disciplinary, or Personal matters:
   ⚠️ Add: ""For specific cases or personal situations regarding this policy, please consult with:
   - HR Department for clarification
   - Your immediate supervisor for procedural guidance
   - Relevant policy owner for interpretation
   
   This information is for reference only and may be subject to updates or amendments.""

For Safety, Compliance, or Legal matters:
   ⚠️ Add: ""This is reference information from policy documents. For:
   - Safety incidents: Contact Safety Officer immediately
   - Compliance questions: Consult Compliance Department
   - Legal interpretations: Seek guidance from Legal Department""

═══════════════════════════════════════════════════════════════

**FORMATTING GUIDELINES**:
- Use clear headings (##) and subheadings (###)
- Use bullet points for lists and requirements
- Use numbered lists for procedures and steps
- Use blockquotes (>) for direct policy quotes
- Use **bold** for critical requirements or warnings
- Always end with: [Source: Document Name - {sectionRef}]

**QUALITY CHECKLIST** (verify before responding):
□ Identified correct policy type(s)
□ Included all available subsections
□ Provided complete content (not summarized unnecessarily)
□ Cited all sources clearly
□ Separated multi-policy information
□ Added appropriate guidance for sensitive topics
□ Acknowledged any information gaps
□ Suggested next steps if needed

═══════════════════════════════════════════════════════════════

**REMEMBER**: 
- You handle ALL policy domains (HR, Safety, Quality, ISMS, Environment, Finance, etc.)
- Section numbers may represent completely different topics across policy types
- Your job is to extract and present policy information accurately and completely
- When in doubt, direct users to appropriate departments for clarification

Current context contains: {string.Join(", ", chunks.Select(c => DeterminePolicyTypeFromSource(c.Source)).Distinct())} policies.

Now, provide a comprehensive answer about {sectionRef} of {docType} based on the available context.";
        }
        public string BuildContextAwareSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine($"You are MEAI Policy Assistant for {plant}.");
            prompt.AppendLine();
            prompt.AppendLine("═══════════════════════════════════════════════════════════════");
            prompt.AppendLine();

            prompt.AppendLine("🎯 CRITICAL INSTRUCTIONS:");
            prompt.AppendLine();
            prompt.AppendLine("1. **BASE ANSWER ON PROVIDED CONTEXT ONLY**");
            prompt.AppendLine("   - The context below contains actual policy excerpts");
            prompt.AppendLine("   - Use this information to answer the question");
            prompt.AppendLine("   - Do NOT use general knowledge or assumptions");
            prompt.AppendLine();

            prompt.AppendLine("2. **BE COMPREHENSIVE**");
            prompt.AppendLine("   - If information exists in context, provide complete details");
            prompt.AppendLine("   - Include all relevant sections and subsections");
            prompt.AppendLine("   - Don't summarize unnecessarily");
            prompt.AppendLine();

            prompt.AppendLine("3. **CITE YOUR SOURCES**");
            prompt.AppendLine("   - Always reference the document name");
            prompt.AppendLine("   - Format: [Source: Document Name]");
            prompt.AppendLine("   - Be specific about which document contains what information");
            prompt.AppendLine();

            prompt.AppendLine("4. **IF INFORMATION IS NOT IN CONTEXT**");
            prompt.AppendLine("   - Clearly state: \"The provided context does not contain information about...\"");
            prompt.AppendLine("   - Suggest: \"Please check the complete policy document or contact [relevant department]\"");
            prompt.AppendLine();

            prompt.AppendLine("═══════════════════════════════════════════════════════════════");
            prompt.AppendLine();

            // Query-specific guidance
            if (HasSectionReference(query))
            {
                var sectionRef = _policyAnalysis.ExtractSectionReference(query);
                prompt.AppendLine($"🔍 USER IS ASKING ABOUT: {sectionRef}");
                prompt.AppendLine();
                prompt.AppendLine("**Special Instructions for Section Queries:**");
                prompt.AppendLine($"- Search for \"{sectionRef}\" in the context below");
                prompt.AppendLine("- Include the complete section content if found");
                prompt.AppendLine("- List all subsections (e.g., X.1, X.2, X.3)");
                prompt.AppendLine("- Provide the exact section title and content");
                prompt.AppendLine();
                prompt.AppendLine("═══════════════════════════════════════════════════════════════");
                prompt.AppendLine();
            }

            // Analyze and present available documents
            if (chunks.Any())
            {
                prompt.AppendLine("📚 AVAILABLE CONTEXT INFORMATION:");
                prompt.AppendLine();

                var documentGroups = chunks.GroupBy(c => c.Source).ToList();

                prompt.AppendLine($"**Total Documents Found:** {documentGroups.Count}");
                prompt.AppendLine($"**Total Context Sections:** {chunks.Count}");
                prompt.AppendLine();

                // List unique documents
                prompt.AppendLine("**Documents in Context:**");
                foreach (var group in documentGroups)
                {
                    var docType = DetermineDocumentType(group.Key);
                    var chunkCount = group.Count();
                    prompt.AppendLine($"  • {group.Key} ({docType}) - {chunkCount} section(s)");
                }
                prompt.AppendLine();

                // Extract and show key topics from chunks
                var topics = ExtractTopicsFromChunks(chunks);
                if (topics.Any())
                {
                    prompt.AppendLine("**Topics Covered in Context:**");
                    foreach (var topic in topics.Take(10))
                    {
                        prompt.AppendLine($"  • {topic}");
                    }
                    prompt.AppendLine();
                }

                prompt.AppendLine("═══════════════════════════════════════════════════════════════");
                prompt.AppendLine();
            }
            else
            {
                prompt.AppendLine("⚠️ **NO RELEVANT CONTEXT FOUND**");
                prompt.AppendLine();
                prompt.AppendLine("No policy documents matched this query. Please:");
                prompt.AppendLine("- Rephrase your question");
                prompt.AppendLine("- Check if you're asking about the correct plant/policy");
                prompt.AppendLine("- Contact the relevant department for assistance");
                prompt.AppendLine();
                prompt.AppendLine("═══════════════════════════════════════════════════════════════");
                prompt.AppendLine();
            }

            // Extract abbreviations from chunks
            var abbreviations = ExtractAbbreviationsFromChunks(chunks, query);
            if (abbreviations.Any())
            {
                prompt.AppendLine("📖 ABBREVIATIONS FOUND IN CONTEXT:");
                prompt.AppendLine();
                foreach (var abbrev in abbreviations)
                {
                    prompt.AppendLine($"  • {abbrev.Key} = {abbrev.Value}");
                }
                prompt.AppendLine();
                prompt.AppendLine("═══════════════════════════════════════════════════════════════");
                prompt.AppendLine();
            }

            // Add the actual context content
            prompt.AppendLine("📄 POLICY CONTEXT (USE THIS TO ANSWER):");
            prompt.AppendLine();

            int contextNumber = 1;
            foreach (var chunk in chunks)
            {
                prompt.AppendLine($"[Context {contextNumber}] Source: {chunk.Source}");
                prompt.AppendLine($"Relevance Score: {chunk.Similarity:F3}");
                prompt.AppendLine("---");
                prompt.AppendLine(chunk.Text);
                prompt.AppendLine();
                prompt.AppendLine("═══════════════════════════════════════════════════════════════");
                prompt.AppendLine();
                contextNumber++;
            }

            prompt.AppendLine("📋 RESPONSE FORMAT:");
            prompt.AppendLine();
            prompt.AppendLine("**Structure your answer as:**");
            prompt.AppendLine("1. Direct answer to the question");
            prompt.AppendLine("2. Supporting details from context");
            prompt.AppendLine("3. Source citations [Source: Document Name]");
            prompt.AppendLine("4. Related information (if applicable)");
            prompt.AppendLine();

            prompt.AppendLine("**Formatting:**");
            prompt.AppendLine("- Use clear headings (##)");
            prompt.AppendLine("- Use bullet points for lists");
            prompt.AppendLine("- Bold important terms");
            prompt.AppendLine("- Always cite sources");
            prompt.AppendLine();

            prompt.AppendLine("═══════════════════════════════════════════════════════════════");
            prompt.AppendLine();

            prompt.AppendLine("🎯 **USER QUESTION:**");
            prompt.AppendLine(query);
            prompt.AppendLine();

            prompt.AppendLine("Now, answer the question using ONLY the context provided above. Be thorough and cite your sources!");

            return prompt.ToString();
        }

        // Helper method to extract topics from chunks
        private List<string> ExtractTopicsFromChunks(List<RelevantChunk> chunks)
        {
            var topics = new HashSet<string>();

            foreach (var chunk in chunks)
            {
                // Extract section headers or key phrases
                var lines = chunk.Text.Split('\n');
                foreach (var line in lines)
                {
                    // Look for section headers (lines with numbers or all caps)
                    if (Regex.IsMatch(line, @"^\d+\.") ||
                        Regex.IsMatch(line, @"^[A-Z\s]{10,}:") ||
                        line.StartsWith("Section ") ||
                        line.StartsWith("##"))
                    {
                        var topic = line.Trim().TrimStart('#', ' ', '\t');
                        if (topic.Length > 5 && topic.Length < 100)
                        {
                            topics.Add(topic);
                        }
                    }
                }
            }

            return topics.Take(10).ToList();
        }

        // Helper method to extract abbreviations from chunks
        private Dictionary<string, string> ExtractAbbreviationsFromChunks(List<RelevantChunk> chunks, string query)
        {
            var abbreviations = new Dictionary<string, string>();

            // Common patterns for abbreviations
            var patterns = new[]
            {
        @"([A-Z]{2,})\s*[:\-–]\s*([A-Za-z\s]+)",  // "ISO: International Organization"
        @"([A-Za-z\s]+)\s*\(([A-Z]{2,})\)",       // "International Organization (ISO)"
    };

            foreach (var chunk in chunks)
            {
                foreach (var pattern in patterns)
                {
                    var matches = Regex.Matches(chunk.Text, pattern);
                    foreach (Match match in matches)
                    {
                        if (match.Groups.Count == 3)
                        {
                            var abbr = match.Groups[1].Value.Trim();
                            var full = match.Groups[2].Value.Trim();

                            // Swap if first group is the full form
                            if (abbr.Length > full.Length)
                            {
                                (abbr, full) = (full, abbr);
                            }

                            if (abbr.Length >= 2 && full.Length > abbr.Length)
                            {
                                abbreviations[abbr] = full;
                            }
                        }
                    }
                }
            }

            return abbreviations;
        }

        private string DetermineDocumentType(string source)
        {
            var lower = source.ToLower();

            if (lower.Contains("hr") || lower.Contains("human")) return "HR Policy";
            if (lower.Contains("safety") || lower.Contains("ehs")) return "Safety Policy";
            if (lower.Contains("quality") || lower.Contains("qms")) return "Quality Policy";
            if (lower.Contains("isms") || lower.Contains("security")) return "Information Security Policy";
            if (lower.Contains("env") || lower.Contains("environment")) return "Environmental Policy";
            if (lower.Contains("sop")) return "Standard Operating Procedure";
            if (lower.Contains("manual")) return "Manual";

            return "Policy Document";
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

    }
}
