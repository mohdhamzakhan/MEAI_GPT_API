using System.Text.RegularExpressions;

public class CodingDetectionService
{
    private readonly Dictionary<string, LanguageIndicators> _languageIndicators;

    public CodingDetectionService()
    {
        _languageIndicators = new Dictionary<string, LanguageIndicators>
        {
            ["csharp"] = new LanguageIndicators
            {
                Keywords = new[] { "class", "interface", "namespace", "using", "public", "private", "async", "await", "var", "string", "int", "list" },
                Patterns = new[] {
                    @"\bpublic\s+class\s+\w+",
                    @"\busing\s+\w+(\.\w+)*\s*;",
                    @"\bnamespace\s+\w+",
                    @"\bvar\s+\w+\s*=",
                    @"\bpublic\s+\w+\s+\w+\s*\(",
                    @"\b(public|private|protected)\s+(static\s+)?(int|string|void|bool)\s+\w+\s*\("
                },
                FileExtensions = new[] { ".cs" },
                FrameworkKeywords = new[] { "linq", "entity framework", "asp.net", "mvc", "web api", "blazor" },
                StrongIndicators = new[] { "public class", "using system", "namespace", "var ", "string " }
            },
            ["python"] = new LanguageIndicators
            {
                Keywords = new[] { "def", "class", "import", "from", "if", "elif", "else", "for", "while", "try", "except" },
                Patterns = new[] {
                    @"\bdef\s+\w+\s*\(",
                    @"\bclass\s+\w+\s*:",
                    @"\bimport\s+\w+",
                    @"\bfrom\s+\w+\s+import",
                    @"if\s+__name__\s*==\s*['""]__main__['""]"
                },
                FileExtensions = new[] { ".py" },
                FrameworkKeywords = new[] { "django", "flask", "pandas", "numpy", "tensorflow", "pytorch" },
                StrongIndicators = new[] { "def ", "import ", "from ", ":" }
            },
            ["javascript"] = new LanguageIndicators
            {
                Keywords = new[] { "const", "let", "var", "class", "import", "export", "async", "await" },
                Patterns = new[] {
                    @"\bfunction\s+\w+\s*\(",
                    @"\bconst\s+\w+\s*=",
                    @"\blet\s+\w+\s*=",
                    @"\bimport\s+.+\s+from\s+['""].+['""]",
                    @"\bexport\s+(?:default\s+)?\w+",
                    @"=>\s*\{", // Arrow functions
                    @"console\.log\s*\("
                },
                FileExtensions = new[] { ".js", ".ts", ".jsx", ".tsx" },
                FrameworkKeywords = new[] { "react", "vue", "angular", "node", "express", "next" },
                StrongIndicators = new[] { "const ", "let ", "=>", "console.log" }
            },
            ["java"] = new LanguageIndicators
            {
                Keywords = new[] { "public", "private", "class", "interface", "extends", "implements", "import", "package" },
                Patterns = new[] {
                    @"\bpublic\s+class\s+\w+",
                    @"\bimport\s+[\w.]+\s*;",
                    @"\bpackage\s+[\w.]+\s*;",
                    @"\bpublic\s+static\s+void\s+main"
                },
                FileExtensions = new[] { ".java" },
                FrameworkKeywords = new[] { "spring", "hibernate", "junit", "maven", "gradle" },
                StrongIndicators = new[] { "public class", "import ", "package " }
            }
        };
    }

    public CodingDetectionResult DetectCodingQuery(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new CodingDetectionResult { IsCodingRelated = false };

        var result = new CodingDetectionResult();
        var lowerInput = input.ToLowerInvariant();

        // Check for explicit language mentions first
        var languageMentions = CheckLanguageMentions(lowerInput);

        // Check for general coding keywords
        var codingKeywords = new[]
        {
            "code", "program", "algorithm", "function", "method", "class", "debug", "error", "bug",
            "compile", "syntax", "implement", "develop", "script", "api", "framework", "library",
            "programming", "software", "application", "coding", "development"
        };
        var hasGeneralCodingKeywords = codingKeywords.Any(keyword => lowerInput.Contains(keyword));

        // Check for actual code patterns
        var hasCodePatterns = HasActualCodePatterns(input);

        // Language-specific detection with improved scoring
        var languageScores = new Dictionary<string, int>();
        foreach (var lang in _languageIndicators)
        {
            var score = CalculateLanguageScore(input, lang.Value, languageMentions.Contains(lang.Key));
            if (score > 0)
                languageScores[lang.Key] = score;
        }

        // Determine if it's coding-related
        result.IsCodingRelated = hasGeneralCodingKeywords || hasCodePatterns || languageScores.Any();

        if (result.IsCodingRelated)
        {
            // If specific languages are mentioned, prioritize them
            if (languageMentions.Any())
            {
                var mentionedLanguageScores = languageScores.Where(kvp => languageMentions.Contains(kvp.Key));
                result.DetectedLanguage = mentionedLanguageScores.Any()
                    ? mentionedLanguageScores.OrderByDescending(kvp => kvp.Value).First().Key
                    : languageMentions.First();
            }
            else
            {
                result.DetectedLanguage = languageScores.Any()
                    ? languageScores.OrderByDescending(kvp => kvp.Value).First().Key
                    : "general";
            }

            result.Confidence = CalculateConfidence(hasGeneralCodingKeywords, hasCodePatterns, languageScores, languageMentions.Any());
            result.LanguageScores = languageScores;
            result.MentionedLanguages = languageMentions.ToList();
        }

        return result;
    }

