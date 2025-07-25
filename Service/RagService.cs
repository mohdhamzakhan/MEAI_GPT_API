using MEAI_GPT_API.Models;
using Microsoft.Extensions.Options;
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

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        try
        {
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
        // Create a hash of policy files to detect changes
        var policyFiles = new List<string>();
        foreach(var extension in _options.SupportedExtensions)
        {
            policyFiles.AddRange(Directory.GetFiles(_options.PolicyFolder, $"*{extension}", SearchOption.AllDirectories));
        }
        var fileInfos = policyFiles.Select(f => new FileInfo(f));
        var latestUpdate = fileInfos.Max(f => f.LastWriteTime);
        var filesHash = string.Join("|", fileInfos.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTime.Ticks}"));
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(filesHash));
    }

    private async Task<string> InitializeCollectionAsync()
    {
        try
        {
            var collectionName = _options.Collections["policies"];
            var collectionData = new
            {
                name = collectionName,
                metadata = new { description = "MEAI HR Policy documents" }
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
            var chromaHealth = await _chromaClient.GetAsync("/api/v2/heartbeat");
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
            "nomic-embed-text:latest",
            "mxbai-embed-large",
            "bge-large-en",
            "bge-base-en"
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

    public async Task<QueryResponse> ProcessQueryAsync(
        string question,
        string generationModel,
        int maxResults = 10,
        bool meaiInfo = true,
        string? sessionId = null,
        bool useReRanking = true,
        string? embeddingModel = null)
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

            if (IsHistoryClearRequest(question))
            {
                return await HandleHistoryClearRequest(context, sessionId);
            }

            embeddingModel ??= await EnsureEmbeddingModelAvailableAsync();

            if (IsTopicChanged(question, context.History))
            {
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
                relevantChunks);

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
                RelevantChunks = relevantChunks.Take(5).ToList()
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Query processing failed");
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

        const int batchSize = 5;
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            await ProcessChunkBatch(batch, model, filePath, lastModified);

            if (i + batchSize < chunks.Count)
            {
                await Task.Delay(1000); // Rate limiting
            }
        }

        // Mark file as processed
        var cacheKey = $"file_processed:{filePath}:{lastModified.Ticks}";
        await _cacheManager.SetAsync(cacheKey, true, TimeSpan.FromDays(30));
    }

    private async Task<QueryResponse> HandleHistoryClearRequest(
        ConversationContext context,
        string? sessionId)
    {
        ClearContext(context);
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessionContexts.TryRemove(sessionId, out _);
        }

        return new QueryResponse
        {
            Answer = "✅ Conversation history cleared. How can I assist you today?",
            IsFromCorrection = false,
            Sources = new List<string>(),
            Confidence = 1.0,
            ProcessingTimeMs = 0,
            RelevantChunks = new List<RelevantChunk>()
        };
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
                return context.RelevantChunks.Select(e => new RelevantChunk
                {
                    Text = e.Text,
                    Source = e.SourceFile,
                    Similarity = e.Similarity
                }).ToList();
            }

            var chunks = await SearchChromaDBAsync(query, embeddingModel, maxResults);

            if (useReRanking && chunks.Count > maxResults)
            {
                chunks = await ReRankChunksAsync(query, chunks, maxResults, generationModel);
            }

            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get relevant chunks");
            return new List<RelevantChunk>();
        }
    }

    private ConversationContext GetOrCreateConversationContext(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return new ConversationContext();

        return _sessionContexts.GetOrAdd(sessionId, _ => new ConversationContext());
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
       double temperature = 0.2)
    {
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = @"You are MEAI HR Policy Assistant, an expert advisor with deep knowledge of company policies and procedures. "
            }
        };

        // Add conversation history (last 3 turns)
        foreach (var turn in history.TakeLast(3))
        {
            messages.Add(new { role = "user", content = turn.Question });
            messages.Add(new { role = "assistant", content = turn.Answer });
        }

        // Add policy context if available
        if (chunks.Any())
        {
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("IMPORTANT:");
            contextBuilder.AppendLine("• Answer ONLY using the provided policy document excerpts and conversation history.");
            contextBuilder.AppendLine("• If the answer is not found, respond exactly with: \"I don't have specific policy information for your query. Please check with HR or refer to the complete policy documents.\"");
            contextBuilder.AppendLine("• Do NOT display or output your reasoning or chain-of-thought. Only provide the final answer clearly and directly.");
            contextBuilder.AppendLine("• Do NOT use <think> tags or similar in your output.");
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("IMPORTANT INSTRUCTIONS:");
            contextBuilder.AppendLine("• Answer ONLY using the provided policy document excerpts and conversation history.");
            contextBuilder.AppendLine("• If the answer is not found in the provided context, respond exactly with: \"I don't have specific policy information for your query. Please check with HR or refer to the complete policy documents.\"");
            contextBuilder.AppendLine("• Do not guess, add assumptions, or use any external knowledge beyond what is provided.");
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("CORE INSTRUCTIONS:");
            contextBuilder.AppendLine("• Provide accurate, actionable answers based ONLY on the provided policy documents");
            contextBuilder.AppendLine("• Use clear, professional language that employees can easily understand");
            contextBuilder.AppendLine("• Structure responses with bullet points or numbered lists when appropriate");
            contextBuilder.AppendLine("• Always cite the exact policy file name in [brackets] after each key point");
            contextBuilder.AppendLine("• Be concise but thorough - include all relevant details without unnecessary elaboration");
            contextBuilder.AppendLine();

            contextBuilder.AppendLine("RESPONSE GUIDELINES:");
            contextBuilder.AppendLine("• For step-by-step processes: Use numbered lists (1, 2, 3...)");
            contextBuilder.AppendLine("• For multiple requirements/benefits: Use bullet points (•)");
            contextBuilder.AppendLine("• For policy clarifications: Provide direct quotes when helpful");
            contextBuilder.AppendLine("• For calculations (leave days, benefits): Show the formula or logic");
            contextBuilder.AppendLine("• For deadlines/timelines: Highlight dates and timeframes clearly");
            contextBuilder.AppendLine();

            contextBuilder.AppendLine("LEAVE ABBREVIATIONS:");
            contextBuilder.AppendLine("• CL = Casual Leave");
            contextBuilder.AppendLine("• SL = Sick Leave");
            contextBuilder.AppendLine("• COFF = Compensatory Off");
            contextBuilder.AppendLine("• EL/PL = Earned Leave / Privilege Leave");
            contextBuilder.AppendLine("• ML = Maternity Leave");
            contextBuilder.AppendLine();

            contextBuilder.AppendLine("CONVERSATION HANDLING:");
            contextBuilder.AppendLine();

            contextBuilder.AppendLine("ERROR HANDLING:");
            contextBuilder.AppendLine("• If policy information is not available: 'I don't have specific policy information for your query. Please check with HR or refer to the complete policy documents.'");
            contextBuilder.AppendLine("• If information is ambiguous: 'Based on available policy information, [provide what you can], but please confirm specific details with HR.'");
            contextBuilder.AppendLine("• If multiple interpretations exist: Present all valid interpretations clearly");
            contextBuilder.AppendLine();

            contextBuilder.AppendLine("FORMATTING REQUIREMENTS:");
            contextBuilder.AppendLine("• Use **bold** for important deadlines, amounts, or key requirements");
            contextBuilder.AppendLine("• Use *italics* for emphasis on critical points");
            contextBuilder.AppendLine("• Include relevant policy section numbers if available");
            contextBuilder.AppendLine("• End responses with: 'For additional clarification, please contact HR.'");
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("=== RELEVANT POLICY INFORMATION ===");

            foreach (var chunk in chunks.Take(5)) // Limit context size
            {
                contextBuilder.AppendLine($"Source: {chunk.Source}");
                contextBuilder.AppendLine($"Content: {chunk.Text}");
                contextBuilder.AppendLine($"Relevance: {chunk.Similarity:F2}");
                contextBuilder.AppendLine("---");
            }

            messages.Add(new { role = "system", content = contextBuilder.ToString() });
        }

        // Add current question
        messages.Add(new { role = "user", content = question });

        var requestData = new
        {
            model = model,
            messages = messages,
            temperature = temperature,
            stream = false,
            options = new
            {
                num_ctx = 4096,
                temperature = temperature,
                top_p = 0.9,
                repeat_penalty = 1.1
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/chat", requestData);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

            // Clean up any model-specific artifacts
            return CleanResponse(content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Chat generation failed: {ex.Message}");
            return "I apologize, but I'm having trouble generating a response right now. Please try again or contact HR directly.";
        }
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
    private List<(string Text, string SourceFile)> ChunkText(string text, string sourceFile, int maxTokens = 4192)
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
    private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, string embeddingModel, int maxResults)
    {
        try
        {
            // Generate query embedding
            var queryEmbedding = await GetEmbedding(query, embeddingModel);

            // Search ChromaDB
            var searchData = new
            {
                query_embeddings = new[] { queryEmbedding },
                n_results = maxResults,
                include = new[] { "documents", "metadatas", "distances" }
            };

            var response = await _chromaClient.PostAsJsonAsync($"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/query", searchData);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(result);

            var relevantChunks = new List<RelevantChunk>();
            var root = doc.RootElement;

            if (root.TryGetProperty("documents", out var documentsArray) && documentsArray.GetArrayLength() > 0)
            {
                var documents = documentsArray[0].EnumerateArray().ToArray();
                var metadatas = root.GetProperty("metadatas")[0].EnumerateArray().ToArray();
                var distances = root.GetProperty("distances")[0].EnumerateArray().ToArray();

                for (int i = 0; i < documents.Length; i++)
                {
                    var similarity = 1.0 - distances[i].GetDouble(); // Convert distance to similarity
                    var metadata = metadatas[i];

                    relevantChunks.Add(new RelevantChunk
                    {
                        Text = documents[i].GetString() ?? "",
                        Source = metadata.TryGetProperty("source_file", out var sourceFile)
                            ? Path.GetFileName(sourceFile.GetString() ?? "")
                            : "Unknown",
                        Similarity = similarity
                    });
                }
            }

            return relevantChunks.OrderByDescending(c => c.Similarity).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ChromaDB search failed: {ex.Message}");
            return new List<RelevantChunk>();
        }
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
                    max_tokens = 1000
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
    private async Task ProcessChunkBatch(
       List<(string Text, string SourceFile)> chunks,
       string model,
       string filePath,
       DateTime lastModified)
    {
        var embeddings = new List<List<float>>();
        var documents = new List<string>();
        var metadatas = new List<Dictionary<string, object>>();
        var ids = new List<string>();

        foreach (var (text, sourceFile) in chunks)
        {
            try
            {
                var embedding = await GetEmbedding(text, model);
                embeddings.Add(embedding);
                documents.Add(text);

                var chunkId = GenerateChunkId(filePath, text, lastModified);
                ids.Add(chunkId);

                metadatas.Add(new Dictionary<string, object>
{
    { "source_file", filePath },
    { "last_modified", lastModified.ToString("O") },
    { "model", model },
    { "chunk_size", text.Length }
});
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to process chunk for file {sourceFile}: {ex.Message}");
                continue;
            }
        }

        if (embeddings.Any())
        {
            await AddToChromaDBAsync(ids, embeddings, documents, metadatas); // metadatas is List<Dictionary<string, object>>
        }
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

        // Truncate if too long
        if (text.Length > 8000)
            text = text.Substring(0, 8000);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var request = new
                {
                    model = "nomic-embed-text:latest",
                    prompt = text,
                    options = new
                    {
                        num_ctx = 2048,
                        temperature = 0.0
                    }
                };

                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                {
                    return embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Embedding attempt {attempt + 1} failed: {ex.Message}");
                if (attempt == maxRetries - 1)
                    throw;

                await Task.Delay(1000 * (attempt + 1));
            }
        }

        throw new Exception("Failed to generate embedding");
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
            // First check cache
            var cacheKey = $"correction:{question.GetHashCode()}";
            var cachedCorrection = await _cacheManager.GetAsync<CorrectionEntry>(cacheKey);
            if (cachedCorrection != null)
            {
                _metrics.RecordCacheOperation("correction_hit", 0, true);
                return cachedCorrection;
            }

            // If not in cache, search ChromaDB corrections collection
            var searchData = new
            {
                where = new Dictionary<string, object>
                {
                    { "question", question }
                },
                limit = 1
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_collectionId}/get",
                searchData);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to check corrections: {StatusCode}",
                    response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<ChromaGetResponse>();
            if (result?.Documents?.FirstOrDefault() == null)
            {
                return null;
            }

            var correction = new CorrectionEntry
            {
                Question = question,
                Answer = result.Documents[0],
                Date = DateTime.Now
            };

            // Cache the correction
            await _cacheManager.SetAsync(cacheKey, correction, TimeSpan.FromHours(24));
            _metrics.RecordCacheOperation("correction_store", 0, true);

            return correction;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking corrections for question: {Question}", question);
            return null;
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

            // Keep only last 5 turns to manage memory
            if (context.History.Count > 5)
            {
                context.History = context.History.TakeLast(5).ToList();
            }

            // Update relevant chunks for context
            context.RelevantChunks = relevantChunks.Select(r => new EmbeddingData(
                Text: r.Text,
                Vector: new List<float>(), // We don't need to store vectors in memory
                SourceFile: r.Source,
                LastModified: DateTime.Now,
                model: _options.DefaultEmbeddingModel)
            {
                Similarity = r.Similarity
            }).ToList();

            context.LastAccessed = DateTime.Now;

            // Log conversation turn for analytics
            _logger.LogInformation(
                "Conversation turn recorded. Question length: {QuestionLength}, Answer length: {AnswerLength}, Sources: {SourceCount}",
                question.Length,
                answer.Length,
                turn.Sources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update conversation history");
            // Don't throw - this is not critical for the response
        }
    }

    private class ChromaGetResponse
    {
        public List<string>? Documents { get; set; }
        public List<Dictionary<string, object>>? Metadatas { get; set; }
    }

    public async Task RefreshEmbeddingsAsync(string model = "default")
    {
        try
        {
            _logger.LogInformation($"Starting embeddings refresh {DateTime.Now}");

            // Delete existing collection
            await DeleteCollectionIfExistsAsync(_options.Collections["policies"]);

            // Create new collection
            await InitializeCollectionAsync();

            // Load and process policy files
            await LoadOrGenerateEmbeddings(model);

            _logger.LogInformation("Embeddings refresh completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh embeddings");
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
                    $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{collectionId}");

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
            _logger.LogInformation($"Saving correction at {DateTime.Now}");

            var correction = new CorrectionEntry
            {
                Id = Guid.NewGuid().ToString(),
                Question = question,
                Answer = correctAnswer
            };

            // Add to ChromaDB
            var embeddingModel = GetBestEmbeddingModel();
            var embedding = await GetEmbedding(question, embeddingModel);

            var addData = new
            {
                ids = new[] { correction.Id.ToString() },
                embeddings = new[] { embedding },
                documents = new[] { correctAnswer },
                metadatas = new[]
                {
                new Dictionary<string, object>
                {
                    { "question", question },
                    { "model", model },
                    { "timestamp", DateTime.Now},
                    { "user", "Hamza" }
                }
            }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{_options.Collections}/add",
                addData);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to save correction: {await response.Content.ReadAsStringAsync()}");
            }

            _logger.LogInformation($"Successfully saved correction for question: {question}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save correction for question: {Question}", question);
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
            var response = await _chromaClient.GetAsync(
                $"/api/v2/tenants/{_options.Tenant} /databases/ {_options.Database} /collections/ {_collectionId}/count");

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
            var response = await _chromaClient.GetAsync($"/api/v2/tenants/{_options.Tenant}  /databases/  {_options.Database}  /collections/  {_options.Collections["policies"]}/count");
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
            "nomic-embed-text:latest",
            "bge-large-en",
            "bge-base-en",
            "all-MiniLM-L6-v2"
        };

        foreach (var model in preferredModels)
        {
            if (IsModelAvailable(model).Result)
            {
                return model;
            }
        }

        return "nomic-embed-text:latest"; // Default fallback
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
}