using DocumentFormat.OpenXml.Office2010.Excel;
using MEAI_GPT_API.Models;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using static MEAI_GPT_API.Models.Conversation;

public class RagService : IRAGService
{
    private readonly ILogger<RagService> _logger;
    private readonly ChromaDbOptions _options;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _chromaClient;
    private readonly IDocumentProcessor _documentProcessor;
    private readonly ICacheManager _cacheManager;

    private string? _collectionId;
    private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly IMetricsCollector _metrics;
    private const string EMBEDDINGS_VERSION_KEY = "embeddings_version";
    private Boolean _isInitialized = false;
    private readonly object _lockObject = new object();
    private readonly ConcurrentBag<CorrectionEntry> _correctionsCache = new();
    private readonly string _currentUser = "mohdhamzakhan";
    private readonly DateTime _currentUtc = DateTime.Parse("2025-07-25 08:21:53");
    private int _nextCorrectionId = 1;

    public RagService(
        ILogger<RagService> logger,
        IOptions<ChromaDbOptions> options,
        IHttpClientFactory httpClientFactory,
        IDocumentProcessor documentProcessor,
        ICacheManager cacheManager,
        IMetricsCollector metrics)
    {
        _logger = logger;
        _options = options.Value;
        _httpClient = httpClientFactory.CreateClient("OllamaAPI");
        _chromaClient = httpClientFactory.CreateClient("ChromaDB");
        _documentProcessor = documentProcessor;
        _cacheManager = cacheManager;
        _metrics = metrics;
        InitializeSessionCleanup(); // Add this line

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // Wait 5 seconds for service to be ready
            try
            {
                var analysis = await AnalyzeChunkSizes();
                _logger.LogInformation($"Chunk Analysis: {JsonSerializer.Serialize(analysis)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze chunks during startup");
            }
        });
    }

    //public async Task InitializeAsync()
    //{
    //    try
    //    {
    //        await _initializationLock.WaitAsync();
    //        _logger.LogInformation("Initializing RAG system with ChromaDB...");

    //        await EnsureChromaDBHealthyAsync();
    //        _collectionId = await InitializeCollectionAsync();

    //        var embeddingModel = await EnsureEmbeddingModelAvailableAsync();
    //        await LoadOrGenerateEmbeddings(embeddingModel);

    //        _logger.LogInformation("RAG system initialization completed successfully");
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Failed to initialize RAG system");
    //        throw new RAGServiceException("Failed to initialize RAG system", ex);
    //    }
    //    finally
    //    {
    //        _initializationLock.Release();
    //    }
    //}
    private void EnsureAbbreviationContext()
    {
        var abbreviationsPath = Path.Combine(_options.ContextFolder, "abbreviations.txt");

        if (!File.Exists(abbreviationsPath))
        {
            var abbreviationContent = @"HR Policy Abbreviations:

CL = Casual Leave
SL = Sick Leave  
COFF = Compensatory Off
EL = Earned Leave
PL = Privilege Leave
ML = Maternity Leave
PL = Paternity Leave

These abbreviations are standard across all MEAI HR policies and should be interpreted consistently.";

            Directory.CreateDirectory(_options.ContextFolder);
            File.WriteAllText(abbreviationsPath, abbreviationContent);

            _logger.LogInformation("Created abbreviations context file");
        }
    }
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        try
        {
            EnsureAbbreviationContext();
            var currentVersion = await GetEmbeddingsVersionAsync();
            var cachedVersion = await _cacheManager.GetAsync<string>(EMBEDDINGS_VERSION_KEY);

            if (cachedVersion == currentVersion)
            {
                _logger.LogInformation("Using cached embeddings version: {Version}", currentVersion);
                _isInitialized = true;
                return;
            }

            await RefreshEmbeddingsAsync();
            await _cacheManager.SetAsync(EMBEDDINGS_VERSION_KEY, currentVersion, TimeSpan.FromDays(30));
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RAG service");
            throw;
        }
    }



    private async Task<string> GetEmbeddingsVersionAsync()
    {
        var policyFiles = new List<string>();
        foreach (var extension in _options.SupportedExtensions)
        {
            policyFiles.AddRange(Directory.GetFiles(_options.PolicyFolder, $"*{extension}", SearchOption.AllDirectories));
        }

        // Add context files
        if (Directory.Exists(_options.ContextFolder))
        {
            policyFiles.AddRange(Directory.GetFiles(_options.ContextFolder, "*.txt", SearchOption.AllDirectories));
        }

        var fileInfos = policyFiles.Select(f => new FileInfo(f));
        var latestUpdate = fileInfos.Max(f => f.LastWriteTime);
        var filesHash = string.Join("|", fileInfos.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTime.Ticks}"));

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(filesHash));
    }


    private async Task<string> InitializeCollectionAsync()
    {
        try
        {
            var collectionName = _options.Collections["policies"];
            var collectionData = new
            {
                name = collectionName,
                metadata = new { description = "MEAI HR Policy documents" },
                dimension = 4096,
                configuration = new
                {
                    hnsw = new
                    {
                        space = "cosine", // ✅ THIS is what Chroma reads
                        ef_construction = 100,
                        ef_search = 100,
                        max_neighbors = 16,
                        resize_factor = 1.2,
                        sync_threshold = 1000
                    }
                }
            };




            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections",
                collectionData);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ChromaCollection>();
                return result?.Id ?? throw new ChromaDBException("Failed to get collection ID");
            }

            // Collection might exist, try to get ID
            return await GetCollectionIdByNameAsync(collectionName)
                ?? throw new ChromaDBException("Failed to create or get collection");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize collection");
            throw new ChromaDBException("Failed to initialize collection", ex);
        }
    }

    public async Task<bool> IsHealthy()
    {
        try
        {
            var chromaHealth = await _chromaClient.GetAsync("cc");
            var ollamaHealth = await _httpClient.GetAsync("/api/tags");

            return chromaHealth.IsSuccessStatusCode && ollamaHealth.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return false;
        }
    }

    private async Task<string?> GetCollectionIdByNameAsync(string collectionName)
    {
        try
        {
            var response = await _chromaClient.GetAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections");
            response.EnsureSuccessStatusCode();

            var collections = await response.Content.ReadFromJsonAsync<List<ChromaCollection>>();
            return collections?
                .FirstOrDefault(c => c.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection ID for {CollectionName}", collectionName);
            throw new ChromaDBException($"Failed to get collection ID for {collectionName}", ex);
        }
    }

    private async Task EnsureChromaDBHealthyAsync()
    {
        try
        {
            var response = await _chromaClient.GetAsync("/api/v2/heartbeat");
            if (!response.IsSuccessStatusCode)
            {
                throw new ChromaDBException(
                    "ChromaDB is not healthy. Please ensure the service is running.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChromaDB health check failed");
            throw new ChromaDBException("Failed to verify ChromaDB health", ex);
        }
    }

    private async Task<string> EnsureEmbeddingModelAvailableAsync()
    {
        var preferredModels = new[]
        {
            "mistral:latest"
        };

        foreach (var model in preferredModels)
        {
            try
            {
                var response = await _httpClient.GetAsync("/api/tags");
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ModelTagsResponse>();
                    if (result?.Models.Any(m => m.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase)) == true)
                    {
                        return model;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check availability for model {Model}", model);
            }
        }

        throw new RAGServiceException("No suitable embedding model available");
    }

    private async Task LoadOrGenerateEmbeddings(string model)
    {
        try
        {
            var policyFiles = Directory.GetFiles(_options.PolicyFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => _options.SupportedExtensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            var abbrevation = Directory.GetFiles(_options.ContextFolder, "*.qa*", SearchOption.AllDirectories)
               .Where(f => _options.SupportedExtensions.Contains(
                   Path.GetExtension(f).ToLowerInvariant()))
               .ToList();

            _logger.LogInformation(
                "Processing {Count} policy files with model: {Model}",
                policyFiles.Count, model);

            var processedCount = 0;
            foreach (var filePath in policyFiles)
            {
                var fileInfo = new FileInfo(filePath);
                var lastModified = fileInfo.LastWriteTime;

                // Check if file is already processed
                if (await IsFileProcessedAsync(filePath, lastModified))
                {
                    _logger.LogDebug("Skipping {File} - already processed", Path.GetFileName(filePath));
                    continue;
                }

                await ProcessFileAsync(filePath, model, lastModified);
                processedCount++;

                _metrics.RecordEmbeddingOperation(
                    0, // Duration will be recorded in ProcessFileAsync
                    0, // Token count will be recorded in ProcessFileAsync
                    true);
            }

            foreach (var filePath in abbrevation)
            {
                var fileInfo = new FileInfo(filePath);
                var lastModified = fileInfo.LastWriteTime;

                // Check if file is already processed
                if (await IsFileProcessedAsync(filePath, lastModified))
                {
                    _logger.LogDebug("Skipping {File} - already processed", Path.GetFileName(filePath));
                    continue;
                }

                await ProcessFileAsync(filePath, model, lastModified);
                processedCount++;

                _metrics.RecordEmbeddingOperation(
                    0, // Duration will be recorded in ProcessFileAsync
                    0, // Token count will be recorded in ProcessFileAsync
                    true);
            }

            _logger.LogInformation(
                "Embedding processing complete. Processed {Count} new files",
                processedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load or generate embeddings");
            throw new RAGServiceException("Failed to process policy files", ex);
        }
    }

    public async Task<QueryResponse> ProcessQueryAsync(string question, 
        string plant, 
        string? generationModel = null, 
        string? embeddingModel = null, 
        int maxResults = 15, 
        bool meaiInfo = true, 
        string? sessionId = null, 
        bool useReRanking = true)

    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _collectionId = await InitializeCollectionAsync();
            if (string.IsNullOrEmpty(_collectionId))
            {
                throw new RAGServiceException("Service not properly initialized");
            }

            var context = GetOrCreateConversationContext(sessionId);
            _logger.LogInformation($"Processing query for session {context.SessionId}: {question}");

            if (IsHistoryClearRequest(question))
            {
                return await HandleHistoryClearRequest(context, sessionId);
            }

            // CHECK CORRECTIONS FIRST - BEFORE other processing
            var correction = await CheckCorrectionsAsync(question);
            if (correction != null)
            {
                _logger.LogInformation($"🎯 Using correction for question: {question}");

                // Update conversation history with correction
                UpdateConversationHistory(context, question, correction.Answer, new List<RelevantChunk>());

                stopwatch.Stop();
                return new QueryResponse
                {
                    Answer = correction.Answer,
                    IsFromCorrection = true, // Mark as correction
                    Sources = new List<string> { "User Correction" },
                    Confidence = 1.0,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    RelevantChunks = new List<RelevantChunk>(),
                    SessionId = context.SessionId
                };
            }

            // Continue with normal processing if no correction found
            embeddingModel ??= await EnsureEmbeddingModelAvailableAsync();

            if (IsTopicChanged(question, context.History))
            {
                _logger.LogInformation($"Topic changed for session {context.SessionId}, clearing context");
                ClearContext(context);
            }

            var contextualQuery = BuildContextualQuery(question, context.History);
            var relevantChunks = await GetRelevantChunksAsync(
                contextualQuery,
                embeddingModel,
                maxResults,
                meaiInfo,
                context,
                useReRanking,
                generationModel);

            var answer = await GenerateChatResponse(
                question,
                generationModel,
                context.History,
                relevantChunks,
                temperature: 0.2,
                ismeai: meaiInfo
            );

            UpdateConversationHistory(context, question, answer, relevantChunks);

            stopwatch.Stop();
            _metrics.RecordQueryProcessing(
                stopwatch.ElapsedMilliseconds,
                relevantChunks.Count,
                true);

            return new QueryResponse
            {
                Answer = answer,
                IsFromCorrection = false,
                Sources = relevantChunks.Select(c => c.Source).Distinct().ToList(),
                Confidence = relevantChunks.FirstOrDefault()?.Similarity ?? 0,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                RelevantChunks = relevantChunks.Take(5).ToList(),
                SessionId = context.SessionId
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Query processing failed for session {SessionId}", sessionId);
            _metrics.RecordQueryProcessing(stopwatch.ElapsedMilliseconds, 0, false);
            throw new RAGServiceException("Failed to process query", ex);
        }
    }


    private class ModelTagsResponse
    {
        public List<ModelInfo> Models { get; set; } = new();
    }

    private class ModelInfo
    {
        public string Name { get; set; } = string.Empty;
    }

    private async Task<bool> IsFileProcessedAsync(string filePath, DateTime lastModified)
    {
        var cacheKey = $"file_processed:{filePath}:{lastModified.Ticks}";
        return await _cacheManager.ExistsAsync(cacheKey);
    }

    private async Task ProcessFileAsync(string filePath, string model, DateTime lastModified)
    {
        var content = await _documentProcessor.ExtractTextAsync(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("No content extracted from file: {File}", filePath);
            return;
        }

        var chunks = ChunkText(content, filePath);
        _logger.LogInformation(
            "Processing {Count} chunks from {File}",
            chunks.Count,
            Path.GetFileName(filePath));

        await ProcessChunkBatch(chunks, model, filePath, lastModified);

        // Mark file as processed
        var cacheKey = $"file_processed:{filePath}:{lastModified.Ticks}";
        await _cacheManager.SetAsync(cacheKey, true, TimeSpan.FromDays(30));
    }

    private async Task<QueryResponse> HandleHistoryClearRequest(
     ConversationContext context,
     string? sessionId)
    {
        _logger.LogInformation($"Clearing history for session {sessionId}");

        ClearContext(context);

        // Remove from session contexts if session ID is provided
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (_sessionContexts.TryRemove(sessionId, out var removedContext))
            {
                _logger.LogInformation($"Removed session context for {sessionId}");
            }
        }

        return new QueryResponse
        {
            Answer = "✅ Conversation history cleared for this session. How can I assist you today?",
            IsFromCorrection = false,
            Sources = new List<string>(),
            Confidence = 1.0,
            ProcessingTimeMs = 0,
            RelevantChunks = new List<RelevantChunk>(),
            SessionId = sessionId
        };
    }

    private void CleanupExpiredSessions()
    {
        try
        {
            var expiredSessions = _sessionContexts
                .Where(kvp => DateTime.Now - kvp.Value.LastAccessed > TimeSpan.FromHours(2))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var sessionId in expiredSessions)
            {
                if (_sessionContexts.TryRemove(sessionId, out var context))
                {
                    _logger.LogInformation($"Cleaned up expired session: {sessionId}");
                }
            }

            _logger.LogInformation($"Cleaned up {expiredSessions.Count} expired sessions. Active sessions: {_sessionContexts.Count}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired sessions");
        }
    }

    private void InitializeSessionCleanup()
    {
        // Run cleanup every 30 minutes
        var timer = new System.Threading.Timer(
            callback: _ => CleanupExpiredSessions(),
            state: null,
            dueTime: TimeSpan.FromMinutes(30),
            period: TimeSpan.FromMinutes(30));

        _logger.LogInformation("Session cleanup timer initialized");
    }

    private void ClearContext(ConversationContext context)
    {
        context.History.Clear();
        context.RelevantChunks.Clear();
        context.LastAccessed = DateTime.Now;
    }

    private async Task<List<RelevantChunk>> GetRelevantChunksAsync(
    string query,
    string embeddingModel,
    int maxResults,
    bool meaiInfo,
    ConversationContext context,
    bool useReRanking,
    string generationModel)
    {
        if (!meaiInfo)
        {
            return new List<RelevantChunk>();
        }

        try
        {
            var correction = await CheckCorrectionsAsync(query);
            if (correction != null)
            {
                return new List<RelevantChunk>();
            }

            if (context.RelevantChunks.Count > 0 && IsFollowUpQuestion(query, context.History))
            {
                _logger.LogInformation($"Using cached chunks for follow-up question in session {context.SessionId}");
                return context.RelevantChunks.Select(e => new RelevantChunk
                {
                    Text = e.Text,
                    Source = e.SourceFile,
                    Similarity = e.Similarity
                }).ToList();
            }

            var chunks = await SearchChromaDBAsync(query, embeddingModel, maxResults);

            // DEBUG LOGGING: Log what chunks were found
            _logger.LogInformation($"Found {chunks.Count} chunks for query '{query}':");
            foreach (var chunk in chunks.Take(3)) // Log first 3 chunks
            {
                _logger.LogInformation($"  - {chunk.Source} (similarity: {chunk.Similarity:F3}): {chunk.Text.Substring(0, Math.Min(100, chunk.Text.Length))}...");
            }

            if (useReRanking && chunks.Count > maxResults)
            {
                chunks = await ReRankChunksAsync(query, chunks, maxResults, generationModel);
            }

            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get relevant chunks for session {SessionId}", context.SessionId);
            return new List<RelevantChunk>();
        }
    }

    private ConversationContext GetOrCreateConversationContext(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            // Generate a temporary session ID if none provided
            sessionId = $"temp_{Guid.NewGuid():N}";
            _logger.LogInformation($"Created temporary session ID: {sessionId}");
        }

        var context = _sessionContexts.GetOrAdd(sessionId, _ => {
            _logger.LogInformation($"Created new conversation context for session: {sessionId}");
            return new ConversationContext
            {
                SessionId = sessionId,
                CreatedAt = DateTime.Now,
                LastAccessed = DateTime.Now
            };
        });

        // Update last accessed time
        context.LastAccessed = DateTime.Now;
        context.SessionId = sessionId;

        _logger.LogInformation($"Retrieved context for session {sessionId} with {context.History.Count} turns");
        return context;
    }
    private bool IsHistoryClearRequest(string question)
    {
        var clearKeywords = new[]
        {
            "clear", "delete", "history", "reset", "start over",
            "new conversation", "clear history", "delete history"
        };

        var questionLower = question.ToLower().Trim();
        return clearKeywords.Any(keyword => questionLower.Contains(keyword));
    }
    private bool IsFollowUpQuestion(string question, List<ConversationTurn> history)
    {
        if (history.Count == 0)
        {
            _logger.LogDebug("No history available, not a follow-up question");
            return false;
        }

        var followUpIndicators = new[]
        {
        "what about", "and", "also", "additionally", "furthermore",
        "this", "that", "it", "they", "more details", "explain more",
        "how", "why", "when", "where", "can you tell me more",
        "elaborate", "details", "more info", "expand on"
    };

        var questionLower = question.ToLower().Trim();

        // Check for direct follow-up indicators
        var hasFollowUpIndicator = followUpIndicators.Any(indicator =>
            questionLower.StartsWith(indicator) ||
            questionLower.Contains($" {indicator} "));

        // Check for contextual references (pronouns, etc.)
        var hasContextualReference = new[] { "this", "that", "it", "they", "these", "those" }
            .Any(pronoun => questionLower.Contains(pronoun));

        // Check if question is related to the last topic
        var lastTurn = history.LastOrDefault();
        if (lastTurn != null)
        {
            var topicSimilarity = CalculateTextSimilarity(question, lastTurn.Question);
            var isRelatedTopic = topicSimilarity > 0.2;

            _logger.LogDebug($"Follow-up analysis: hasIndicator={hasFollowUpIndicator}, hasReference={hasContextualReference}, topicSimilarity={topicSimilarity:F3}");

            return hasFollowUpIndicator || hasContextualReference || isRelatedTopic;
        }

        return hasFollowUpIndicator || hasContextualReference;
    }

    private bool IsTopicChanged(string question, List<ConversationTurn> history) =>
       history.Count > 0 && CalculateTextSimilarity(question, history.Last().Question) < 0.3;

    private string BuildContextualQuery(string currentQuestion, List<ConversationTurn> history)
    {
        if (history.Count == 0) return currentQuestion;

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
    private async Task<string> GenerateChatResponse(
    string question,
    string model,
    List<ConversationTurn> history,
    List<RelevantChunk> chunks,
    double temperature = 0.1, // Lower temperature for more focused responses
    Boolean ismeai = true)
    {
        var messages = new List<object>();

        // Improved system prompt
        messages.Add(new
        {
            role = "system",
            content = ismeai ? @"You are MEAI HR Policy Assistant, an expert advisor with comprehensive knowledge of company policies.

🔑 CRITICAL ABBREVIATION DEFINITIONS (NEVER DEVIATE):
• CL = Casual Leave (NEVER 'Continuous Learning')
• SL = Sick Leave  
• COFF = Compensatory Off
• EL = Earned Leave
• PL = Privilege Leave  
• ML = Maternity Leave

📋 RESPONSE REQUIREMENTS:
1. Use ALL relevant information from provided policy excerpts
2. Structure answers with clear headings and bullet points
3. Include specific procedures, timelines, and requirements
4. Cite policy sources in [brackets] after each point
5. If information is partial, state what's available and suggest HR contact for details
6. Be comprehensive - don't withhold relevant information from the context
7. Use professional, helpful tone

⚠️ IMPORTANT: The policy excerpts provided contain official information. Use them fully to provide complete, accurate answers." :
            @"You are a helpful assistant. Use only the provided context to answer questions accurately and completely."
        });

        // Add recent conversation history (more context)
        foreach (var turn in history.TakeLast(8)) // Increased from 10 to 8 for balance
        {
            messages.Add(new { role = "user", content = turn.Question });
            messages.Add(new { role = "assistant", content = turn.Answer });
        }

        // Enhanced context building
        if (chunks.Any() && ismeai)
        {
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("=== OFFICIAL POLICY INFORMATION ===");
            contextBuilder.AppendLine();

            // Group chunks by source for better organization
            var groupedChunks = chunks
                .GroupBy(c => c.Source)
                .OrderByDescending(g => g.Max(c => c.Similarity))
                .Take(6); // Limit to top 6 sources

            foreach (var group in groupedChunks)
            {
                contextBuilder.AppendLine($"📄 {group.Key}:");
                foreach (var chunk in group.OrderByDescending(c => c.Similarity).Take(2))
                {
                    contextBuilder.AppendLine($"• {chunk.Text.Trim()}");
                    contextBuilder.AppendLine($"  (Relevance: {chunk.Similarity:F2})");
                    contextBuilder.AppendLine();
                }
            }

            messages.Add(new { role = "system", content = contextBuilder.ToString() });
        }

        // Add current question
        messages.Add(new { role = "user", content = question });

        var requestData = new
        {
            model,
            messages,
            temperature,
            stream = false,
            options = new
            {
                num_ctx = 8192, // Increased context window
                temperature,
                top_p = 0.9,
                repeat_penalty = 1.1,
                max_tokens = 2048, // Increased for longer responses
                stop = new[] { "\n\nUser:", "\n\nHuman:" } // Prevent response continuation
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/chat", requestData);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

            return CleanAndEnhanceResponse(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat generation failed");
            return "I apologize, but I'm having trouble generating a response right now. Please try again or contact HR directly for assistance.";
        }
    }

    private string CleanAndEnhanceResponse(string response)
    {
        // Remove thinking tags and clean up
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            response,
            @"<think>.*?</think>",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        // Clean up formatting
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n\s*\n\s*\n", "\n\n");
        cleaned = cleaned.Trim();

        // Ensure proper abbreviation usage in response
        cleaned = EnsureCorrectAbbreviations(cleaned);

        return cleaned;
    }

    private string EnsureCorrectAbbreviations(string text)
    {
        // Fix common abbreviation misinterpretations
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bContinuous Learning\b", "Casual Leave (CL)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return text;
    }


    private string CleanResponse(string response)
    {
        // Remove thinking tags if present (for models like qwen)
        var cleanedResponse = System.Text.RegularExpressions.Regex.Replace(
            response,
            @"<think>.*?</think>",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        // Clean up extra whitespace
        cleanedResponse = System.Text.RegularExpressions.Regex.Replace(cleanedResponse, @"\n\s*\n", "\n\n");

        return cleanedResponse.Trim();
    }
    private List<(string Text, string SourceFile)> ChunkText(string text, string sourceFile, int maxTokens = 200)
    {
        var chunks = new List<(string, string)>();

        // Split by sections first (headers, paragraphs)
        var sections = SplitIntoSections(text);

        foreach (var section in sections)
        {
            if (EstimateTokenCount(section) <= maxTokens)
            {
                // Small section, use as-is
                chunks.Add((section.Trim(), sourceFile));
            }
            else
            {
                // Large section, split by sentences
                var sentences = SplitIntoSentences(section);
                var currentChunk = new StringBuilder();
                int currentTokens = 0;

                foreach (var sentence in sentences)
                {
                    int sentenceTokens = EstimateTokenCount(sentence);

                    if (currentTokens + sentenceTokens > maxTokens && currentChunk.Length > 0)
                    {
                        chunks.Add((currentChunk.ToString().Trim(), sourceFile));
                        currentChunk.Clear();
                        currentTokens = 0;
                    }

                    currentChunk.AppendLine(sentence);
                    currentTokens += sentenceTokens;
                }

                if (currentChunk.Length > 0)
                {
                    chunks.Add((currentChunk.ToString().Trim(), sourceFile));
                }
            }
        }

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c.Item1)).ToList();
    }

    private List<string> SplitIntoSections(string text)
    {
        // Split by common section markers
        var sections = new List<string>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var currentSection = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Check if this is a section header
            if (IsLikelySectionHeader(trimmedLine) && currentSection.Length > 0)
            {
                sections.Add(currentSection.ToString());
                currentSection.Clear();
            }

            currentSection.AppendLine(trimmedLine);
        }

        if (currentSection.Length > 0)
        {
            sections.Add(currentSection.ToString());
        }

        return sections.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private bool IsLikelySectionHeader(string line)
    {
        // Detect section headers
        return line.Length < 100 &&
               (line.EndsWith(":") ||
                System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.") ||
                line.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)));
    }

    private List<string> SplitIntoSentences(string text)
    {
        // Better sentence splitting
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return sentences;
    }

    private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, string embeddingModel, int maxResults)
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();
            _logger.LogInformation($"Searching ChromaDB with enhanced query: {query}");

            // Enhance query with context
            var enhancedQuery = EnhanceQueryForSearch(query);
            var queryEmbedding = await GetEmbedding(enhancedQuery, embeddingModel);

            var searchData = new
            {
                query_embeddings = new List<List<float>> { queryEmbedding },
                n_results = Math.Max(maxResults * 2, 20), // Get more initial results
                include = new[] { "documents", "metadatas", "distances" }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/query",
                searchData);

            var responseContent = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;
            var relevantChunks = new List<RelevantChunk>();

            if (root.TryGetProperty("documents", out var documentsArray) &&
                documentsArray.GetArrayLength() > 0 &&
                documentsArray[0].GetArrayLength() > 0)
            {
                var documents = documentsArray[0].EnumerateArray().ToArray();
                var metadatas = root.GetProperty("metadatas")[0].EnumerateArray().ToArray();
                var distances = root.GetProperty("distances")[0].EnumerateArray().ToArray();

                for (int i = 0; i < documents.Length; i++)
                {
                    var similarity = 1.0 - distances[i].GetDouble();

                    // More intelligent threshold based on query type
                    var threshold = DetermineThreshold(query);
                    if (similarity < threshold)
                    {
                        continue;
                    }

                    var metadata = metadatas[i];
                    var sourceFile = metadata.TryGetProperty("source_file", out var sf)
                        ? Path.GetFileName(sf.GetString() ?? "")
                        : "Unknown";

                    var documentText = documents[i].GetString() ?? "";

                    // Boost relevance for exact matches and context files
                    similarity = BoostRelevanceScore(similarity, documentText, sourceFile, query);

                    relevantChunks.Add(new RelevantChunk
                    {
                        Text = documentText,
                        Source = sourceFile,
                        Similarity = similarity
                    });
                }
            }

            // Better sorting and filtering
            var finalChunks = relevantChunks
                .OrderByDescending(c => c.Similarity)
                .GroupBy(c => c.Text) // Remove duplicates
                .Select(g => g.First())
                .Take(maxResults)
                .ToList();

            _logger.LogInformation($"Retrieved {finalChunks.Count} unique chunks with avg similarity: {finalChunks.Average(c => c.Similarity):F3}");
            return finalChunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChromaDB search failed");
            return new List<RelevantChunk>();
        }
    }

    private string EnhanceQueryForSearch(string query)
    {
        // Expand abbreviations in query
        var enhanced = query;
        enhanced = enhanced.Replace("CL", "Casual Leave CL");
        enhanced = enhanced.Replace("SL", "Sick Leave SL");
        enhanced = enhanced.Replace("COFF", "Compensatory Off COFF");
        enhanced = enhanced.Replace("EL", "Earned Leave EL");
        enhanced = enhanced.Replace("PL", "Privilege Leave PL");
        enhanced = enhanced.Replace("ML", "Maternity Leave ML");
        enhanced = enhanced.Replace("HR", "Human Resources HR");
        enhanced = enhanced.Replace("Policy", "Company Policy");
        enhanced = enhanced.Replace("Procedure", "Company Procedure");
        enhanced = enhanced.Replace("A Shift", "First Shift");
        enhanced = enhanced.Replace("B Shift", "Second Shift");
        enhanced = enhanced.Replace("C Shift", "Third Shift");
        enhanced = enhanced.Replace("G Shift", "General Shift");

        return enhanced;
    }

    private double DetermineThreshold(string query)
    {
        // Lower threshold for specific policy questions
        if (query.ToLower().Contains("leave") || query.ToLower().Contains("policy") ||
            query.ToLower().Contains("process") || query.ToLower().Contains("procedure"))
        {
            return 0.1; // Very permissive for policy questions
        }

        return 0.2; // Standard threshold
    }

    private double BoostRelevanceScore(double similarity, string text, string sourceFile, string query)
    {
        // Boost for exact keyword matches
        var queryLower = query.ToLower();
        var textLower = text.ToLower();

        if (sourceFile.Contains("abbreviation") || sourceFile.Contains("context"))
        {
            similarity = Math.Min(0.98, similarity + 0.3); // High boost for context
        }

        // Boost for exact matches
        var queryWords = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matchCount = queryWords.Count(word => textLower.Contains(word));
        var matchRatio = (double)matchCount / queryWords.Length;

        if (matchRatio > 0.7) // Most words match
        {
            similarity = Math.Min(0.95, similarity + 0.2);
        }

        return similarity;
    }

    private async Task<List<RelevantChunk>> ReRankChunksAsync
        (
    string query,
    List<RelevantChunk> chunks,
    int maxResults,
    string generationModel)
    {
        try
        {
            var requestData = new
            {
                model = generationModel,
                prompt = $"Rank the following chunks for relevance to: \"{query}\"\n\n" +
                         string.Join("\n\n", chunks.Select((c, idx) => $"Chunk {idx + 1}: {c.Text}")),
                options = new
                {
                    temperature = 0.0,
                    max_tokens = 2048, // Increased from 1000 to 2048
                    num_ctx = 4096 // Ensure enough context for processing all chunks
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", requestData);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var text = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

            // For now, just return the top N chunks by existing similarity since parsing ranked results needs prompt engineering.
            return chunks.OrderByDescending(c => c.Similarity).Take(maxResults).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ReRankChunks failed: {ex.Message}");
            return chunks.OrderByDescending(c => c.Similarity).Take(maxResults).ToList();
        }
    }

    public async Task<Dictionary<string, object>> AnalyzeChunkSizes()
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/get",
                new { limit = 100 }); // Get sample of chunks

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("documents", out var docs))
                {
                    var documents = docs.EnumerateArray().ToArray();
                    var sizes = documents.Select(d => d.GetString()?.Length ?? 0).ToList();

                    return new Dictionary<string, object>
                {
                    { "total_chunks", documents.Length },
                    { "avg_chunk_size", sizes.Average() },
                    { "min_chunk_size", sizes.Min() },
                    { "max_chunk_size", sizes.Max() },
                    { "chunks_under_500_chars", sizes.Count(s => s < 500) },
                    { "chunks_500_to_1000", sizes.Count(s => s >= 500 && s < 1000) },
                    { "chunks_over_1000", sizes.Count(s => s >= 1000) }
                };
                }
            }

            return new Dictionary<string, object> { { "error", "Could not analyze chunks" } };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze chunk sizes");
            return new Dictionary<string, object> { { "error", ex.Message } };
        }
    }

    private async Task ProcessChunkBatch(
     List<(string Text, string SourceFile)> chunks,
     string model,
     string filePath,
     DateTime lastModified)
    {
        try
        {
            _logger.LogInformation($"[{_currentUser}] Processing chunk batch at {_currentUtc}");

            var embeddings = new List<List<float>>();
            var documents = new List<string>();
            var metadatas = new List<Dictionary<string, object>>();
            var ids = new List<string>();

            // ✅ PRIORITIZE context chunks - inject at the beginning
            var contextChunks = GetContextChunksFromFiles();

            // Add context chunks first with special metadata
            foreach (var ctx in contextChunks)
            {
                if (!chunks.Any(c => c.Text.Trim() == ctx.Text.Trim()))
                {
                    chunks.Insert(0, ctx);
                    _logger.LogInformation($"🚀 Injected context chunk from {ctx.SourceFile}");
                }
            }

            foreach (var (text, sourceFile) in chunks)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogWarning($"Skipping empty chunk from {sourceFile}");
                    continue;
                }

                try
                {
                    var cleanedText = CleanText(text);
                    var embedding = await GetEmbedding(cleanedText, model);
                    if (embedding.Count == 0)
                    {
                        _logger.LogWarning($"Empty embedding generated for chunk from {sourceFile}");
                        continue;
                    }

                    embeddings.Add(embedding);
                    documents.Add(cleanedText);

                    var chunkId = GenerateChunkId(filePath, cleanedText, lastModified);
                    ids.Add(chunkId);

                    // Enhanced metadata with context flags
                    var metadata = new Dictionary<string, object>
                {
                    { "source_file", sourceFile },
                    { "last_modified", lastModified.ToString("O") },
                    { "model", model },
                    { "chunk_size", cleanedText.Length },
                    { "processed_at", _currentUtc.ToString("O") },
                    { "processed_by", _currentUser }
                };

                    // Mark context/abbreviation chunks for priority boosting
                    if (sourceFile.Contains("abbreviation") || sourceFile.Contains("context") ||
                        cleanedText.ToUpper().Contains("CL =") || cleanedText.ToUpper().Contains("CASUAL LEAVE"))
                    {
                        metadata["is_context"] = true;
                        metadata["priority"] = "high";
                        _logger.LogInformation($"🎯 Marked {sourceFile} as high-priority context chunk");
                    }

                    metadatas.Add(metadata);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to process chunk from {sourceFile}");
                }
            }

            if (embeddings.Any())
            {
                var success = await AddToChromaDBAsync(ids, embeddings, documents, metadatas);
                if (success)
                {
                    _logger.LogInformation($"Successfully added {embeddings.Count} chunks to ChromaDB");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process chunk batch");
            throw;
        }
    }

    private List<(string Text, string SourceFile)> GetContextChunksFromFiles()
    {
        var chunks = new List<(string, string)>();

        if (!Directory.Exists(_options.ContextFolder))
        {
            _logger.LogWarning($"Context folder not found: {_options.ContextFolder}");
            return chunks;
        }

        var files = Directory.GetFiles(_options.ContextFolder, "*.txt", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file).Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                var filename = Path.GetFileName(file);
                chunks.Add((content, filename));
            }
        }

        return chunks;
    }


    private string CleanText(string text)
    {
        // Remove extra whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        // Remove special characters but keep basic punctuation
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\w\s.,!?-]", "");

        // Normalize line endings
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        return text.Trim();
    }

    private string GenerateChunkId(string filePath, string text, DateTime lastModified)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var textHash = text.GetHashCode().ToString("X");
        var timeStamp = lastModified.Ticks.ToString();
        return $"{fileName}_{textHash}_{timeStamp}";
    }
    private int EstimateTokenCount(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? 0 : text.Length / 4; // Approximate: 1 token ~ 4 chars
    }
    private double CalculateTextSimilarity(string text1, string text2)
    {
        var words1 = ExtractKeywords(text1).Split(' ').ToHashSet();
        var words2 = ExtractKeywords(text2).Split(' ').ToHashSet();
        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private async Task<List<float>> GetEmbedding(string text, string model, int maxRetries = 3)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty");

        // Better text preprocessing
        text = PreprocessTextForEmbedding(text);

        // Truncate if too long (keep more text)
        if (text.Length > 8000) // Increased from 12000 to better limit
            text = text.Substring(0, 8000);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var request = new
                {
                    model = "mistral:latest", // Or use a dedicated embedding model like "nomic-embed-text"
                    prompt = text,
                    options = new
                    {
                        num_ctx = 2048, // Increased context
                        temperature = 0.0,
                        num_predict = -1 // Let model decide
                    }
                };

                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                {
                    var embedding = embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToList();
                    if (embedding.Count > 0)
                    {
                        return embedding;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Embedding attempt {attempt + 1} failed: {ex.Message}");
                if (attempt == maxRetries - 1)
                    throw;
                await Task.Delay(2000 * (attempt + 1)); // Longer delay
            }
        }

        throw new Exception("Failed to generate embedding after all retries");
    }

    private string PreprocessTextForEmbedding(string text)
    {
        // Clean and normalize text for better embeddings
        text = text.Trim();

        // Remove excessive whitespace but preserve structure
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        // Preserve important abbreviations
        text = text.Replace("CL ", "Casual Leave ");
        text = text.Replace("SL ", "Sick Leave ");
        text = text.Replace("COFF ", "Compensatory Off ");
        text = text.Replace("EL ", "Earned Leave ");
        text = text.Replace("PL ", "Privilege Leave ");
        text = text.Replace("ML ", "Maternity Leave ");

        return text;
    }

    private string ExtractKeywords(string text)
    {
        var stopWords = new HashSet<string>
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
            "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
            "will", "would", "could", "should", "may", "might", "can", "what", "how", "when", "where", "why"
        };

        var words = text.ToLower()
            .Split(new char[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 2 && !stopWords.Contains(word))
            .Distinct()
            .OrderBy(word => word);

        return string.Join(" ", words);
    }

    public async Task<bool> AddToChromaDBAsync(
    List<string> ids,
    List<List<float>> embeddings,
    List<string> documents,
    List<Dictionary<string, object>> metadatas)
    {
        if (ids == null || embeddings == null || documents == null || metadatas == null)
            throw new ArgumentNullException("Input lists cannot be null");
        if (ids.Count != embeddings.Count || ids.Count != documents.Count || ids.Count != metadatas.Count)
            throw new ArgumentException("All lists must be of equal length");

        try
        {
            var addData = new
            {
                ids,
                embeddings,
                documents,
                metadatas
            };
            _collectionId = await InitializeCollectionAsync();
            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/add",
                addData);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                // Replace with your logging solution
                Console.WriteLine($"ChromaDB add failed: {error}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Replace with your logging solution
            Console.WriteLine($"Failed to add to ChromaDB: {ex.Message}");
            return false;
        }
    }
    private async Task<CorrectionEntry?> CheckCorrectionsAsync(string question)
    {
        try
        {
            // First check local cache with more flexible matching
            lock (_lockObject)
            {
                var cachedCorrection = _correctionsCache
                    .Where(c =>
                        c.Question.Equals(question, StringComparison.OrdinalIgnoreCase) ||
                        CalculateTextSimilarity(c.Question, question) > 0.85)
                    .OrderByDescending(c => c.Date)
                    .FirstOrDefault();

                if (cachedCorrection != null)
                {
                    _logger.LogInformation($"✅ Found correction in cache for: {question}");
                    _metrics.RecordCacheOperation("correction_hit", 0, true);
                    return cachedCorrection;
                }
            }

            // If not in cache, search ChromaDB
            _collectionId = await InitializeCollectionAsync();
            if (string.IsNullOrEmpty(_collectionId))
            {
                return null;
            }

            // Search using embedding similarity for corrections
            var embeddingModel = GetBestEmbeddingModel();
            var queryEmbedding = await GetEmbedding(question, embeddingModel);

            var searchData = new
            {
                query_embeddings = new List<List<float>> { queryEmbedding.ToList() },
                n_results = 10,
                include = new[] { "documents", "metadatas", "distances" },
                where = new Dictionary<string, object>
            {
                { "type", "correction" }
            }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/query",
                searchData);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to search corrections: {StatusCode}, Content: {Content}",
                    response.StatusCode, errorContent);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation($"🔍 ChromaDB correction search response: {responseContent}");

            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("documents", out var documentsArray) &&
                documentsArray.GetArrayLength() > 0 &&
                documentsArray[0].GetArrayLength() > 0)
            {
                var documents = documentsArray[0].EnumerateArray().ToArray();
                var metadatas = root.GetProperty("metadatas")[0].EnumerateArray().ToArray();
                var distances = root.GetProperty("distances")[0].EnumerateArray().ToArray();

                // Find the best matching correction with LOWER threshold
                for (int i = 0; i < documents.Length; i++)
                {
                    var distance = distances[i].GetDouble();
                    var similarity = 1.0 - distance;

                    // LOWERED threshold from 0.9 to 0.7 for better correction matching
                    if (similarity > 0.7)
                    {
                        var metadata = metadatas[i];
                        var originalQuestion = metadata.TryGetProperty("question", out var q) ? q.GetString() : ""; // ✅ MOVED INSIDE THE LOOP
                        var correctionId = metadata.TryGetProperty("id", out var idProp) ? idProp.GetString() : Guid.NewGuid().ToString();

                        var correction = new CorrectionEntry
                        {
                            Id = correctionId!,
                            Question = originalQuestion ?? question,
                            Answer = documents[i].GetString() ?? "",
                            Date = DateTime.Now
                        };

                        // Cache the correction for future use
                        lock (_lockObject)
                        {
                            // Check if already cached to avoid duplicates
                            if (!_correctionsCache.Any(c => c.Id == correction.Id))
                            {
                                _correctionsCache.Add(correction);
                            }
                        }

                        _logger.LogInformation($"✅ Found correction with similarity {similarity:F3} for: {question}");
                        _metrics.RecordCacheOperation("correction_store", 0, true);
                        return correction;
                    }
                    else
                    {
                        // ✅ FIXED: Declare originalQuestion here as well for logging
                        var metadata = metadatas[i];
                        var originalQuestion = metadata.TryGetProperty("question", out var q) ? q.GetString() : "";
                        _logger.LogInformation($"❌ Correction similarity too low: {similarity:F3} for question: {originalQuestion}");
                    }
                }
            }
            else
            {
                _logger.LogInformation($"📭 No correction documents found for: {question}");
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking corrections for question: {Question}", question);
            return null;
        }
    }


    public async Task<List<CorrectionEntry>> DebugGetAllCorrections()
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();

            var searchData = new
            {
                query_embeddings = new List<List<float>>(), // Empty query to get all
                n_results = 1000, // Get many results
                include = new[] { "documents", "metadatas", "distances" },
                where = new Dictionary<string, object>
            {
                { "type", "correction" }
            }
            };

            var getAllData = new
            {
                limit = 1000,
                where = new Dictionary<string, object>
            {
                { "type", "correction" }
            },
                include = new[] { "documents", "metadatas" }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/get",
                getAllData);

            var corrections = new List<CorrectionEntry>();

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Debug corrections response: {responseContent}");

                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;

                if (root.TryGetProperty("documents", out var docs) &&
                    root.TryGetProperty("metadatas", out var metas))
                {
                    var documents = docs.EnumerateArray().ToArray();
                    var metadatas = metas.EnumerateArray().ToArray();

                    for (int i = 0; i < documents.Length; i++)
                    {
                        var metadata = metadatas[i];
                        corrections.Add(new CorrectionEntry
                        {
                            Id = metadata.TryGetProperty("id", out var id) ? id.GetString() : "",
                            Question = metadata.TryGetProperty("question", out var q) ? q.GetString() : "",
                            Answer = documents[i].GetString() ?? "",
                            Date = DateTime.Now
                        });
                    }
                }
            }

            _logger.LogInformation($"Found {corrections.Count} corrections in database");
            return corrections;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to debug get all corrections");
            return new List<CorrectionEntry>();
        }
    }


    private void UpdateConversationHistory(
    ConversationContext context,
    string question,
    string answer,
    List<RelevantChunk> relevantChunks)
    {
        try
        {
            var turn = new ConversationTurn
            {
                Question = question,
                Answer = answer,
                Timestamp = DateTime.Now,
                Sources = relevantChunks.Select(c => c.Source).Distinct().ToList()
            };

            context.History.Add(turn);

            // Keep only last 10 turns to manage memory (increased from 5)
            if (context.History.Count > 10)
            {
                context.History = context.History.TakeLast(10).ToList();
            }

            // Update relevant chunks for context
            context.RelevantChunks = relevantChunks.Select(r => new EmbeddingData(
                Text: r.Text,
                Vector: new List<float>(), // We don't need to store vectors in memory
                SourceFile: r.Source,
                LastModified: DateTime.Now,
                model: "llama3.2:1b")
            {
                Similarity = r.Similarity
            }).ToList();

            context.LastAccessed = DateTime.Now;

            // Log conversation turn for analytics
            _logger.LogInformation(
                "Session {SessionId}: Turn recorded. Question length: {QuestionLength}, Answer length: {AnswerLength}, Sources: {SourceCount}, Total turns: {TotalTurns}",
                context.SessionId,
                question.Length,
                answer.Length,
                turn.Sources.Count,
                context.History.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update conversation history for session {SessionId}", context.SessionId);
            // Don't throw - this is not critical for the response
        }
    }

    private class ChromaGetResponse
    {
        public List<string>? Documents { get; set; }
        public List<Dictionary<string, object>>? Metadatas { get; set; }
    }

    public async Task RefreshEmbeddingsAsync(string model = "mistral:latest")
    {
        try
        {
            _logger.LogInformation($"Starting embeddings refresh {DateTime.Now}");

            // **BETTER APPROACH: Delete only policy documents, preserve corrections**
            await DeletePolicyDocumentsAsync();

            // Ensure collection exists
            _collectionId = await InitializeCollectionAsync();

            // Load and process policy files
            await LoadOrGenerateEmbeddings(model);

            _logger.LogInformation("Embeddings refresh completed successfully - corrections preserved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh embeddings");
            throw;
        }
    }

    private async Task DeletePolicyDocumentsAsync()
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();

            // Delete only documents that are NOT corrections
            var deleteData = new
            {
                where = new Dictionary<string, object>
            {
                { "$ne", new Dictionary<string, object> { { "type", "correction" } } }
            }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/delete",
                deleteData);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully deleted policy documents while preserving corrections");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to selectively delete policy documents: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete policy documents");
            throw;
        }
    }



    private async Task DeleteCollectionIfExistsAsync(string collectionName)
    {
        try
        {
            _logger.LogInformation($"[{_currentUser}] Attempting to delete collection at {_currentUtc}");

            // First get the collection ID
            var collectionId = await GetCollectionIdByNameAsync(collectionName);
            if (string.IsNullOrEmpty(collectionId))
            {
                _logger.LogInformation($"Collection {collectionName} not found, nothing to delete");
                return;
            }

            // Prepare delete request with proper structure
            var deleteRequest = new
            {
            };

            // Make POST request to delete endpoint
            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant} /databases/ {_options.Database}/collections/{collectionId}/delete",
                deleteRequest);

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation($"Delete response: {responseContent}");

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Successfully deleted collection content: {collectionName}");

                // Now delete the collection itself
                response = await _chromaClient.DeleteAsync(
                    $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{collectionName}");

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Successfully deleted collection: {collectionName}");
                }
                else
                {
                    responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to delete collection structure. Status: {response.StatusCode}, Content: {responseContent}");
                }
            }
            else
            {
                _logger.LogError($"Failed to delete collection content. Status: {response.StatusCode}, Content: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete collection {collectionName}");
            throw;
        }
    }



    public async Task SaveCorrectionAsync(string question, string correctAnswer, string model)
    {
        try
        {
            _logger.LogInformation($"💾 Saving correction at {DateTime.Now} for question: {question}");

            // Ensure collection is initialized
            _collectionId = await InitializeCollectionAsync();
            if (string.IsNullOrEmpty(_collectionId))
            {
                throw new RAGServiceException("Collection not initialized");
            }

            var correctionId = Guid.NewGuid().ToString();
            var correction = new CorrectionEntry
            {
                Id = correctionId,
                Question = question,
                Answer = correctAnswer,
                Date = DateTime.Now
            };

            // Add to ChromaDB with proper metadata structure
            var embeddingModel = GetBestEmbeddingModel();
            var embedding = await GetEmbedding(question, embeddingModel);

            var addData = new
            {
                ids = new[] { correctionId },
                embeddings = new[] { embedding },
                documents = new[] { correctAnswer },
                metadatas = new[]
                {
                new Dictionary<string, object>
                {
                    { "id", correctionId }, // Add ID to metadata
                    { "question", question },
                    { "model", model },
                    { "timestamp", DateTime.Now.ToString("O") },
                    { "user", _currentUser },
                    { "type", "correction" }, // CRITICAL: Mark as correction type
                    { "source_file", "user_correction" }
                }
            }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/add",
                addData);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ Failed to save correction to ChromaDB: {errorContent}");
                throw new Exception($"Failed to save correction: {errorContent}");
            }

            // Also add to local cache for immediate availability
            lock (_lockObject)
            {
                // Remove any existing correction for the same question
                var existingCorrections = _correctionsCache
                    .Where(c => c.Question.Equals(question, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var existing in existingCorrections)
                {
                    ((ICollection<CorrectionEntry>)_correctionsCache).Remove(existing);
                }

                _correctionsCache.Add(correction);

                // Keep cache size manageable (last 100 corrections)
                while (_correctionsCache.Count > 100)
                {
                    var oldest = _correctionsCache.OrderBy(c => c.Date).FirstOrDefault();
                    if (oldest != null)
                    {
                        ((ICollection<CorrectionEntry>)_correctionsCache).Remove(oldest);
                    }
                }
            }

            _logger.LogInformation($"✅ Successfully saved correction for question: {question}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to save correction for question: {Question}", question);
            throw;
        }
    }


    public SystemStatus GetSystemStatus()
    {
        try
        {
            _logger.LogInformation($"Getting system status at {DateTime.Now}");

            var isHealthy = IsSystemHealthy();
            var totalEmbeddings = GetCollectionCountAsync().Result;
            var totalCorrections = GetCorrectionsCountAsync().Result;

            return new SystemStatus
            {
                TotalEmbeddings = totalEmbeddings,
                TotalCorrections = totalCorrections,
                LastUpdated = DateTime.Parse("2025-07-25 08:16:41"),
                IsHealthy = isHealthy,
                PoliciesFolder = _options.PolicyFolder,
                SupportedExtensions = _options.SupportedExtensions.ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get system status");
            throw;
        }
    }

    private bool IsSystemHealthy()
    {
        try
        {
            // Check ChromaDB health
            var chromaHealth = _chromaClient.GetAsync("/api/v2/heartbeat").Result;
            if (!chromaHealth.IsSuccessStatusCode)
                return false;

            // Check Ollama health
            var ollamaHealth = _httpClient.GetAsync("/api/tags").Result;
            if (!ollamaHealth.IsSuccessStatusCode)
                return false;

            // Check if policies folder exists
            if (!Directory.Exists(_options.PolicyFolder))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> GetCorrectionsCountAsync()
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();
            var response = await _chromaClient.GetAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/count");

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(result);
                return doc.RootElement.GetInt32();
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get corrections count");
            return 0;
        }
    }

    public List<CorrectionEntry> GetRecentCorrections(int limit = 50)
    {
        try
        {
            _logger.LogInformation($"Retrieving {limit} recent corrections at {DateTime.Now}");

            lock (_lockObject)
            {
                return _correctionsCache
                    .OrderByDescending(c => c.Date)
                    .Take(limit)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent corrections");
            throw;
        }
    }

    public async Task<bool> DeleteCorrectionAsync(string id)
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();
            _logger.LogInformation($"Deleting correction {id} at {DateTime.Now}");

            var deleteData = new
            {
                ids = new[] { id }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant} /databases/ {_options.Database} /collections/ {_collectionId}/delete",
                deleteData);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to delete correction: {await response.Content.ReadAsStringAsync()}");
                return false;
            }

            // Remove from cache
            lock (_lockObject)
            {
                var correction = _correctionsCache.FirstOrDefault(c => c.Id.ToString() == id);
                if (correction != null)
                {
                    return ((ICollection<CorrectionEntry>)_correctionsCache).Remove(correction);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete correction {Id}", id);
            throw;
        }
    }

    public async Task ProcessUploadedPolicyAsync(Stream fileStream, string fileName, string model)
    {
        try
        {
            _logger.LogInformation($"Processing uploaded policy {fileName} at {DateTime.Now}");

            // Read file content
            using var reader = new StreamReader(fileStream);
            var content = await reader.ReadToEndAsync();

            // Chunk the document
            var chunks = ChunkText(content, fileName);

            // Process chunks in batches
            var batchSize = 5;
            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                await ProcessChunkBatch(batch, model, fileName, DateTime.UtcNow);
            }

            _logger.LogInformation($"Successfully processed policy file {fileName} with {chunks.Count} chunks");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process uploaded policy {FileName}", fileName);
            throw;
        }
    }
    private async Task<int> GetCollectionCountAsync()
    {
        try
        {
            _collectionId = await InitializeCollectionAsync();
            var response = await _chromaClient.GetAsync($"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/count");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(result);
                return doc.RootElement.GetInt32();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get collection count: {ex.Message}");
            return 0;
        }
    }
    private string GetBestEmbeddingModel()
    {
        var preferredModels = new[]
        {
            "mistral:latest"
        };

        foreach (var model in preferredModels)
        {
            if (IsModelAvailable(model).Result)
            {
                return model;
            }
        }

        return "mistral:latest"; // Default fallback
    }
    private async Task<bool> IsModelAvailable(string modelName)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    return modelsArray.EnumerateArray()
                        .Any(model => model.TryGetProperty("name", out var nameProperty) &&
                                    nameProperty.GetString()?.StartsWith(modelName, StringComparison.OrdinalIgnoreCase) == true);
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking model availability: {ex.Message}");
            return false;
        }
    }
    private async Task<List<CorrectionEntry>> BackupCorrectionsAsync()
    {
        try
        {
            var corrections = new List<CorrectionEntry>();

            // Get all corrections from ChromaDB
            _collectionId = await InitializeCollectionAsync();

            var getAllData = new
            {
                limit = 10000, // Get all corrections
                where = new Dictionary<string, object>
            {
                { "type", "correction" }
            },
                include = new[] { "documents", "metadatas" }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/get",
                getAllData);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;

                if (root.TryGetProperty("documents", out var docs) &&
                    root.TryGetProperty("metadatas", out var metas))
                {
                    var documents = docs.EnumerateArray().ToArray();
                    var metadatas = metas.EnumerateArray().ToArray();

                    for (int i = 0; i < documents.Length; i++)
                    {
                        var metadata = metadatas[i];
                        var correction = new CorrectionEntry
                        {
                            Id = metadata.TryGetProperty("id", out var id) ? id.GetString() : Guid.NewGuid().ToString(),
                            Question = metadata.TryGetProperty("question", out var q) ? q.GetString() : "",
                            Answer = documents[i].GetString() ?? "",
                            Date = metadata.TryGetProperty("timestamp", out var ts) && DateTime.TryParse(ts.GetString(), out var date)
                                   ? date : DateTime.Now
                        };

                        corrections.Add(correction);
                    }
                }
            }

            _logger.LogInformation($"Backed up {corrections.Count} corrections from ChromaDB");
            return corrections;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup corrections");
            return new List<CorrectionEntry>();
        }
    }

    private async Task RestoreCorrectionsAsync(List<CorrectionEntry> corrections, string model)
    {
        try
        {
            if (!corrections.Any())
            {
                _logger.LogInformation("No corrections to restore");
                return;
            }

            _logger.LogInformation($"Restoring {corrections.Count} corrections to ChromaDB");

            var embeddingModel = GetBestEmbeddingModel();
            var batchSize = 10; // Process in smaller batches

            for (int i = 0; i < corrections.Count; i += batchSize)
            {
                var batch = corrections.Skip(i).Take(batchSize).ToList();
                await RestoreCorrectionBatch(batch, embeddingModel);
                _logger.LogInformation($"Restored batch {i / batchSize + 1} of {(corrections.Count + batchSize - 1) / batchSize}");
            }

            // Also restore to local cache
            lock (_lockObject)
            {
                _correctionsCache.Clear();
                foreach (var correction in corrections)
                {
                    _correctionsCache.Add(correction);
                }
            }

            _logger.LogInformation("Successfully restored all corrections");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore corrections");
            throw;
        }
    }

    private async Task RestoreCorrectionBatch(List<CorrectionEntry> corrections, string embeddingModel)
    {
        try
        {
            var ids = new List<string>();
            var embeddings = new List<List<float>>();
            var documents = new List<string>();
            var metadatas = new List<Dictionary<string, object>>();

            foreach (var correction in corrections)
            {
                // Generate embedding for the question
                var embedding = await GetEmbedding(correction.Question, embeddingModel);

                ids.Add(correction.Id);
                embeddings.Add(embedding);
                documents.Add(correction.Answer);

                metadatas.Add(new Dictionary<string, object>
            {
                { "id", correction.Id },
                { "question", correction.Question },
                { "model", embeddingModel },
                { "timestamp", correction.Date.ToString("O") },
                { "user", _currentUser },
                { "type", "correction" }, // CRITICAL: Mark as correction
                { "source_file", "user_correction" },
                { "restored", true } // Mark as restored
            });
            }

            var addData = new
            {
                ids,
                embeddings,
                documents,
                metadatas
            };

            _collectionId = await InitializeCollectionAsync();
            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/add",
                addData);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to restore correction batch: {errorContent}");
                throw new Exception($"Failed to restore corrections: {errorContent}");
            }

            _logger.LogInformation($"Successfully restored batch of {corrections.Count} corrections");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore correction batch");
            throw;
        }
    }

    public Task<List<ModelConfiguration>> GetAvailableModelsAsync()
    {
        throw new NotImplementedException();
    }

    public Task<QueryResponse> ProcessQueryAsync(string question, string? generationModel = null, string? embeddingModel = null, int maxResults = 15, bool meaiInfo = true, string? sessionId = null, bool useReRanking = true)
    {
        throw new NotImplementedException();
    }

    public Task MarkAppreciatedAsync(string sessionId, string question)
    {
        throw new NotImplementedException();
    }

    
}