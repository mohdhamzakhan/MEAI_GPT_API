// Services/DynamicRagService.cs
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static MEAI_GPT_API.Models.Conversation;

namespace MEAI_GPT_API.Services
{
    public class DynamicRagService : IRAGService
    {
        private readonly IModelManager _modelManager;
        private readonly DynamicCollectionManager _collectionManager;
        private readonly DynamicRAGConfiguration _config;
        private readonly ChromaDbOptions _chromaOptions;
        private readonly ILogger<DynamicRagService> _logger;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _chromaClient;
        private readonly IDocumentProcessor _documentProcessor;
        private readonly ICacheManager _cacheManager;
        private readonly IMetricsCollector _metrics;
        private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();
        private readonly ConcurrentBag<CorrectionEntry> _correctionsCache = new();
        private readonly object _lockObject = new();
        private readonly string _currentUser = "system";
        private bool _isInitialized = false;

        public DynamicRagService(
            IModelManager modelManager,
            DynamicCollectionManager collectionManager,
            IOptions<DynamicRAGConfiguration> config,
            IOptions<ChromaDbOptions> chromaOptions,
            ILogger<DynamicRagService> logger,
            IHttpClientFactory httpClientFactory,
            IDocumentProcessor documentProcessor,
            ICacheManager cacheManager,
            IMetricsCollector metrics)
        {
            _modelManager = modelManager;
            _collectionManager = collectionManager;
            _config = config.Value;
            _chromaOptions = chromaOptions.Value;
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("OllamaAPI");
            _chromaClient = httpClientFactory.CreateClient("ChromaDB");
            _documentProcessor = documentProcessor;
            _cacheManager = cacheManager;
            _metrics = metrics;

            InitializeSessionCleanup();
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                _logger.LogInformation("🚀 Starting dynamic RAG system initialization");

                // Ensure directories exist
                EnsureDirectoriesExist();

                // Create abbreviation context if needed
                EnsureAbbreviationContext();

                // Discover available models
                var availableModels = await _modelManager.DiscoverAvailableModelsAsync();
                _logger.LogInformation($"📋 Found {availableModels.Count} available models");

                if (!availableModels.Any())
                {
                    throw new RAGServiceException("No models available for RAG system");
                }

                // Set default models if not configured
                await ConfigureDefaultModelsAsync(availableModels);

                // Initialize collections for all embedding models
                var embeddingModels = availableModels.Where(m =>
                    m.Type == "embedding" || m.Type == "both").ToList();

                if (!embeddingModels.Any())
                {
                    throw new RAGServiceException("No embedding models available");
                }

                // Process documents for all embedding models
                await ProcessDocumentsForAllModelsAsync(embeddingModels);

                _isInitialized = true;
                _logger.LogInformation("✅ Dynamic RAG system initialization completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize dynamic RAG system");
                throw;
            }
        }

