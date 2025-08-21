using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static MEAI_GPT_API.Models.Conversation;

/// <summary>
/// Language-specific configuration for coding assistance
/// </summary>
public class LanguageConfig
{
    public string Name { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
    public List<string> ComplexityIndicators { get; set; } = new();
    public Dictionary<string, string> RelatedTopics { get; set; } = new();
    public string CodeBlockIdentifier { get; set; } = string.Empty;
    public List<string> BestPractices { get; set; } = new();
    public string SystemPrompt { get; set; } = string.Empty;
}

/// <summary>
/// Factory for creating language-specific configurations
/// </summary>
public static class LanguageConfigFactory
{
    private static readonly Dictionary<string, LanguageConfig> _configs = new()
    {
        ["csharp"] = new LanguageConfig
        {
            Name = "C#",
            FileExtension = ".cs",
            CodeBlockIdentifier = "csharp",
            Keywords = new List<string>
            {
                "class", "interface", "struct", "enum", "namespace", "using",
                "async", "await", "linq", "var", "string", "int", "list",
                "array", "loop", "if", "else", "switch", "try", "catch"
            },
            ComplexityIndicators = new List<string>
            {
                "async", "await", "threading", "parallel", "concurrent",
                "generic", "reflection", "lambda", "linq", "expression",
                "interface", "abstract", "inheritance", "polymorphism"
            },
            RelatedTopics = new Dictionary<string, string>
            {
                ["async"] = "Asynchronous Programming",
                ["linq"] = "LINQ and Query Expressions",
                ["generic"] = "Generic Types and Methods",
                ["exception"] = "Exception Handling",
                ["interface"] = "Interfaces and Contracts",
                ["inheritance"] = "Object-Oriented Programming"
            },
            BestPractices = new List<string>
            {
                "Use meaningful variable names",
                "Include appropriate error handling",
                "Add XML documentation comments for methods",
                "Follow C# naming conventions",
                "Consider async/await patterns where appropriate"
            },
            SystemPrompt = @"You are an expert C# developer and programming mentor. Your responses should be:
            - Technically accurate and up-to-date with .NET standards
            - Well-structured with clear explanations
            - Include working code examples with proper error handling
            - Follow C# best practices and conventions
            - Be educational and help the user learn"
        },

        ["python"] = new LanguageConfig
        {
            Name = "Python",
            FileExtension = ".py",
            CodeBlockIdentifier = "python",
            Keywords = new List<string>
            {
                "def", "class", "import", "from", "if", "elif", "else",
                "for", "while", "try", "except", "finally", "with", "lambda",
                "list", "dict", "tuple", "set", "string", "int", "float"
            },
            ComplexityIndicators = new List<string>
            {
                "decorator", "generator", "comprehension", "async", "await",
                "metaclass", "threading", "multiprocessing", "numpy", "pandas"
            },
            RelatedTopics = new Dictionary<string, string>
            {
                ["async"] = "Asynchronous Programming",
                ["pandas"] = "Data Analysis with Pandas",
                ["numpy"] = "Numerical Computing",
                ["flask"] = "Web Development with Flask",
                ["django"] = "Web Framework Development",
                ["machine learning"] = "ML and AI Development"
            },
            BestPractices = new List<string>
            {
                "Follow PEP 8 style guidelines",
                "Use descriptive variable and function names",
                "Include docstrings for functions and classes",
                "Handle exceptions appropriately",
                "Use virtual environments for dependencies"
            },
            SystemPrompt = @"You are an expert Python developer and programming mentor. Your responses should be:
            - Follow Python best practices and PEP standards
            - Include clear, readable code with proper indentation
            - Provide working examples with error handling
            - Be Pythonic in approach and style
            - Help users understand Python idioms and patterns"
        },

        ["javascript"] = new LanguageConfig
        {
            Name = "JavaScript",
            FileExtension = ".js",
            CodeBlockIdentifier = "javascript",
            Keywords = new List<string>
            {
                "function", "const", "let", "var", "class", "import", "export",
                "if", "else", "for", "while", "switch", "try", "catch",
                "promise", "async", "await", "object", "array", "string"
            },
            ComplexityIndicators = new List<string>
            {
                "promise", "async", "await", "closure", "prototype",
                "callback", "event", "dom", "react", "node", "express"
            },
            RelatedTopics = new Dictionary<string, string>
            {
                ["async"] = "Asynchronous JavaScript",
                ["promise"] = "Promises and Async/Await",
                ["react"] = "React Development",
                ["node"] = "Node.js Backend Development",
                ["dom"] = "DOM Manipulation",
                ["es6"] = "Modern JavaScript Features"
            },
            BestPractices = new List<string>
            {
                "Use const/let instead of var",
                "Handle promises and async operations properly",
                "Follow modern ES6+ syntax",
                "Include proper error handling",
                "Use meaningful variable names"
            },
            SystemPrompt = @"You are an expert JavaScript developer and programming mentor. Your responses should be:
            - Use modern ES6+ JavaScript syntax
            - Include working code examples
            - Follow JavaScript best practices
            - Consider both browser and Node.js environments
            - Help users understand asynchronous programming concepts"
        },

        ["java"] = new LanguageConfig
        {
            Name = "Java",
            FileExtension = ".java",
            CodeBlockIdentifier = "java",
            Keywords = new List<string>
            {
                "class", "interface", "extends", "implements", "public", "private",
                "protected", "static", "final", "abstract", "synchronized",
                "try", "catch", "finally", "throw", "throws", "import", "package"
            },
            ComplexityIndicators = new List<string>
            {
                "generic", "reflection", "annotation", "lambda", "stream",
                "concurrent", "thread", "synchronized", "design pattern"
            },
            RelatedTopics = new Dictionary<string, string>
            {
                ["stream"] = "Java Streams API",
                ["lambda"] = "Lambda Expressions",
                ["concurrent"] = "Concurrency and Threading",
                ["spring"] = "Spring Framework",
                ["jpa"] = "Java Persistence API",
                ["junit"] = "Unit Testing with JUnit"
            },
            BestPractices = new List<string>
            {
                "Follow Java naming conventions",
                "Use proper access modifiers",
                "Include Javadoc comments",
                "Handle exceptions appropriately",
                "Follow SOLID principles"
            },
            SystemPrompt = @"You are an expert Java developer and programming mentor. Your responses should be:
            - Follow Java best practices and conventions
            - Include proper object-oriented design principles
            - Provide working code with appropriate error handling
            - Consider performance and memory management
            - Help users understand Java-specific concepts"
        }
    };