    private bool HasActualCodePatterns(string input)
    {
        var codePatterns = new[]
        {
        """\bclass\s+\w+\s*\{""",
        """\bdef\s+\w+\s*\(.*\)\s*:""",
        """public\s+class\s+\w+\s*\{""",
        """def\s+\w+\s*$$[^)]*$$\s*:""",
        """function\s+\w+\s*$$[^)]*$$\s*\{""",
        """#include\s*<\w+>""",
        """<!DOCTYPE\s+html>""",
        """SELECT\s+\*\s+FROM\s+\w+""",
        """console\.log\s*$$""",
        """System\.out\.println\s*$$""",
        """print\s*$$"""
    };

        return codePatterns.Any(pattern =>
            Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase));
    }


    private List<string> CheckLanguageMentions(string lowerInput)
    {
        var mentions = new List<string>();

        var languageNames = new Dictionary<string, string[]>
        {
            ["csharp"] = new[] { "c#", "csharp", "c sharp", ".net" },
            ["python"] = new[] { "python", "py" },
            ["javascript"] = new[] { "javascript", "js", "node.js", "nodejs" },
            ["java"] = new[] { "java" }
        };

        foreach (var lang in languageNames)
        {
            if (lang.Value.Any(name => lowerInput.Contains(name)))
                mentions.Add(lang.Key);
        }

        return mentions;
    }
    private int CalculateLanguageScore(string input, LanguageIndicators indicators, bool isExplicitlyMentioned)
    {
        var score = 0;
        var lowerInput = input.ToLowerInvariant();

        // Boost score if language is explicitly mentioned
        if (isExplicitlyMentioned)
            score += 10;

        // Check strong indicators first (higher weight)
        score += indicators.StrongIndicators?.Count(indicator => lowerInput.Contains(indicator.ToLowerInvariant())) * 5 ?? 0;

        // Check keywords (lower weight to avoid false positives)
        score += indicators.Keywords.Count(keyword => lowerInput.Contains(keyword)) * 1;

        // Check patterns (high weight for actual code)
        foreach (var pattern in indicators.Patterns)
        {
            if (Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase))
                score += 4;
        }

        // Check framework keywords
        score += indicators.FrameworkKeywords.Count(keyword => lowerInput.Contains(keyword)) * 3;

        // Check file extensions
        score += indicators.FileExtensions.Count(ext => lowerInput.Contains(ext)) * 2;

        return score;
    }

    private double CalculateConfidence(bool hasGeneralKeywords, bool hasCodePatterns, Dictionary<string, int> languageScores, bool hasLanguageMentions)
    {
        var confidence = 0.0;

        if (hasCodePatterns) confidence += 0.7; // Very high confidence for actual code
        if (hasLanguageMentions) confidence += 0.5; // High confidence for explicit mentions
        if (hasGeneralKeywords) confidence += 0.3; // Medium confidence for keywords
        if (languageScores.Any()) confidence += 0.2; // Language-specific boost

        return Math.Min(confidence, 1.0);
    }
}

public class LanguageIndicators
{
    public string[] Keywords { get; set; } = Array.Empty<string>();
    public string[] Patterns { get; set; } = Array.Empty<string>();
    public string[] FileExtensions { get; set; } = Array.Empty<string>();
    public string[] FrameworkKeywords { get; set; } = Array.Empty<string>();
    public string[] StrongIndicators { get; set; } = Array.Empty<string>(); // New property
}

public class CodingDetectionResult
{
    public bool IsCodingRelated { get; set; }
    public string DetectedLanguage { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public Dictionary<string, int> LanguageScores { get; set; } = new();
    public List<string> MentionedLanguages { get; set; } = new(); // New property
}