        private void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(_chromaOptions.PolicyFolder))
            {
                Directory.CreateDirectory(_chromaOptions.PolicyFolder);
                _logger.LogInformation($"Created policy folder: {_chromaOptions.PolicyFolder}");
            }

            if (!Directory.Exists(_chromaOptions.ContextFolder))
            {
                Directory.CreateDirectory(_chromaOptions.ContextFolder);
                _logger.LogInformation($"Created context folder: {_chromaOptions.ContextFolder}");
            }
        }

        private void EnsureAbbreviationContext()
        {
            var abbreviationsPath = Path.Combine(_chromaOptions.ContextFolder, "abbreviations.txt");
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

                File.WriteAllText(abbreviationsPath, abbreviationContent);
                _logger.LogInformation("Created abbreviations context file");
            }
        }

        private async Task ConfigureDefaultModelsAsync(List<ModelConfiguration> availableModels)
        {
            if (string.IsNullOrEmpty(_config.DefaultEmbeddingModel))
            {
                var embeddingModel = availableModels.FirstOrDefault(m =>
                    m.Type == "embedding" || m.Type == "both");

                if (embeddingModel != null)
                {
                    _config.DefaultEmbeddingModel = embeddingModel.Name;
                    _logger.LogInformation($"🎯 Auto-selected default embedding model: {embeddingModel.Name}");
                }
            }

            if (string.IsNullOrEmpty(_config.DefaultGenerationModel))
            {
                var generationModel = availableModels.FirstOrDefault(m =>
                    m.Type == "generation" || m.Type == "both");

                if (generationModel != null)
                {
                    _config.DefaultGenerationModel = generationModel.Name;
                    _logger.LogInformation($"🎯 Auto-selected default generation model: {generationModel.Name}");
                }
            }
        }

        private async Task ProcessDocumentsForAllModelsAsync(List<ModelConfiguration> embeddingModels)
        {
            var policyFiles = GetPolicyFiles();
            _logger.LogInformation($"📄 Processing {policyFiles.Count} files for {embeddingModels.Count} embedding models");

            var tasks = embeddingModels.Select(async model =>
            {
                _logger.LogInformation($"🔄 Processing documents for model: {model.Name}");

                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);

                foreach (var filePath in policyFiles)
                {
                    await ProcessFileForModelAsync(filePath, model, collectionId);
                }

                _logger.LogInformation($"✅ Completed document processing for model: {model.Name}");
            });

            await Task.WhenAll(tasks);
        }

        private List<string> GetPolicyFiles()
        {
            var policyFiles = new List<string>();

            if (Directory.Exists(_chromaOptions.PolicyFolder))
            {
                policyFiles.AddRange(Directory.GetFiles(_chromaOptions.PolicyFolder, "*.*", SearchOption.AllDirectories)
                    .Where(f => _chromaOptions.SupportedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant())));
            }

            if (Directory.Exists(_chromaOptions.ContextFolder))
            {
                policyFiles.AddRange(Directory.GetFiles(_chromaOptions.ContextFolder, "*.txt", SearchOption.AllDirectories));
            }

            return policyFiles;
        }

        private async Task ProcessFileForModelAsync(string filePath, ModelConfiguration model, string collectionId)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var cacheKey = $"file_processed:{filePath}:{fileInfo.LastWriteTime.Ticks}:{model.Name}";

                if (await _cacheManager.ExistsAsync(cacheKey))
                {
                    _logger.LogDebug($"Skipping {Path.GetFileName(filePath)} for {model.Name} - already processed");
                    return;
                }

                var content = await _documentProcessor.ExtractTextAsync(filePath);
                if (string.IsNullOrWhiteSpace(content)) return;

                var chunks = ChunkText(content, filePath);
                await ProcessChunkBatchForModelAsync(chunks, model, collectionId, fileInfo.LastWriteTime);

                await _cacheManager.SetAsync(cacheKey, true, TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process file {filePath} for model {model.Name}");
            }
        }

        private async Task ProcessChunkBatchForModelAsync(
            List<(string Text, string SourceFile)> chunks,
            ModelConfiguration model,
            string collectionId,
            DateTime lastModified)
        {
            try
            {
                var embeddings = new List<List<float>>();
                var documents = new List<string>();
                var metadatas = new List<Dictionary<string, object>>();
                var ids = new List<string>();

                foreach (var (text, sourceFile) in chunks)
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    try
                    {
                        var cleanedText = CleanText(text);
                        var embedding = await GetEmbeddingAsync(cleanedText, model);

                        if (embedding.Count == 0) continue;

                        embeddings.Add(embedding);
                        documents.Add(cleanedText);

                        var chunkId = GenerateChunkId(sourceFile, cleanedText, lastModified, model.Name);
                        ids.Add(chunkId);

                        var metadata = CreateChunkMetadata(sourceFile, lastModified, model.Name, cleanedText);
                        metadatas.Add(metadata);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to process chunk from {sourceFile} for model {model.Name}");
                    }
                }

                if (embeddings.Any())
                {
                    await AddToChromaDBAsync(collectionId, ids, embeddings, documents, metadatas);
                    _logger.LogInformation($"✅ Added {embeddings.Count} chunks for model {model.Name}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process chunk batch for model {model.Name}");
            }
        }

        // Updated embedding generation with dynamic model support
        private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be empty");

            // Dynamic text truncation based on model's context length
            var maxLength = model.MaxContextLength * 3; // Approximate char to token ratio
            if (text.Length > maxLength)
                text = text.Substring(0, maxLength);

            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var request = new
                    {
                        model = model.Name, // Dynamic model name!
                        prompt = text,
                        options = model.ModelOptions // Dynamic model options!
                    };

                    var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                    {
                        var embedding = embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToList();

                        // Validate embedding dimension matches model configuration
                        if (embedding.Count != model.EmbeddingDimension)
                        {
                            _logger.LogWarning($"Embedding dimension mismatch for {model.Name}. Expected: {model.EmbeddingDimension}, Got: {embedding.Count}");
                        }

                        return embedding;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Embedding attempt {attempt + 1} failed for model {model.Name}: {ex.Message}");
                    if (attempt == maxRetries - 1)
                        throw;
                    await Task.Delay(2000 * (attempt + 1));
                }
            }

            throw new Exception($"Failed to generate embedding with model {model.Name} after all retries");
        }

        // Updated query processing with dynamic model selection
        public async Task<QueryResponse> ProcessQueryAsync(
            string question,
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
                // Use provided models or defaults
                generationModel ??= _config.DefaultGenerationModel;
                embeddingModel ??= _config.DefaultEmbeddingModel;

                // Validate models are available
                var genModel = await _modelManager.GetModelAsync(generationModel!);
                var embModel = await _modelManager.GetModelAsync(embeddingModel!);

                if (genModel == null)
                    throw new ArgumentException($"Generation model {generationModel} not available");
                if (embModel == null)
                    throw new ArgumentException($"Embedding model {embeddingModel} not available");

                var context = GetOrCreateConversationContext(sessionId);
                _logger.LogInformation($"Processing query with models - Gen: {generationModel}, Emb: {embeddingModel}");

                if (IsHistoryClearRequest(question))
                {
                    return await HandleHistoryClearRequest(context, sessionId);
                }

                // Check corrections (model-agnostic)
                var correction = await CheckCorrectionsAsync(question);
                if (correction != null)
                {
                    _logger.LogInformation($"🎯 Using correction for question: {question}");
                    UpdateConversationHistory(context, question, correction.Answer, new List<RelevantChunk>());

                    stopwatch.Stop();


                    return new QueryResponse
                    {
                        Answer = correction.Answer,
                        IsFromCorrection = true,
                        Sources = new List<string> { "User Correction" },
                        Confidence = 1.0,
                        ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                        RelevantChunks = new List<RelevantChunk>(),
                        SessionId = context.SessionId,
                        ModelsUsed = new Dictionary<string, string>
                        {
                            ["generation"] = generationModel,
                            ["embedding"] = embeddingModel
                        }
                    };
                }

                if (IsTopicChanged(question, context.History))
                {
                    _logger.LogInformation($"Topic changed for session {context.SessionId}, clearing context");
                    ClearContext(context);
                }

                var contextualQuery = BuildContextualQuery(question, context.History);
                var relevantChunks = await GetRelevantChunksAsync(
                    contextualQuery,
                    embModel,
                    maxResults,
                    meaiInfo,
                    context,
                    useReRanking,
                    genModel);

                var answer = await GenerateChatResponseAsync(
                    question,
                    genModel,
                    context.History,
                    relevantChunks,
                    ismeai: meaiInfo
                );

                var answerEmbedding = await GetEmbeddingAsync(answer, embModel);

                // Rank chunks by similarity to answer
                var scoredChunks = new List<(RelevantChunk Chunk, double Similarity)>();
                foreach (var chunk in relevantChunks)
                {
                    var chunkEmbedding = await GetEmbeddingAsync(chunk.Text, embModel);
                    var similarity = CosineSimilarity(answerEmbedding, chunkEmbedding);
                    chunk.Similarity = similarity; // Store back to chunk if you use it
                    scoredChunks.Add((chunk, similarity));
                }

                // Sort by similarity
                var topChunks = scoredChunks
                    .Where(c => c.Similarity >= 0.5) // or another threshold
                    .OrderByDescending(c => c.Similarity)
                    .Take(5)
                    .Select(c =>
                    {
                        c.Chunk.Similarity = c.Similarity;
                        return c.Chunk;
                    })
                    .ToList();

                // Compute confidence
                var confidence = topChunks.FirstOrDefault()?.Similarity ?? 0;


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
                    Confidence = confidence,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    RelevantChunks = topChunks,
                    SessionId = context.SessionId,
                    ModelsUsed = new Dictionary<string, string>
                    {
                        ["generation"] = generationModel,
                        ["embedding"] = embeddingModel
                    }
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

        private double CosineSimilarity(List<float> a, List<float> b)
        {
            if (a.Count != b.Count) throw new ArgumentException("Vector dimensions must match");

            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Count; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }


        // Updated search with model-specific collection
        private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, ModelConfiguration embeddingModel, int maxResults)
        {
            try
            {
                var collectionId = _collectionManager.GetCollectionId(embeddingModel.Name);
                if (string.IsNullOrEmpty(collectionId))
                {
                    throw new InvalidOperationException($"No collection found for embedding model {embeddingModel.Name}");
                }

                _logger.LogInformation($"Searching with model {embeddingModel.Name} in collection {collectionId}");

                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = maxResults * 2,
                    include = new[] { "documents", "metadatas", "distances" }
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData);

                var responseContent = await response.Content.ReadAsStringAsync();
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(responseContent);
                return ParseSearchResults(doc.RootElement, maxResults);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ChromaDB search failed for model {embeddingModel.Name}");
                return new List<RelevantChunk>();
            }
        }

        private List<RelevantChunk> ParseSearchResults(JsonElement root, int maxResults)
        {
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

                    if (similarity < 0.15) continue; // Lowered threshold for broader coverage

                    var metadata = metadatas[i];
                    var sourceFile = metadata.TryGetProperty("source_file", out var sf)
                        ? Path.GetFileName(sf.GetString() ?? "")
                        : "Unknown";

                    var documentText = documents[i].GetString() ?? "";

                    // Priority boost for context files
                    if (sourceFile.Contains("abbreviation") || sourceFile.Contains("context") ||
                        documentText.ToUpper().Contains("CL =") || documentText.ToUpper().Contains("CASUAL LEAVE"))
                    {
                        similarity = Math.Min(0.95, similarity + 0.2);
                    }

                    relevantChunks.Add(new RelevantChunk
                    {
                        Text = documentText,
                        Source = sourceFile,
                        Similarity = similarity
                    });
                }
            }

            return relevantChunks
                .OrderByDescending(c => c.Similarity)
                .Take(maxResults)
                .ToList();
        }

        private async Task<string> GenerateChatResponseAsync(
            string question,
            ModelConfiguration generationModel,
            List<ConversationTurn> history,
            List<RelevantChunk> chunks,
            bool ismeai = true)
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

            // Add recent conversation history
            foreach (var turn in history.TakeLast(8))
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

                var groupedChunks = chunks
                    .GroupBy(c => c.Source)
                    .OrderByDescending(g => g.Max(c => c.Similarity))
                    .Take(6);

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
                model = generationModel.Name,
                messages,
                temperature = generationModel.Temperature,
                stream = false,
                options = generationModel.ModelOptions
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

        // API endpoint to get available models
        public async Task<List<ModelConfiguration>> GetAvailableModelsAsync()
        {
            return await _modelManager.DiscoverAvailableModelsAsync();
        }

        public async Task<SystemStatus> GetSystemStatusAsync()
        {
            var models = await GetAvailableModelsAsync();
            var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();
            var generationModels = models.Where(m => m.Type == "generation" || m.Type == "both").ToList();

            return new SystemStatus
            {
                TotalEmbeddings = await GetTotalEmbeddingsAcrossModelsAsync(),
                TotalCorrections = await GetCorrectionsCountAsync(),
                LastUpdated = DateTime.UtcNow,
                IsHealthy = await IsHealthy(),
                PoliciesFolder = _chromaOptions.PolicyFolder,
                SupportedExtensions = _chromaOptions.SupportedExtensions.ToList(),
                AvailableModels = models,
                EmbeddingModels = embeddingModels,
                GenerationModels = generationModels,
                DefaultEmbeddingModel = _config.DefaultEmbeddingModel ?? "",
                DefaultGenerationModel = _config.DefaultGenerationModel ?? ""
            };
        }

        private async Task<int> GetTotalEmbeddingsAcrossModelsAsync()
        {
            try
            {
                int totalEmbeddings = 0;
                var collectionIds = await _collectionManager.GetAllCollectionIdsAsync();

                _logger.LogInformation($"Counting embeddings across {collectionIds.Count} model collections");

                foreach (var collectionId in collectionIds)
                {
                    try
                    {
                        var count = await GetCollectionCountAsync(collectionId);
                        totalEmbeddings += count;
                        _logger.LogDebug($"Collection {collectionId}: {count} embeddings");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to get count for collection {collectionId}");
                    }
                }

                _logger.LogInformation($"Total embeddings across all models: {totalEmbeddings}");
                return totalEmbeddings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get total embeddings across models");
                return 0;
            }
        }

        private async Task<int> GetCollectionCountAsync(string collectionId)
        {
            try
            {
                var response = await _chromaClient.GetAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/count");

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
                _logger.LogError(ex, $"Error getting count for collection {collectionId}");
                return 0;
            }
        }

        // Additional helper methods...
        private Dictionary<string, object> CreateChunkMetadata(string sourceFile, DateTime lastModified, string modelName, string text)
        {
            var metadata = new Dictionary<string, object>
            {
                { "source_file", sourceFile },
                { "last_modified", lastModified.ToString("O") },
                { "model", modelName },
                { "chunk_size", text.Length },
                { "processed_at", DateTime.UtcNow.ToString("O") },
                { "processed_by", _currentUser }
            };

            if (sourceFile.Contains("abbreviation") || sourceFile.Contains("context"))
            {
                metadata["is_context"] = true;
                metadata["priority"] = "high";
            }

            return metadata;
        }

        private string GenerateChunkId(string sourceFile, string text, DateTime lastModified, string modelName)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourceFile);
            var textHash = text.GetHashCode().ToString("X");
            var timeStamp = lastModified.Ticks.ToString();
            var modelHash = modelName.GetHashCode().ToString("X");
            return $"{fileName}_{textHash}_{timeStamp}_{modelHash}";
        }

        private List<(string Text, string SourceFile)> ChunkText(string text, string sourceFile, int maxTokens = 200)
        {
            var chunks = new List<(string, string)>();
            var sentences = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var sb = new StringBuilder();
            int tokenCount = 0;

            foreach (var sentence in sentences)
            {
                int sentenceTokens = EstimateTokenCount(sentence);

                if (tokenCount + sentenceTokens > maxTokens && sb.Length > 0)
                {
                    chunks.Add((sb.ToString().Trim(), sourceFile));
                    sb.Clear();
                    tokenCount = 0;
                }

                sb.AppendLine(sentence);
                tokenCount += sentenceTokens;
            }

            if (sb.Length > 0)
            {
                chunks.Add((sb.ToString().Trim(), sourceFile));
            }

            return chunks.Where(c => !string.IsNullOrWhiteSpace(c.Item1)).ToList();
        }

        private string CleanText(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\w\s.,!?-]", "");
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return text.Trim();
        }

        private int EstimateTokenCount(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? 0 : text.Length / 4;
        }

        private string CleanAndEnhanceResponse(string response)
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                response,
                "","",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n\s*\n\s*\n", "\n\n");
            return cleaned.Trim();
        }

        // Session management methods
        private void InitializeSessionCleanup()
        {
            var timer = new System.Threading.Timer(
                callback: _ => CleanupExpiredSessions(),
                state: null,
                dueTime: TimeSpan.FromMinutes(30),
                period: TimeSpan.FromMinutes(30));
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup expired sessions");
            }
        }

        private ConversationContext GetOrCreateConversationContext(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = $"temp_{Guid.NewGuid():N}";
            }

            var context = _sessionContexts.GetOrAdd(sessionId, _ => new ConversationContext
            {
                SessionId = sessionId,
                CreatedAt = DateTime.Now,
                LastAccessed = DateTime.Now
            });

            context.LastAccessed = DateTime.Now;
            return context;
        }

        // Implement other required methods from IRAGService
        private bool IsHistoryClearRequest(string question)
        {
            var clearKeywords = new[] { "clear", "delete", "history", "reset", "start over" };
            return clearKeywords.Any(keyword => question.ToLower().Contains(keyword));
        }

        private async Task<QueryResponse> HandleHistoryClearRequest(ConversationContext context, string? sessionId)
        {
            ClearContext(context);
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

        private void ClearContext(ConversationContext context)
        {
            context.History.Clear();
            context.RelevantChunks.Clear();
            context.LastAccessed = DateTime.Now;
        }

        private bool IsTopicChanged(string question, List<ConversationTurn> history)
        {
            return history.Count > 0 && CalculateTextSimilarity(question, history.Last().Question) < 0.3;
        }

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

        private double CalculateTextSimilarity(string text1, string text2)
        {
            var words1 = text1.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var words2 = text2.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        private async Task<List<RelevantChunk>> GetRelevantChunksAsync(
            string query,
            ModelConfiguration embeddingModel,
            int maxResults,
            bool meaiInfo,
            ConversationContext context,
            bool useReRanking,
            ModelConfiguration generationModel)
        {
            if (!meaiInfo)
                return new List<RelevantChunk>();

            try
            {
                var correction = await CheckCorrectionsAsync(query);
                if (correction != null)
                    return new List<RelevantChunk>();

                var chunks = await SearchChromaDBAsync(query, embeddingModel, maxResults);
                return chunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get relevant chunks");
                return new List<RelevantChunk>();
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

                if (context.History.Count > 10)
                {
                    context.History = context.History.TakeLast(10).ToList();
                }

                context.LastAccessed = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update conversation history");
            }
        }

        private async Task<CorrectionEntry?> CheckCorrectionsAsync(string question)
        {
            // Simplified implementation - you can enhance based on your needs
            lock (_lockObject)
            {
                return _correctionsCache
                    .Where(c => c.Question.Equals(question, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.Date)
                    .FirstOrDefault();
            }
        }

        private async Task<int> GetCorrectionsCountAsync()
        {
            lock (_lockObject)
            {
                return _correctionsCache.Count;
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
            catch
            {
                return false;
            }
        }

        private async Task<bool> AddToChromaDBAsync(
            string collectionId,
            List<string> ids,
            List<List<float>> embeddings,
            List<string> documents,
            List<Dictionary<string, object>> metadatas)
        {
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
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/add",
                    addData);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add to ChromaDB");
                return false;
            }
        }

        // Implement other required interface methods...
        public Task SaveCorrectionAsync(string question, string correctAnswer, string model)
        {
            // Implementation here
            throw new NotImplementedException();
        }

        public Task RefreshEmbeddingsAsync(string model = "mistral:latest")
        {
            // Implementation here - you can modify this to be dynamic
            throw new NotImplementedException();
        }

        public Task<List<CorrectionEntry>> GetRecentCorrections(int limit = 50)
        {
            // Implementation here
            throw new NotImplementedException();
        }

        public Task<bool> DeleteCorrectionAsync(string id)
        {
            // Implementation here
            throw new NotImplementedException();
        }

        public Task ProcessUploadedPolicyAsync(Stream fileStream, string fileName, string model)
        {
            // Implementation here
            throw new NotImplementedException();
        }

        public Task<QueryResponse> ProcessQueryAsync(string question, string generationModel, int maxResults = 10, bool meaiInfo = true, string? sessionId = null, bool useReRanking = true, string? embeddingModel = null)
        {
            throw new NotImplementedException();
        }
    }
}