    public static LanguageConfig GetConfig(string language)
    {
        var key = language.ToLowerInvariant().Replace("#", "sharp");
        return _configs.TryGetValue(key, out var config) ? config : CreateGenericConfig(language);
    }

    public static List<string> GetSupportedLanguages()
    {
        return _configs.Keys.ToList();
    }

    private static LanguageConfig CreateGenericConfig(string language)
    {
        return new LanguageConfig
        {
            Name = language,
            FileExtension = $".{language.ToLower()}",
            CodeBlockIdentifier = language.ToLower(),
            Keywords = new List<string> { "function", "class", "if", "else", "for", "while" },
            ComplexityIndicators = new List<string> { "async", "recursive", "algorithm" },
            RelatedTopics = new Dictionary<string, string>(),
            BestPractices = new List<string> { "Use meaningful names", "Include error handling", "Follow language conventions" },
            SystemPrompt = $"You are an expert {language} developer and programming mentor."
        };
    }
}

/// <summary>
/// Dynamic coding assistance response
/// </summary>
public class CodingAssistanceResponse
{
    public string Solution { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool IsFromCache { get; set; }
    public double Confidence { get; set; }
    public long ProcessingTimeMs { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public List<CodeExample> CodeExamples { get; set; } = new();
    public string TechnicalLevel { get; set; } = string.Empty;
    public string SolutionComplexity { get; set; } = string.Empty;
    public List<string> RecommendedNextSteps { get; set; } = new();
    public List<string> RelatedTopics { get; set; } = new();
}

/// <summary>
/// Enhanced code example with language-specific metadata
/// </summary>
public class CodeExample
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Language detection service
/// </summary>
public class LanguageDetectionService
{
    private readonly Dictionary<string, LanguageConfig> _languageConfigs;

    public LanguageDetectionService()
    {
        _languageConfigs = LanguageConfigFactory.GetSupportedLanguages()
            .ToDictionary(lang => lang, LanguageConfigFactory.GetConfig);
    }

    public string DetectLanguage(string question, string? codeContext = null)
    {
        var combinedText = $"{question} {codeContext}".ToLowerInvariant();

        // Direct language mentions
        foreach (var config in _languageConfigs)
        {
            var languageName = config.Value.Name.ToLowerInvariant();
            if (combinedText.Contains(languageName) ||
                combinedText.Contains(config.Key) ||
                combinedText.Contains(config.Value.FileExtension))
            {
                return config.Key;
            }
        }

        // Keyword-based detection
        var languageScores = new Dictionary<string, int>();

        foreach (var config in _languageConfigs)
        {
            var score = config.Value.Keywords.Count(keyword =>
                combinedText.Contains(keyword.ToLowerInvariant()));

            if (score > 0)
                languageScores[config.Key] = score;
        }

        if (languageScores.Any())
        {
            return languageScores.OrderByDescending(kvp => kvp.Value).First().Key;
        }

        // Default fallback
        return "csharp"; // or return null to force user specification
    }
}

/// <summary>
/// Dynamic multi-language coding assistance processor
/// </summary>
public partial class DynamicCodingAssistanceService
{
    private readonly ILogger<DynamicCodingAssistanceService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConversationStorageService _conversationStorage;
    private readonly IModelManager _modelManager;
    private readonly DynamicRAGConfiguration _config;
    private readonly Conversation _conversation;
    private readonly string _currentUser;
    private readonly LanguageDetectionService _languageDetection;

    public DynamicCodingAssistanceService(
    ILogger<DynamicCodingAssistanceService> logger,
    IHttpClientFactory httpClientFactory, // Use factory instead
    IConversationStorageService conversationStorage,
    IModelManager modelManager,
    IOptions<DynamicRAGConfiguration> config,
    Conversation conversation)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("OllamaAPI"); // Get the named client
        _conversationStorage = conversationStorage;
        _modelManager = modelManager;
        _config = config.Value;
        _conversation = conversation;
        _currentUser = "system"; // Set a default value
        _languageDetection = new LanguageDetectionService();
    }


    /// <summary>
    /// Processes coding assistance queries for any programming language
    /// </summary>
    /// <param name="codingQuestion">The programming question or problem</param>
    /// <param name="codeContext">Optional: Existing code context or snippets</param>
    /// <param name="language">Programming language (auto-detected if null)</param>
    /// <param name="sessionId">Session identifier for conversation tracking</param>
    /// <param name="includeExamples">Whether to include code examples in response</param>
    /// <param name="difficulty">Difficulty level: beginner, intermediate, advanced</param>
    /// <returns>Coding assistance response with code examples and explanations</returns>
    public async Task<CodingAssistanceResponse> ProcessCodingQueryAsync(
        string codingQuestion,
        string? codeContext = null,
        string? language = null,
        string? sessionId = null,
        bool includeExamples = true,
        string difficulty = "intermediate")
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(codingQuestion))
            throw new ArgumentException("Coding question cannot be empty");

