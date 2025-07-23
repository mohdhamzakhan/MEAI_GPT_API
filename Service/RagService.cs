using MEAI_GPT_API.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using static MEAI_GPT_API.Models.Conversation;

public class RagService
{
    private static readonly HttpClient httpClient = new()
    {
        BaseAddress = new Uri("http://10.235.20.58:11434"),
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static string CACHE_FILE = "policy_embeddings_cache.json";
    private static readonly string POLICIES_FOLDER = "D:\\Code\\MEAIGPT\\MEAIRAG\\policies";
    private static readonly string[] SUPPORTED_EXTENSIONS = { ".txt"};
    //private static readonly string[] SUPPORTED_EXTENSIONS = { ".txt", ".md", ".doc", ".docx", ".pdf" };
    private static string CORRECTIONS_CACHE_FILE = "corrections_cache.json";

    private readonly ConcurrentBag<EmbeddingData> _cachedEmbeddings = new();
    private readonly ConcurrentBag<CorrectionEntry> _correctionsCache = new();
    private readonly object _lockObject = new();
    private int _nextCorrectionId = 1;

    public async Task InitializeAsync()
    {
        Console.WriteLine("Initializing RAG system...");
        await LoadOrGenerateEmbeddings();
        LoadCorrections();
        Console.WriteLine($"Ready! Loaded {_cachedEmbeddings.Count} policy chunks.");
    }

    private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();

    public async Task<QueryResponse> ProcessQueryAsync(
        string question,
        string model,
        int maxResults = 10,
        bool meai_info = true,
        string? sessionId = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Get or create conversation context
        var conversationContext = GetOrCreateConversationContext(sessionId);

        // Check if user wants to clear history FIRST
        if (IsHistoryClearRequest(question))
        {
            // Clear the conversation history
            conversationContext.History.Clear();
            conversationContext.RelevantChunks.Clear();
            conversationContext.LastAccessed = DateTime.UtcNow;

            stopwatch.Stop();

            return new QueryResponse
            {
                Answer = "History cleared. How can I assist you today?",
                IsFromCorrection = false,
                Sources = new List<string>(),
                Confidence = 1.0,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                RelevantChunks = new List<RelevantChunk>()
            };
        }

        // Enhanced query with conversation context
        var contextualQuery = BuildContextualQuery(question, conversationContext.History);
        List<EmbeddingData> topChunks = new();

        if (meai_info)
        {
            // Check if we can reuse cached chunks or need new ones
            if (conversationContext.RelevantChunks.Count > 0 && IsFollowUpQuestion(question, conversationContext.History))
            {
                topChunks = conversationContext.RelevantChunks;
                Console.WriteLine($"✅ Reusing context for follow-up question in session {sessionId}");
            }
            else
            {
                // Get fresh embeddings for new topic
                var queryEmbedding = await GetEmbedding(contextualQuery, model);
                topChunks = _cachedEmbeddings
                    .Select(e => new EmbeddingData(e.Text, e.Vector, e.SourceFile, e.LastModified)
                    {
                        Similarity = CosineSimilarity(queryEmbedding, e.Vector)
                    })
                    .OrderByDescending(e => e.Similarity)
                    .Take(maxResults)
                    .ToList();
                conversationContext.RelevantChunks = topChunks;
            }
        }

        // Generate response with conversation history
        var prompt = BuildPrompt(question, topChunks, conversationContext.History, meai_info);
        var answer = await GenerateResponse(prompt, model, temperature: 0.2);

        // Update conversation history
        conversationContext.History.Add(new ConversationTurn
        {
            Question = question,
            Answer = answer,
            Timestamp = DateTime.UtcNow,
            Sources = topChunks.Select(c => Path.GetFileName(c.SourceFile)).Distinct().ToList()
        });

        // Keep only last 5 conversation turns
        if (conversationContext.History.Count > 5)
        {
            conversationContext.History = conversationContext.History.TakeLast(5).ToList();
        }

        conversationContext.LastAccessed = DateTime.UtcNow;
        stopwatch.Stop();

        return new QueryResponse
        {
            Answer = answer,
            IsFromCorrection = false,
            Sources = topChunks.Select(c => Path.GetFileName(c.SourceFile)).Distinct().ToList(),
            Confidence = topChunks.FirstOrDefault()?.Similarity ?? 0,
            ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
            RelevantChunks = topChunks.Select(c => new RelevantChunk
            {
                Text = c.Text.Length > 200 ? c.Text.Substring(0, 200) + "..." : c.Text,
                Source = Path.GetFileName(c.SourceFile),
                Similarity = c.Similarity
            }).ToList()
        };
    }

    private ConversationContext GetOrCreateConversationContext(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return new ConversationContext();

        return _sessionContexts.GetOrAdd(sessionId, _ => new ConversationContext());
    }

    private string BuildContextualQuery(string currentQuestion, List<ConversationTurn> history)
    {
        if (history.Count == 0) return currentQuestion;

        // Check for pronouns or references that need context
        var contextualPhrases = new[] { "this", "that", "it", "they", "what about", "and", "also" };

        if (contextualPhrases.Any(phrase => currentQuestion.ToLower().Contains(phrase)))
        {
            var lastTurn = history.LastOrDefault();
            if (lastTurn != null)
            {
                return $"Previous context: {lastTurn.Question} -> {lastTurn.Answer.Substring(0, Math.Min(100, lastTurn.Answer.Length))}... Current question: {currentQuestion}";
            }
        }

        return currentQuestion;
    }

    private bool IsFollowUpQuestion(string question, List<ConversationTurn> history)
    {
        if (history.Count == 0) return false;

        var followUpIndicators = new[]
        {
        "what about", "and", "also", "additionally", "furthermore",
        "this", "that", "it", "they", "more details", "explain",
        "how", "why", "when", "where"
    };

        return followUpIndicators.Any(indicator =>
            question.ToLower().StartsWith(indicator) ||
            question.ToLower().Contains($" {indicator} "));
    }

    // Cleanup old sessions periodically
    public void CleanupOldSessions()
    {
        var cutoffTime = DateTime.UtcNow.AddHours(-2); // Keep sessions for 2 hours
        var oldSessions = _sessionContexts
            .Where(kvp => kvp.Value.LastAccessed < cutoffTime)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in oldSessions)
        {
            _sessionContexts.TryRemove(sessionId, out _);
        }
    }


