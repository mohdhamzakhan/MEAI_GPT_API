using MEAI_GPT_API.Models;

public interface IDocumentProcessor
{
    Task<string> ExtractTextAsync(string filePath);

    private string BuildDynamicSystemPrompt(string plant, List<RelevantChunk> chunks)
    {
        var documentTypes = chunks.Select(c => DetermineDocumentType(c.Source)).Distinct().ToList();

        var basePrompt = $@"You are MEAI Policy Assistant for {plant}.

🎯 INSTRUCTIONS:
1. **READ ALL PROVIDED CONTEXT CAREFULLY** - The context contains actual policy content
2. **USE EXACT CONTENT**: Base answers ONLY on the provided policy context
3. **SECTION IDENTIFICATION**: Look for numbered sections, subsections, and policy references
4. **IF CONTENT EXISTS IN CONTEXT**: Provide complete details from that section
5. **IF CONTENT NOT IN CONTEXT**: Only then say it's not available

📋 DETECTED POLICY TYPES IN CONTEXT:
{string.Join(", ", documentTypes)}

KEY DEFINITIONS (auto-detected from context):";

        // Add dynamic definitions based on found content
        var definitions = ExtractDefinitionsFromChunks(chunks);
        foreach (var def in definitions)
        {
            basePrompt += $"\n• {def.Key} = {def.Value}";
        }

        basePrompt += @"

ANSWER FORMAT:
- Use clear headings and bullet points
- Cite sources as [DocumentName]
- Provide complete information when available
- Be specific about section numbers and policy references

Check the context thoroughly before saying any section doesn't exist.";

        return basePrompt;
    }
    private string DetermineDocumentType(string sourceFile)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourceFile).ToLower();

        var policyMappings = new Dictionary<string, string[]>
        {
            ["ISMS"] = new[] { "isms", "information security", "security management" },
            ["HR Policy"] = new[] { "hr", "human resource", "employee", "personnel" },
            ["Safety Policy"] = new[] { "safety", "occupational", "workplace safety", "ehs" },
            ["Quality Policy"] = new[] { "quality", "qms", "iso", "quality management" },
            ["Finance Policy"] = new[] { "finance", "financial", "accounting", "budget" },
            ["IT Policy"] = new[] { "it", "information technology", "computer", "software" },
            ["Procurement Policy"] = new[] { "procurement", "purchase", "vendor", "supplier" },
            ["Compliance Policy"] = new[] { "compliance", "regulatory", "legal", "audit" },
            ["Training Policy"] = new[] { "training", "development", "learning", "education" },
            ["Leave Policy"] = new[] { "leave", "absence", "vacation", "holiday" },
            ["Code of Conduct"] = new[] { "conduct", "ethics", "behavior", "code" },
            ["Environment Policy"] = new[] { "environment", "environmental", "green", "sustainability" }
        };

        foreach (var mapping in policyMappings)
        {
            if (mapping.Value.Any(keyword => fileName.Contains(keyword)))
            {
                return mapping.Key;
            }
        }

        return "Policy Document";
    }

    private Dictionary<string, string> ExtractDefinitionsFromChunks(List<RelevantChunk> chunks)
    {
        var definitions = new Dictionary<string, string>();

        foreach (var chunk in chunks.Take(5)) // Check first 5 chunks
        {
            var text = chunk.Text.ToLowerInvariant();

            // Common HR abbreviations
            var commonDefs = new Dictionary<string, string>
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
                ["qms"] = "Quality Management System",
                ["sop"] = "Standard Operating Procedure"
            };

            foreach (var def in commonDefs)
            {
                if (text.Contains(def.Key) && !definitions.ContainsKey(def.Key.ToUpper()))
                {
                    definitions[def.Key.ToUpper()] = def.Value;
                }
            }
        }

        return definitions;
    }


}