        try
        {
            // Auto-detect language if not specified
            var detectedLanguage = language ?? _languageDetection.DetectLanguage(codingQuestion, codeContext);
            var languageConfig = LanguageConfigFactory.GetConfig(detectedLanguage);

            _logger.LogInformation($"🖥️ Processing {languageConfig.Name} coding query: {codingQuestion}");

            // Get or create session for coding assistance
            var dbSession = await _conversationStorage.GetOrCreateSessionAsync(
                sessionId ?? Guid.NewGuid().ToString(), _currentUser);

            var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);

            // Get required models
            var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultGenerationModel);
            var generationModel = await _modelManager.GetModelAsync(_config.DefaultGenerationModel!);

            if (embeddingModel == null || generationModel == null)
            {
                throw new InvalidOperationException("Required models not available for coding assistance");
            }

            // Generate embedding for the coding question
            var questionEmbedding = await GetEmbeddingAsync(codingQuestion, embeddingModel);

            // Search for similar coding solutions in database (language-specific)
            var similarCodingSolutions = await SearchSimilarCodingConversationsAsync(
                questionEmbedding, detectedLanguage, threshold: 0.8, limit: 3);

            // Check for reusable solutions
            if (similarCodingSolutions.Any())
            {
                var bestMatch = similarCodingSolutions.First();
                if (bestMatch.Entry.WasAppreciated)
                {
                    _logger.LogInformation($"💡 Reusing appreciated {languageConfig.Name} solution (ID: {bestMatch.Entry.Id})");

                    return new CodingAssistanceResponse
                    {
                        Solution = bestMatch.Entry.Answer,
                        Language = detectedLanguage,
                        IsFromCache = true,
                        Confidence = bestMatch.Similarity,
                        ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                        SessionId = dbSession.SessionId,
                        CodeExamples = ExtractCodeExamplesFromAnswer(bestMatch.Entry.Answer, languageConfig),
                        TechnicalLevel = difficulty
                    };
                }
            }