    public async Task SaveCorrectionAsync(string question, string correctAnswer, string model)
    {
        lock (_lockObject)
        {
            // Remove existing correction for the same question
            var existing = _correctionsCache.Where(c =>
                c.Question.Equals(question, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var item in existing)
            {
                _correctionsCache.TryTake(out _);
            }

            var correction = new CorrectionEntry
            {
                Id = _nextCorrectionId++,
                Question = question,
                Answer = correctAnswer,
                Date = DateTime.UtcNow
            };

            _correctionsCache.Add(correction);
        }

        await SaveCorrectionsToFile();

        // 🚀 Add correction to embeddings
        try
        {
            var combinedText = $"{question}\n{correctAnswer}";
            var embedding = await GetEmbedding(combinedText, model); // or your default model for embeddings
            var sourceName = $"correction_{DateTime.UtcNow:yyyyMMddHHmmss}_{_nextCorrectionId}.txt";

            _cachedEmbeddings.Add(new EmbeddingData(
                combinedText,
                embedding,
                sourceName,
                DateTime.UtcNow
            ));

            await SaveToCache();
            Console.WriteLine("\u001b[32mCorrection embedded and cached successfully.\u001b[0m");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\u001b[31mFailed to embed correction: {ex.Message}\u001b[0m");
        }
    }


    public SystemStatus GetSystemStatus()
    {
        return new SystemStatus
        {
            TotalEmbeddings = _cachedEmbeddings.Count,
            TotalCorrections = _correctionsCache.Count,
            LastUpdated = DateTime.UtcNow,
            IsHealthy = true,
            PoliciesFolder = POLICIES_FOLDER,
            SupportedExtensions = SUPPORTED_EXTENSIONS.ToList()
        };
    }

    public async Task RefreshEmbeddingsAsync(string model)
    {
        _cachedEmbeddings.Clear();
        await LoadOrGenerateEmbeddings(model);
    }

    public List<CorrectionEntry> GetRecentCorrections(int limit = 50)
    {
        return _correctionsCache
            .OrderByDescending(c => c.Date)
            .Take(limit)
            .ToList();
    }

    public async Task<bool> DeleteCorrectionAsync(int id)
    {
        var correction = _correctionsCache.FirstOrDefault(c => c.Id == id);
        if (correction == null) return false;

        var tempList = _correctionsCache.Where(c => c.Id != id).ToList();
        _correctionsCache.Clear();
        foreach (var item in tempList)
        {
            _correctionsCache.Add(item);
        }

        await SaveCorrectionsToFile();
        return true;
    }

    public async Task ProcessUploadedPolicyAsync(IFormFile file, string model)
    {
        var tempPath = Path.GetTempFileName();
        await using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        try
        {
            var content = await ReadFileContent(tempPath);
            if (!string.IsNullOrWhiteSpace(content))
            {
                var policyName = Path.GetFileNameWithoutExtension(file.FileName);
                var chunks = ChunkText(content, file.FileName, maxTokens: 256);

                foreach (var chunk in chunks)
                {
                    var embedding = await GetEmbedding(chunk.Text, model);
                    _cachedEmbeddings.Add(new EmbeddingData(
                        chunk.Text, embedding, file.FileName, DateTime.UtcNow));
                    await Task.Delay(50); // Rate limiting
                }

                await SaveToCache();
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // Private helper methods (same as original but adapted)
    private async Task LoadOrGenerateEmbeddings(string model = "llama3.1:8b")
    {
        CACHE_FILE = model.Replace('.', '_').Replace(':', '_') + "_" + "policy_embeddings_cache.json";
        if (File.Exists(CACHE_FILE))
        {
            await LoadFromCache(model,CACHE_FILE);
        }

        var policyFiles = GetAllPolicyFiles();
        int totalChunksAdded = 0;

        foreach (var filePath in policyFiles)
        {
            var fileInfo = new FileInfo(filePath);
            var lastModified = fileInfo.LastWriteTimeUtc;

            if (_cachedEmbeddings.Any(e => e.SourceFile == filePath && e.LastModified == lastModified))
            {
                continue;
            }

            var content = await ReadFileContent(filePath);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var chunks = ChunkText(content, filePath, maxTokens: 256);

            foreach (var chunk in chunks)
            {
                var embedding = await GetEmbedding(chunk.Text, model);
                _cachedEmbeddings.Add(new EmbeddingData(chunk.Text, embedding, filePath, lastModified));
                totalChunksAdded++;
            }
        }

        await SaveToCache();
    }

    private void LoadCorrections()
    {
        if (File.Exists(CORRECTIONS_CACHE_FILE))
        {
            var json = File.ReadAllText(CORRECTIONS_CACHE_FILE);
            var corrections = JsonSerializer.Deserialize<List<CorrectionEntry>>(json) ?? new List<CorrectionEntry>();

            foreach (var correction in corrections)
            {
                if (correction.Id >= _nextCorrectionId)
                    _nextCorrectionId = correction.Id + 1;
                _correctionsCache.Add(correction);
            }
        }
    }

    private async Task SaveCorrectionsToFile()
    {
        var corrections = _correctionsCache.ToList();
        var json = JsonSerializer.Serialize(corrections, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(CORRECTIONS_CACHE_FILE, json);
    }

    private List<string> GetAllPolicyFiles() =>
        Directory.GetFiles(POLICIES_FOLDER, "*.*", SearchOption.AllDirectories)
            .Where(f => SUPPORTED_EXTENSIONS.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

    private async Task<string> ReadFileContent(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".txt" || ext == ".md")
            return await File.ReadAllTextAsync(filePath);
        if (ext == ".pdf")
            return ExtractTextFromPdf(filePath);

        return "";
    }

    private string ExtractTextFromPdf(string filePath)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(filePath);
        foreach (var page in pdf.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    private List<(string Text, string SourceFile)> ChunkText(string text, string sourceFile, int maxTokens = 256)
    {
        var chunks = new List<(string, string)>();
        var sentences = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var sb = new StringBuilder();
        int tokenCount = 0;
        foreach (var sentence in sentences)
        {
            int sentenceTokens = EstimateTokenCount(sentence);
            if (tokenCount + sentenceTokens > maxTokens)
            {
                if (sb.Length > 0)
                {
                    chunks.Add((sb.ToString(), sourceFile));
                    sb.Clear();
                    tokenCount = 0;
                }
            }
            sb.AppendLine(sentence);
            tokenCount += sentenceTokens;
        }
        if (sb.Length > 0)
            chunks.Add((sb.ToString(), sourceFile));
        return chunks;
    }

    private int EstimateTokenCount(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private async Task<List<float>> GetEmbedding(string text, string model)
    {
        var request = new { model = model, prompt = text };
        var response = await httpClient.PostAsJsonAsync("/api/embeddings", request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToList();
    }

    private async Task<string> GenerateResponse(string prompt,string model, double temperature = 0.0)
    
    {
        var request = new
        {
            model = model.Contains("nomic") ? "llama3.1:8b" : model,
            prompt = prompt,
            temperature = temperature,
            stream = false
        };

        var response = await httpClient.PostAsJsonAsync("/api/generate", request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("response").GetString() ?? "";
    }

    private double CosineSimilarity(List<float> a, List<float> b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB) + 1e-10);
    }

    private string BuildPrompt(string question, IEnumerable<dynamic> chunks, List<ConversationTurn> conversationHistory, bool is_meai)
    {
        var prompt = new StringBuilder();

        // Check if user wants to clear history
        if (IsHistoryClearRequest(question))
        {
            return "History cleared. How can I assist you today?";
        }

        if (is_meai)
        {
            prompt.AppendLine("You are MEAI Policy Assistant, an expert HR policy advisor with deep knowledge of company policies and procedures.");
            prompt.AppendLine();
            prompt.AppendLine("CORE INSTRUCTIONS:");
            prompt.AppendLine("• Provide accurate, actionable answers based ONLY on the provided policy documents");
            prompt.AppendLine("• Use clear, professional language that employees can easily understand");
            prompt.AppendLine("• Structure responses with bullet points or numbered lists when appropriate");
            prompt.AppendLine("• Always cite the exact policy file name in [brackets] after each key point");
            prompt.AppendLine("• Be concise but thorough - include all relevant details without unnecessary elaboration");
            prompt.AppendLine();

            prompt.AppendLine("RESPONSE GUIDELINES:");
            prompt.AppendLine("• For step-by-step processes: Use numbered lists (1, 2, 3...)");
            prompt.AppendLine("• For multiple requirements/benefits: Use bullet points (•)");
            prompt.AppendLine("• For policy clarifications: Provide direct quotes when helpful");
            prompt.AppendLine("• For calculations (leave days, benefits): Show the formula or logic");
            prompt.AppendLine("• For deadlines/timelines: Highlight dates and timeframes clearly");
            prompt.AppendLine();

            prompt.AppendLine("LEAVE ABBREVIATIONS:");
            prompt.AppendLine("• CL = Casual Leave");
            prompt.AppendLine("• SL = Sick Leave");
            prompt.AppendLine("• COFF = Compensatory Off");
            prompt.AppendLine("• EL/PL = Earned Leave / Privilege Leave");
            prompt.AppendLine("• ML = Maternity Leave");
            prompt.AppendLine();

            prompt.AppendLine("CONVERSATION HANDLING:");
            if (conversationHistory.Count > 0)
            {
                prompt.AppendLine("• Review the conversation history to understand context");
                prompt.AppendLine("• For follow-up questions, build upon previous responses");
                prompt.AppendLine("• When users say 'this', 'that', 'it', refer to the previous conversation");
                prompt.AppendLine("• Avoid repeating information already provided unless clarification is needed");
            }
            else
            {
                prompt.AppendLine("• This is the start of a new conversation");
            }
            prompt.AppendLine();

            prompt.AppendLine("ERROR HANDLING:");
            prompt.AppendLine("• If policy information is not available: 'I don't have specific policy information for your query. Please check with HR or refer to the complete policy documents.'");
            prompt.AppendLine("• If information is ambiguous: 'Based on available policy information, [provide what you can], but please confirm specific details with HR.'");
            prompt.AppendLine("• If multiple interpretations exist: Present all valid interpretations clearly");
            prompt.AppendLine();

            prompt.AppendLine("FORMATTING REQUIREMENTS:");
            prompt.AppendLine("• Use **bold** for important deadlines, amounts, or key requirements");
            prompt.AppendLine("• Use *italics* for emphasis on critical points");
            prompt.AppendLine("• Include relevant policy section numbers if available");
            prompt.AppendLine("• End responses with: 'For additional clarification, please contact HR.'");
            prompt.AppendLine();
        }

        // Add conversation history with better formatting
        if (conversationHistory.Count > 0)
        {
            prompt.AppendLine("=== RECENT CONVERSATION ===");
            var recentTurns = conversationHistory.TakeLast(3).ToList();

            for (int i = 0; i < recentTurns.Count; i++)
            {
                var turn = recentTurns[i];
                prompt.AppendLine($"Q{i + 1}: {turn.Question}");

                // Truncate long previous answers to keep prompt manageable
                var truncatedAnswer = turn.Answer.Length > 300
                    ? turn.Answer.Substring(0, 300) + "... [truncated]"
                    : turn.Answer;

                prompt.AppendLine($"A{i + 1}: {truncatedAnswer}");

                if (turn.Sources.Any())
                {
                    prompt.AppendLine($"Sources: [{string.Join(", ", turn.Sources)}]");
                }
                prompt.AppendLine();
            }
            prompt.AppendLine("=== END CONVERSATION ===");
            prompt.AppendLine();
        }

        // Add relevant policy documents with better organization
        if (chunks.Any())
        {
            prompt.AppendLine("=== RELEVANT POLICY INFORMATION ===");

            var groupedChunks = chunks
                .GroupBy(c => c.SourceFile)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in groupedChunks)
            {
                prompt.AppendLine($"📋 **{Path.GetFileNameWithoutExtension(group.Key)}**");
                prompt.AppendLine($"Source: {group.Key}");
                prompt.AppendLine();

                foreach (var chunk in group)
                {
                    // Clean up chunk text for better readability
                    var cleanText = chunk.Text
                        .Replace("\r\n", "\n")
                        .Replace("\n\n\n", "\n\n")
                        .Trim();

                    prompt.AppendLine(cleanText);
                    prompt.AppendLine();
                }
                prompt.AppendLine("---");
            }
            prompt.AppendLine("=== END POLICY INFORMATION ===");
            prompt.AppendLine();
        }

        // Enhanced question handling
        prompt.AppendLine("=== CURRENT QUERY ===");

        // Detect question type and add specific guidance
        var questionLower = question.ToLower();
        if (questionLower.Contains("how to") || questionLower.Contains("process") || questionLower.Contains("apply"))
        {
            prompt.AppendLine("Note: This appears to be a process question. Provide step-by-step instructions.");
        }
        else if (questionLower.Contains("how much") || questionLower.Contains("how many") || questionLower.Contains("calculate"))
        {
            prompt.AppendLine("Note: This appears to be a calculation question. Show the formula and example if applicable.");
        }
        else if (questionLower.Contains("when") || questionLower.Contains("deadline") || questionLower.Contains("timeline"))
        {
            prompt.AppendLine("Note: This appears to be a timing question. Highlight all relevant dates and deadlines.");
        }
        else if (questionLower.Contains("eligib") || questionLower.Contains("qualify") || questionLower.Contains("criteria"))
        {
            prompt.AppendLine("Note: This appears to be an eligibility question. List all requirements clearly.");
        }
        else if (conversationHistory.Count > 0 && (questionLower.StartsWith("what about") || questionLower.Contains("also") || questionLower.Contains("additionally")))
        {
            prompt.AppendLine("Note: This appears to be a follow-up question. Reference the previous conversation context.");
        }

        prompt.AppendLine();
        prompt.AppendLine($"**User Question:** {question}");
        prompt.AppendLine();
        prompt.AppendLine("**Your Response:**");

        return prompt.ToString();
    }

    // Helper method to detect history clearing requests
    public bool IsHistoryClearRequest(string question)
    {
        var clearKeywords = new[]
        {
        "clear",
        "delete",
        "history",
        "reset",
        "start over",
        "new conversation",
        "clear history",
        "delete history",
        "clear conversation",
        "reset chat"
    };

        var questionLower = question.ToLower().Trim();

        // Check for direct clear commands
        if (clearKeywords.Any(keyword => questionLower.Contains(keyword)))
        {
            // Additional validation to ensure it's really a clear request
            var clearPhrases = new[]
            {
            "clear history",
            "delete history",
            "clear conversation",
            "clear chat",
            "reset conversation",
            "start over",
            "new conversation",
            "clear all",
            "delete all"
        };

            return clearPhrases.Any(phrase => questionLower.Contains(phrase)) ||
                   (questionLower.Contains("clear") && questionLower.Contains("history")) ||
                   (questionLower.Contains("delete") && questionLower.Contains("history")) ||
                   questionLower == "clear" ||
                   questionLower == "reset";
        }

        return false;
    }

    // You would also need a method to actually clear the conversation history
    

        public bool ShouldClearHistory(string question)
        {
            return IsHistoryClearRequest(question);
        }
    

    private async Task LoadFromCache(string model, string file)
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var cacheData = JsonSerializer.Deserialize<CacheData>(json);

            foreach (var embedding in cacheData.Embeddings)
            {
                _cachedEmbeddings.Add(new EmbeddingData(
                    embedding.Text,
                    embedding.Vector.ToList(),
                    embedding.SourceFile,
                    embedding.LastModified));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cache load failed: {ex.Message}");
        }
    }

    private async Task SaveToCache()
    {
        var cacheData = new CacheData
        {
            GeneratedAt = DateTime.UtcNow,
            Embeddings = _cachedEmbeddings.Select(e => new EmbeddingEntry
            {
                Text = e.Text,
                Vector = e.Vector.ToArray(),
                SourceFile = e.SourceFile,
                LastModified = e.LastModified
            }).ToArray()
        };

        var options = new JsonSerializerOptions { WriteIndented = false };
        await File.WriteAllTextAsync(CACHE_FILE, JsonSerializer.Serialize(cacheData, options));
    }
}