            // Build language-specific coding prompt
            var codingPrompt = BuildLanguageSpecificCodingPrompt(
                codingQuestion, codeContext, difficulty, includeExamples, context, languageConfig);

            // Generate coding assistance response
            var solution = await GenerateCodingResponseAsync(
                codingPrompt, generationModel, context.History, languageConfig);

            // Extract code examples from the response
            var codeExamples = ExtractCodeExamplesFromAnswer(solution, languageConfig);

            // Analyze solution complexity
            var solutionMetadata = AnalyzeSolutionComplexity(solution, codingQuestion, languageConfig);

            // Save coding conversation to database
            var conversationId = await SaveCodingConversationToDatabase(
                dbSession.SessionId, codingQuestion, solution, codeContext,
                generationModel, embeddingModel, questionEmbedding,
                await GetEmbeddingAsync(solution, embeddingModel),
                stopwatch.ElapsedMilliseconds, difficulty, detectedLanguage);

            // Update conversation context
            await UpdateCodingConversationHistory(context, codingQuestion, solution, codeExamples, languageConfig);

            stopwatch.Stop();
            _logger.LogInformation($"✅ {languageConfig.Name} coding assistance completed in {stopwatch.ElapsedMilliseconds}ms");

            return new CodingAssistanceResponse
            {
                Solution = solution,
                Language = detectedLanguage,
                IsFromCache = false,
                Confidence = 0.9,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                SessionId = dbSession.SessionId,
                CodeExamples = codeExamples,
                TechnicalLevel = difficulty,
                SolutionComplexity = solutionMetadata.Complexity,
                RecommendedNextSteps = solutionMetadata.NextSteps,
                RelatedTopics = solutionMetadata.RelatedTopics
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Coding assistance query failed for session {SessionId}", sessionId);
            throw new RAGServiceException("Failed to process coding assistance query", ex);
        }
    }

    /// <summary>
    /// Builds a language-specific prompt for coding assistance
    /// </summary>
    private string BuildLanguageSpecificCodingPrompt(
        string question,
        string? codeContext,
        string difficulty,
        bool includeExamples,
        ConversationContext context,
        LanguageConfig languageConfig)
    {
        var promptBuilder = new StringBuilder();

        promptBuilder.AppendLine($"You are an expert {languageConfig.Name} programming assistant.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("🎯 **CODING ASSISTANCE GUIDELINES:**");
        promptBuilder.AppendLine();

        // Difficulty-specific instructions
        switch (difficulty.ToLowerInvariant())
        {
            case "beginner":
                promptBuilder.AppendLine("**BEGINNER LEVEL ASSISTANCE:**");
                promptBuilder.AppendLine("- Provide detailed explanations for each concept");
                promptBuilder.AppendLine("- Include basic syntax explanations");
                promptBuilder.AppendLine("- Use simple, clear language");
                promptBuilder.AppendLine("- Explain WHY things work, not just HOW");
                break;

            case "intermediate":
                promptBuilder.AppendLine("**INTERMEDIATE LEVEL ASSISTANCE:**");
                promptBuilder.AppendLine("- Focus on best practices and patterns");
                promptBuilder.AppendLine("- Include performance considerations");
                promptBuilder.AppendLine("- Suggest alternative approaches");
                promptBuilder.AppendLine("- Explain trade-offs between solutions");
                break;

            case "advanced":
                promptBuilder.AppendLine("**ADVANCED LEVEL ASSISTANCE:**");
                promptBuilder.AppendLine("- Deep dive into advanced concepts");
                promptBuilder.AppendLine("- Discuss architecture and design patterns");
                promptBuilder.AppendLine("- Include performance optimization tips");
                promptBuilder.AppendLine("- Consider scalability and maintainability");
                break;
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("**RESPONSE FORMAT:**");
        promptBuilder.AppendLine("1. **Brief Explanation** - Summarize the solution approach");
        promptBuilder.AppendLine($"2. **Code Solution** - Provide working {languageConfig.Name} code with comments");
        promptBuilder.AppendLine("3. **Key Points** - Highlight important concepts");
        promptBuilder.AppendLine($"4. **Best Practices** - Mention relevant {languageConfig.Name} coding standards");

        if (includeExamples)
        {
            promptBuilder.AppendLine("5. **Usage Example** - Show how to use the solution");
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine($"**{languageConfig.Name.ToUpper()} CODING STANDARDS:**");
        foreach (var practice in languageConfig.BestPractices)
        {
            promptBuilder.AppendLine($"- {practice}");
        }
        promptBuilder.AppendLine();

        // Add context if provided
        if (!string.IsNullOrEmpty(codeContext))
        {
            promptBuilder.AppendLine("**EXISTING CODE CONTEXT:**");
            promptBuilder.AppendLine($"```{languageConfig.CodeBlockIdentifier}");
            promptBuilder.AppendLine(codeContext);
            promptBuilder.AppendLine("```");
            promptBuilder.AppendLine();
        }

        // Add conversation history context
        if (context.History.Any())
        {
            promptBuilder.AppendLine("**CONVERSATION CONTEXT:**");
            var recentCodingQuestions = context.History.TakeLast(2)
                .Where(h => IsCodingRelated(h.Question, languageConfig))
                .ToList();

            foreach (var turn in recentCodingQuestions)
            {
                promptBuilder.AppendLine($"Previous Q: {turn.Question}");
                promptBuilder.AppendLine($"Previous A: {TruncateText(turn.Answer, 200)}");
            }
            promptBuilder.AppendLine();
        }

        promptBuilder.AppendLine("**USER QUESTION:**");
        promptBuilder.AppendLine(question);

        return promptBuilder.ToString();
    }

    /// <summary>
    /// Generates language-specific coding assistance response
    /// </summary>
    private async Task<string> GenerateCodingResponseAsync(
        string prompt,
        ModelConfiguration generationModel,
        List<ConversationTurn> history,
        LanguageConfig languageConfig)
    {
        var messages = new List<object>();

        // Language-specific system message
        messages.Add(new
        {
            role = "system",
            content = $@"{languageConfig.SystemPrompt}
            
            Always format code blocks with ```{languageConfig.CodeBlockIdentifier}
            Focus on {languageConfig.Name}-specific best practices and idioms."
        });

        // Add recent coding-related conversation history
        var codingHistory = history.TakeLast(4)
            .Where(turn => IsCodingRelated(turn.Question, languageConfig))
            .ToList();

        foreach (var turn in codingHistory)
        {
            messages.Add(new { role = "user", content = turn.Question });
            messages.Add(new { role = "assistant", content = TruncateText(turn.Answer, 1000) });
        }

        // Add current prompt
        messages.Add(new { role = "user", content = prompt });

        var requestData = new
        {
            model = generationModel.Name,
            messages,
            temperature = 0.2, // Lower temperature for more focused coding responses
            stream = false,
            options = new Dictionary<string, object>
            {
                { "num_ctx", 6000 },
                { "num_predict", 3000 },
                { "top_p", 0.9 },
                { "repeat_penalty", 1.05 }
            }
        };

        try
        {
            _logger.LogInformation($"🤖 Generating {languageConfig.Name} response with model: {generationModel.Name}");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var response = await _httpClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ {languageConfig.Name} response generation failed: {response.StatusCode} - {errorContent}");
                return $"I apologize, but I'm having trouble generating a {languageConfig.Name} solution right now. Please try again.";
            }

            var json = await response.Content.ReadAsStringAsync();
            return await ParseLLMResponse(json, false, "");
        }
        catch (OperationCanceledException)
        {
            _logger.LogError($"❌ {languageConfig.Name} response generation timed out");
            return $"The {languageConfig.Name} response generation timed out. Please try with a simpler question.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ {languageConfig.Name} response generation failed");
            return $"I apologize, but I encountered an error while generating the {languageConfig.Name} solution.";
        }
    }

    /// <summary>
    /// Extracts code examples from the generated response for any language
    /// </summary>
    private List<CodeExample> ExtractCodeExamplesFromAnswer(string answer, LanguageConfig languageConfig)
    {
        var codeExamples = new List<CodeExample>();

        // Extract language-specific code blocks
        var codeBlockPattern = $@"```{languageConfig.CodeBlockIdentifier}\s*\n(.*?)\n```";
        var matches = Regex.Matches(answer, codeBlockPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int i = 0; i < matches.Count; i++)
        {
            var codeContent = matches[i].Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(codeContent))
            {
                codeExamples.Add(new CodeExample
                {
                    Code = codeContent,
                    Language = languageConfig.Name,
                    Description = ExtractCodeDescription(answer, matches[i].Index),
                    OrderIndex = i + 1,
                    Tags = ExtractCodeTags(codeContent, languageConfig)
                });
            }
        }

        return codeExamples;
    }

    /// <summary>
    /// Extracts tags from code content based on language-specific patterns
    /// </summary>
    private List<string> ExtractCodeTags(string codeContent, LanguageConfig languageConfig)
    {
        var tags = new List<string>();
        var lowerCode = codeContent.ToLowerInvariant();

        // Check for complexity indicators
        foreach (var indicator in languageConfig.ComplexityIndicators)
        {
            if (lowerCode.Contains(indicator.ToLowerInvariant()))
            {
                tags.Add(indicator);
            }
        }

        // Check for related topics
        foreach (var topic in languageConfig.RelatedTopics.Keys)
        {
            if (lowerCode.Contains(topic.ToLowerInvariant()))
            {
                tags.Add(languageConfig.RelatedTopics[topic]);
            }
        }

        return tags.Distinct().Take(5).ToList();
    }

    /// <summary>
    /// Determines if a question is coding-related for a specific language
    /// </summary>
    private bool IsCodingRelated(string question, LanguageConfig languageConfig)
    {
        var lowerQuestion = question.ToLowerInvariant();
        var generalCodingKeywords = new[]
        {
            "code", "function", "method", "programming", "algorithm",
            "debug", "compile", "syntax", "error", "bug", "implement"
        };

        // Check language-specific keywords
        var hasLanguageKeywords = languageConfig.Keywords
            .Any(keyword => lowerQuestion.Contains(keyword.ToLowerInvariant()));

        // Check general coding keywords
        var hasGeneralKeywords = generalCodingKeywords
            .Any(keyword => lowerQuestion.Contains(keyword));

        // Check language name
        var hasLanguageName = lowerQuestion.Contains(languageConfig.Name.ToLowerInvariant());

        return hasLanguageKeywords || hasGeneralKeywords || hasLanguageName;
    }

    /// <summary>
    /// Analyzes solution complexity for any programming language
    /// </summary>
    private SolutionMetadata AnalyzeSolutionComplexity(string solution, string originalQuestion, LanguageConfig languageConfig)
    {
        var metadata = new SolutionMetadata();

        var lowerSolution = solution.ToLowerInvariant();
        var lowerQuestion = originalQuestion.ToLowerInvariant();

        // Determine complexity based on language-specific indicators
        var complexityScore = languageConfig.ComplexityIndicators
            .Count(indicator => lowerSolution.Contains(indicator.ToLowerInvariant()));

        metadata.Complexity = complexityScore switch
        {
            0 => "Simple",
            1 or 2 => "Moderate",
            3 or 4 => "Complex",
            _ => "Advanced"
        };

        // Language-agnostic next steps
        if (lowerQuestion.Contains("beginner") || lowerQuestion.Contains("start"))
        {
            metadata.NextSteps = new List<string>
            {
                $"Practice the basic {languageConfig.Name} syntax shown",
                "Try modifying the example with your own data",
                $"Read about {languageConfig.Name} fundamentals and best practices"
            };
        }
        else if (lowerQuestion.Contains("performance") || lowerQuestion.Contains("optimize"))
        {
            metadata.NextSteps = new List<string>
            {
                "Profile your application to identify bottlenecks",
                $"Consider {languageConfig.Name}-specific performance patterns",
                "Look into language-specific optimization techniques"
            };
        }
        else
        {
            metadata.NextSteps = new List<string>
            {
                "Test the solution with different inputs",
                "Consider edge cases and error handling",
                $"Review {languageConfig.Name} best practices for similar scenarios"
            };
        }

        // Extract related topics based on language configuration
        metadata.RelatedTopics = ExtractRelatedTopics(lowerSolution, languageConfig);

        return metadata;
    }

    /// <summary>
    /// Extracts related programming topics from the solution based on language configuration
    /// </summary>
    private List<string> ExtractRelatedTopics(string solution, LanguageConfig languageConfig)
    {
        var topics = new List<string>();

        foreach (var topicKeyword in languageConfig.RelatedTopics)
        {
            if (solution.Contains(topicKeyword.Key))
            {
                topics.Add(topicKeyword.Value);
            }
        }

        return topics.Distinct().Take(5).ToList();
    }

    /// <summary>
    /// Saves coding conversation to database with language-specific metadata
    /// </summary>
    private async Task<int> SaveCodingConversationToDatabase(
        string sessionId, string question, string answer, string? codeContext,
        ModelConfiguration generationModel, ModelConfiguration embeddingModel,
        List<float> questionEmbedding, List<float> answerEmbedding,
        long processingTimeMs, string difficulty, string language)
    {
        try
        {
            var languageConfig = LanguageConfigFactory.GetConfig(language);

            var entry = new ConversationEntry
            {
                SessionId = sessionId,
                Question = question,
                Answer = answer,
                CreatedAt = DateTime.UtcNow,
                QuestionEmbedding = questionEmbedding,
                AnswerEmbedding = answerEmbedding,
                NamedEntities = ExtractCodingEntities(question, answer, languageConfig),
                WasAppreciated = false,
                TopicTag = $"coding_{language}",
                FollowUpToId = null,
                GenerationModel = generationModel.Name,
                EmbeddingModel = embeddingModel.Name,
                Confidence = 0.9,
                ProcessingTimeMs = processingTimeMs,
                RelevantChunksCount = 0,
                Sources = new List<string> { $"{languageConfig.Name} Coding Assistance" },
                IsFromCorrection = false,
                Plant = $"coding_assistance_{language}" // Language-specific plant designation
            };

            await _conversationStorage.SaveConversationAsync(entry);
            _logger.LogInformation($"💾 Saved {languageConfig.Name} coding conversation {entry.Id} to database");
            return entry.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save coding conversation to database");
            return 0;
        }
    }

    /// <summary>
    /// Extracts coding-related entities from question and answer for any language
    /// </summary>
    private List<string> ExtractCodingEntities(string question, string answer, LanguageConfig languageConfig)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var combinedText = $"{question} {answer}";

        // Extract language-specific patterns based on the language
        var patterns = GetLanguageSpecificPatterns(languageConfig);

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(combinedText, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 2)
                    entities.Add(match.Groups[2].Value);
                else if (match.Groups.Count > 1)
                    entities.Add(match.Groups[1].Value);
            }
        }

        // Extract keywords specific to the language
        foreach (var keyword in languageConfig.Keywords)
        {
            if (combinedText.ToLowerInvariant().Contains(keyword.ToLowerInvariant()))
            {
                entities.Add(keyword);
            }
        }

        return entities.Take(10).ToList();
    }

    /// <summary>
    /// Gets language-specific regex patterns for entity extraction
    /// </summary>
    private List<string> GetLanguageSpecificPatterns(LanguageConfig languageConfig)
    {
        return languageConfig.Name.ToLowerInvariant() switch
        {
            "c#" => new List<string>
            {
                @"\b(class|interface|struct|enum)\s+(\w+)", // Type definitions
                @"\b(public|private|protected|internal)\s+(\w+)", // Access modifiers with types
                @"\b(\w+)\s*\(.*?\)", // Method calls
                @"using\s+([^;]+);", // Using statements
                @"namespace\s+([^{]+)" // Namespaces
            },
            "python" => new List<string>
            {
                @"\bdef\s+(\w+)", // Function definitions
                @"\bclass\s+(\w+)", // Class definitions
                @"\bimport\s+(\w+)", // Import statements
                @"\bfrom\s+(\w+)", // From imports
                @"@(\w+)" // Decorators
            },
            "javascript" => new List<string>
{
    @"\bfunction\s+(\w+)", // Function definitions
    @"\bclass\s+(\w+)", // Class definitions  
    @"\b(const|let|var)\s+(\w+)", // Variable declarations
    @"\bimport\s+.*?from\s+['""]([^'""]+)['""]", // Import statements
    @"\bexport\s+(?:default\s+)?(\w+)" // Export statements
},

            "java" => new List<string>
            {
                @"\b(class|interface|enum)\s+(\w+)", // Type definitions
                @"\b(public|private|protected)\s+(static\s+)?(\w+)", // Method/field definitions
                @"\bpackage\s+([^;]+);", // Package statements
                @"\bimport\s+([^;]+);", // Import statements
                @"@(\w+)" // Annotations
            },
            _ => new List<string>
            {
                @"\b(function|class|method|def)\s+(\w+)", // Generic patterns
                @"\b(import|include|using)\s+(\w+)" // Generic import patterns
            }
        };
    }

    /// <summary>
    /// Updates conversation history for multi-language coding assistance
    /// </summary>
    private async Task UpdateCodingConversationHistory(
        ConversationContext context, string question, string answer,
        List<CodeExample> codeExamples, LanguageConfig languageConfig)
    {
        try
        {
            var turn = new ConversationTurn
            {
                Question = question,
                Answer = answer,
                Timestamp = DateTime.Now,
                Sources = new List<string> { $"{languageConfig.Name} Coding Assistance" }
            };

            context.History.Add(turn);

            if (context.History.Count > 15) // Keep more history for coding sessions
                context.History = context.History.TakeLast(15).ToList();

            context.LastAccessed = DateTime.Now;

            // Add language-specific entities to context
            var codingEntities = ExtractCodingEntities(question, answer, languageConfig);
            foreach (var entity in codingEntities)
            {
                if (!context.NamedEntities.Contains(entity, StringComparer.OrdinalIgnoreCase))
                    context.NamedEntities.Add(entity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update coding conversation history");
        }
    }

    /// <summary>
    /// Searches for similar coding conversations in the database for a specific language
    /// </summary>
    private async Task<List<ConversationSearchResult>> SearchSimilarCodingConversationsAsync(
        List<float> questionEmbedding, string language, double threshold = 0.8, int limit = 3)
    {
        try
        {
            // Search specifically in language-specific coding conversations
            return await _conversationStorage.SearchSimilarConversationsAsync(
                questionEmbedding, $"coding_assistance_{language}", threshold, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to search similar {language} coding conversations");
            return new List<ConversationSearchResult>();
        }
    }

    /// <summary>
    /// Extracts description for code examples based on surrounding text
    /// </summary>
    private string ExtractCodeDescription(string fullText, int codeBlockIndex)
    {
        // Look for text before the code block that might describe it
        var beforeCodeBlock = fullText.Substring(0, codeBlockIndex);
        var lines = beforeCodeBlock.Split('\n').Reverse().Take(3);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) &&
                !trimmed.StartsWith("#") &&
                trimmed.Length > 10)
            {
                return trimmed;
            }
        }

        return "Code example";
    }

    /// <summary>
    /// Truncates text to specified length with ellipsis
    /// </summary>
    private string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength - 3) + "...";
    }

    /// <summary>
    /// Placeholder method for embedding generation
    /// </summary>
    private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration embeddingModel)
    {
        // This should implement your actual embedding generation logic
        // For now, returning empty list as placeholder
        await Task.Delay(1); // Simulate async operation
        return new List<float>();
    }

    /// <summary>
    /// Placeholder method for LLM response parsing
    /// </summary>
    private async Task<string> ParseLLMResponse(string json, bool isStreaming, string fallback)
    {
        // This should implement your actual LLM response parsing logic
        await Task.Delay(1); // Simulate async operation

        try
        {
            var response = JsonSerializer.Deserialize<dynamic>(json);
            // Extract the actual response content based on your LLM API format
            return response?.ToString() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>
/// Metadata about the coding solution
/// </summary>
public class SolutionMetadata
{
    public string Complexity { get; set; } = string.Empty;
    public List<string> NextSteps { get; set; } = new();
    public List<string> RelatedTopics { get; set; } = new();
}

/// <summary>
/// Extension methods for enhanced functionality
/// </summary>
public static class CodingAssistanceExtensions
{
    /// <summary>
    /// Adds a new language configuration dynamically
    /// </summary>
    public static void AddLanguageConfig(string languageKey, LanguageConfig config)
    {
        // Implementation would update the internal dictionary in LanguageConfigFactory
        // This could be enhanced to support runtime language addition
    }

    /// <summary>
    /// Gets supported file extensions for all configured languages
    /// </summary>
    public static Dictionary<string, string> GetSupportedFileExtensions()
    {
        var extensions = new Dictionary<string, string>();
        foreach (var lang in LanguageConfigFactory.GetSupportedLanguages())
        {
            var config = LanguageConfigFactory.GetConfig(lang);
            extensions[config.FileExtension] = lang;
        }
        return extensions;
    }

    /// <summary>
    /// Validates if a language is supported
    /// </summary>
    public static bool IsLanguageSupported(string language)
    {
        var supportedLanguages = LanguageConfigFactory.GetSupportedLanguages();
        return supportedLanguages.Contains(language.ToLowerInvariant().Replace("#", "sharp"));
    }
}