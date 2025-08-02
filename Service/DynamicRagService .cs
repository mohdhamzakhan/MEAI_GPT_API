// Services/DynamicRagService.cs
using DocumentFormat.OpenXml.InkML;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
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
        private readonly Conversation _conversation;
        private static readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();
        private static readonly ConcurrentBag<CorrectionEntry> _correctionsCache = new();
        private readonly object _lockObject = new();
        private readonly string _currentUser = "system";
        private bool _isInitialized = false;
        private readonly object _lock = new();
        private static readonly ConcurrentBag<(string Question, string Answer, List<RelevantChunk> Chunks)> _appreciatedTurns = new();
        private readonly PlantSettings _plants;
        private readonly IConversationStorageService _conversationStorage;
        public DynamicRagService(
            IModelManager modelManager,
            DynamicCollectionManager collectionManager,
            IOptions<DynamicRAGConfiguration> config,
            IOptions<ChromaDbOptions> chromaOptions,
            ILogger<DynamicRagService> logger,
            IHttpClientFactory httpClientFactory,
            IDocumentProcessor documentProcessor,
            ICacheManager cacheManager,
            Conversation conversation,
            IOptions<PlantSettings> plants,
            IMetricsCollector metrics,
            IConversationStorageService conversationStorage)
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
            _conversation = conversation;
            _plants = plants.Value;
            _conversationStorage = conversationStorage;

            InitializeSessionCleanup();
        }

        // 🆕 Enhanced InitializeAsync method
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;
            lock (_lock)
            {
                if (_isInitialized) return;
                _isInitialized = true;
            }
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
                foreach (var plant in _plants.Plants.Keys)
                {
                    _logger.LogInformation($"Processing documents for plant: {plant}");
                    await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);
                }

                // 🆕 Load historical appreciated answers
                await LoadHistoricalAppreciatedAnswersAsync();
                await LoadCorrectionCacheAsync();

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
        private async Task ProcessDocumentsForAllModelsAsync(List<ModelConfiguration> embeddingModels, string plant)
        {
            var policyFiles = GetPolicyFiles(plant);
            _logger.LogInformation($"📄 Processing {policyFiles.Count} files for {embeddingModels.Count} embedding models");

            var tasks = embeddingModels.Select(async model =>
            {
                _logger.LogInformation($"🔄 Processing documents for model: {model.Name}");

                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);

                foreach (var filePath in policyFiles)
                {
                    await ProcessFileForModelAsync(filePath, model, collectionId, plant);
                }

                _logger.LogInformation($"✅ Completed document processing for model: {model.Name}");
            });

            await Task.WhenAll(tasks);
        }
        private List<string> GetPolicyFiles(string plant)
        {
            var policyFiles = new List<string>();

            if (Directory.Exists(Path.Combine(_chromaOptions.PolicyFolder, plant)))
            {
                string path = Path.Combine(_chromaOptions.PolicyFolder, plant);
                policyFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => _chromaOptions.SupportedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant())));
            }

            if (Directory.Exists(_chromaOptions.ContextFolder))
            {
                policyFiles.AddRange(Directory.GetFiles(_chromaOptions.ContextFolder, "*.txt", SearchOption.AllDirectories));
            }

            return policyFiles;
        }
        private async Task ProcessFileForModelAsync(string filePath, ModelConfiguration model, string collectionId, string plant)
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
                await ProcessChunkBatchForModelAsync(chunks, model, collectionId, fileInfo.LastWriteTime, plant);

                await _cacheManager.SetAsync(cacheKey, true, TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process file {filePath} for model {model.Name}");
            }
        }
        //private async Task ProcessChunkBatchForModelAsync(
        //    List<(string Text, string SourceFile)> chunks,
        //    ModelConfiguration model,
        //    string collectionId,
        //    DateTime lastModified,
        //    string plant)
        //{
        //    try
        //    {
        //        var embeddings = new List<List<float>>();
        //        var documents = new List<string>();
        //        var metadatas = new List<Dictionary<string, object>>();
        //        var ids = new List<string>();

        //        foreach (var (text, sourceFile) in chunks)
        //        {
        //            if (string.IsNullOrWhiteSpace(text)) continue;

        //            try
        //            {
        //                var cleanedText = CleanText(text);
        //                var embedding = await GetEmbeddingAsync(cleanedText, model);

        //                if (embedding.Count == 0) continue;

        //                embeddings.Add(embedding);
        //                documents.Add(cleanedText);

        //                var chunkId = GenerateChunkId(sourceFile, cleanedText, lastModified, model.Name);
        //                ids.Add(chunkId);

        //                var metadata = CreateChunkMetadata(sourceFile, lastModified, model.Name, cleanedText, plant);
        //                metadatas.Add(metadata);
        //            }
        //            catch (Exception ex)
        //            {
        //                _logger.LogError(ex, $"Failed to process chunk from {sourceFile} for model {model.Name}");
        //            }
        //        }

        //        if (embeddings.Any())
        //        {
        //            await AddToChromaDBAsync(collectionId, ids, embeddings, documents, metadatas);
        //            _logger.LogInformation($"✅ Added {embeddings.Count} chunks for model {model.Name}");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Failed to process chunk batch for model {model.Name}");
        //    }
        //}
        // Updated embedding generation with dynamic model support
        //private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        //{
        //    if (string.IsNullOrWhiteSpace(text))
        //        throw new ArgumentException("Text cannot be empty");

        //    // Dynamic text truncation based on model's context length
        //    var maxLength = model.MaxContextLength * 3; // Approximate char to token ratio
        //    if (text.Length > maxLength)
        //        text = text.Substring(0, maxLength);

        //    const int maxRetries = 3;
        //    var safeOptions = new Dictionary<string, object>();
        //    foreach (var kvp in model.ModelOptions)
        //    {
        //        object value = kvp.Value;

        //        if (kvp.Key == "num_ctx" && value is String d)
        //            value = int.Parse(d);
        //        else if (kvp.Key == "top_p" && value is string e)
        //            value = float.Parse(e);
        //        else if (kvp.Key == "temperature" && value is string f)
        //            value = float.Parse(f);

        //        safeOptions[kvp.Key] = value;
        //    }

        //    for (int attempt = 0; attempt < maxRetries; attempt++)
        //    {
        //        try
        //        {
        //            var request = new
        //            {
        //                model = model.Name, // Dynamic model name!
        //                prompt = text,
        //                options = safeOptions // Dynamic model options!
        //            };

        //            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request);
        //            response.EnsureSuccessStatusCode();

        //            var json = await response.Content.ReadAsStringAsync();
        //            using var doc = JsonDocument.Parse(json);

        //            if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
        //            {
        //                var embedding = embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToList();

        //                // Validate embedding dimension matches model configuration
        //                if (embedding.Count != model.EmbeddingDimension)
        //                {
        //                    _logger.LogWarning($"Embedding dimension mismatch for {model.Name}. Expected: {model.EmbeddingDimension}, Got: {embedding.Count}");
        //                }

        //                return embedding;
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogWarning($"Embedding attempt {attempt + 1} failed for model {model.Name}: {ex.Message}");
        //            if (attempt == maxRetries - 1)
        //                throw;
        //            await Task.Delay(2000 * (attempt + 1));
        //        }
        //    }

        //    throw new Exception($"Failed to generate embedding with model {model.Name} after all retries");
        //}
        // Updated query processing with dynamic model selection
        // UPDATE the ProcessQueryAsync method - Add early return for meaiInfo = false
        public async Task<QueryResponse> ProcessQueryAsync(
            string question,
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
                // 🚀 FAST PATH: If meaiInfo is false, skip ALL embedding operations
                if (!meaiInfo)
                {
                    return await ProcessNonMeaiQueryFast(question, sessionId, generationModel, stopwatch);
                }

                // Use provided models or defaults (ONLY when meaiInfo = true)
                generationModel ??= _config.DefaultGenerationModel;
                embeddingModel ??= _config.DefaultEmbeddingModel;

                // Validate models are available
                var genModel = await _modelManager.GetModelAsync(generationModel!);
                var embModel = await _modelManager.GetModelAsync(embeddingModel!);

                if (genModel == null)
                    throw new ArgumentException($"Generation model {generationModel} not available");
                if (embModel == null)
                    throw new ArgumentException($"Embedding model {embeddingModel} not available");
                if (embModel.EmbeddingDimension == 0)
                    embModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);

                // Get or create session in database
                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(),
                    _currentUser);

                var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);

                _logger.LogInformation($"Processing MEAI query with models - Gen: {generationModel}, Emb: {embeddingModel}");

                if (IsHistoryClearRequest(question))
                {
                    return await HandleHistoryClearRequest(context, dbSession.SessionId);
                }

                // 🆕 Check for similar conversations in database first (ONLY for MEAI queries)
                var questionEmbedding = await GetEmbeddingAsync(question, embModel);
                var similarConversations = await _conversationStorage.SearchSimilarConversationsAsync(
                    questionEmbedding, plant, threshold: 0.8, limit: 3);

                if (similarConversations.Any())
                {
                    var bestMatch = similarConversations.First();
                    if (bestMatch.Entry.WasAppreciated)
                    {
                        _logger.LogInformation($"💡 Reusing appreciated answer from database (ID: {bestMatch.Entry.Id})");

                        // Create a copy for this session
                        await SaveConversationToDatabase(
                            dbSession.SessionId, question, bestMatch.Entry.Answer,
                            new List<RelevantChunk>(), genModel, embModel,
                            bestMatch.Similarity, stopwatch.ElapsedMilliseconds,
                            isFromCorrection: false, parentId: null, plant: plant);

                        return new QueryResponse
                        {
                            Answer = bestMatch.Entry.Answer,
                            IsFromCorrection = false,
                            Sources = bestMatch.Entry.Sources,
                            Confidence = bestMatch.Similarity,
                            ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                            RelevantChunks = new List<RelevantChunk>(),
                            SessionId = dbSession.SessionId,
                            ModelsUsed = new Dictionary<string, string>
                            {
                                ["generation"] = generationModel,
                                ["embedding"] = embeddingModel
                            }
                        };
                    }
                }

                // Check corrections (existing logic) - ONLY for MEAI queries
                var correction = await CheckCorrectionsAsync(question);
                if (correction != null)
                {
                    _logger.LogInformation($"🎯 Using correction for question: {question}");

                    string finalAnswer = correction.Answer;
                    try
                    {
                        finalAnswer = await RephraseWithLLMAsync(correction.Answer, generationModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rephrase correction — using raw answer.");
                    }

                    await SaveConversationToDatabase(
                        dbSession.SessionId, question, finalAnswer,
                        new List<RelevantChunk>(), genModel, embModel,
                        1.0, stopwatch.ElapsedMilliseconds,
                        isFromCorrection: true, parentId: null, plant: plant);

                    stopwatch.Stop();

                    return new QueryResponse
                    {
                        Answer = finalAnswer,
                        IsFromCorrection = true,
                        Sources = new List<string> { "User Correction" },
                        Confidence = 1.0,
                        ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                        RelevantChunks = new List<RelevantChunk>(),
                        SessionId = dbSession.SessionId,
                        ModelsUsed = new Dictionary<string, string>
                        {
                            ["generation"] = generationModel,
                            ["embedding"] = embeddingModel
                        }
                    };
                }

                // Check appreciated answers - ONLY for MEAI queries
                var appreciated = await CheckAppreciatedAnswerAsync(question);
                if (appreciated != null)
                {
                    _logger.LogInformation("✨ Using appreciated answer match for question.");
                    string finalAnswer = appreciated.Value.Answer;
                    try
                    {
                        finalAnswer = await RephraseWithLLMAsync(appreciated.Value.Answer, generationModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rephrase correction — using raw answer.");
                    }

                    return new QueryResponse
                    {
                        Answer = finalAnswer,
                        IsFromCorrection = false,
                        Sources = new List<string> { "Appreciated Answer" },
                        Confidence = 0.95,
                        ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                        SessionId = dbSession.SessionId,
                        RelevantChunks = new List<RelevantChunk>(),
                        ModelsUsed = new Dictionary<string, string>
                        {
                            ["generation"] = generationModel,
                            ["embedding"] = embeddingModel
                        }
                    };
                }

                // Continue with rest of MEAI-specific logic...
                // Check topic change and context
                if (IsTopicChanged(question, context))
                {
                    _logger.LogInformation($"Topic changed for session {context.SessionId}, clearing context");
                    ClearContext(context);
                }

                var contextualQuery = BuildContextualQuery(question, context.History);

                // Get relevant chunks - ONLY for MEAI queries
                var relevantChunks = await GetRelevantChunksAsync(
                    contextualQuery, embModel, maxResults, meaiInfo, context, useReRanking, genModel);

                // Determine parent conversation ID for follow-ups
                int? parentId = null;
                if (context.History.Any())
                {
                    var lastConversationId = dbSession.Metadata.ContainsKey("lastConversationId")
                        ? Convert.ToInt32(dbSession.Metadata["lastConversationId"])
                        : (int?)null;

                    if (lastConversationId.HasValue && IsFollowUpQuestion(question, context))
                    {
                        parentId = lastConversationId.Value;
                    }
                }

                // Generate answer with MEAI context
                var answer = await GenerateChatResponseAsync(
                    question, genModel, context.History, relevantChunks, context, ismeai: meaiInfo);

                var answerEmbedding = await GetEmbeddingAsync(answer, embModel);

                // Rank chunks by similarity to answer
                var scoredChunks = new List<(RelevantChunk Chunk, double Similarity)>();
                foreach (var chunk in relevantChunks.Take(10))
                {
                    var chunkEmbedding = await GetEmbeddingAsync(chunk.Text, embModel);
                    var similarity = CosineSimilarity(answerEmbedding, chunkEmbedding);
                    chunk.Similarity = similarity;
                    scoredChunks.Add((chunk, similarity));
                }

                // Sort by similarity
                var topChunks = scoredChunks
                    .Where(c => c.Similarity >= 0.5)
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

                // Save conversation to database
                var conversationId = await SaveConversationToDatabase(
                    dbSession.SessionId, question, answer, topChunks,
                    genModel, embModel, confidence, stopwatch.ElapsedMilliseconds,
                    isFromCorrection: false, parentId, plant);

                // Update session metadata with last conversation ID
                dbSession.Metadata["lastConversationId"] = conversationId;
                await _conversationStorage.UpdateSessionAsync(dbSession);

                // Update in-memory context
                await UpdateConversationHistory(context, question, answer, relevantChunks);

                stopwatch.Stop();
                _metrics.RecordQueryProcessing(stopwatch.ElapsedMilliseconds, relevantChunks.Count, true);

                return new QueryResponse
                {
                    Answer = answer,
                    IsFromCorrection = false,
                    Sources = relevantChunks.Select(c => c.Source).Distinct().ToList(),
                    Confidence = confidence,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    RelevantChunks = topChunks,
                    SessionId = dbSession.SessionId,
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
        private async Task<string> RephraseWithLLMAsync(string originalAnswer, string modelName)
        {
            var systemPrompt = "You are a professional assistant. Rephrase the following content to make it sound polished, concise, and formal.";
            var userMessage = $"Rephrase this:\n\n\"{originalAnswer}\"";

            var messages = new[]
            {
        new { role = "system", content = systemPrompt },
        new { role = "user", content = userMessage }
    };

            var payload = new
            {
                model = modelName,
                messages = messages,
                stream = true, // Enable streaming
                temperature = 0.2
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") // Adjust path as needed
            {
                Content = JsonContent.Create(payload)
            };

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var finalContent = new StringBuilder();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("message", out var msgElem) &&
                        msgElem.TryGetProperty("content", out var contentElem))
                    {
                        finalContent.Append(contentElem.GetString());
                    }
                }
                catch
                {
                    // ignore bad JSON or keep logging
                }
            }

            return finalContent.ToString();
        }
        private async Task<int> SaveConversationToDatabase(
    string sessionId,
    string question,
    string answer,
    List<RelevantChunk> chunks,
    ModelConfiguration generationModel,
    ModelConfiguration embeddingModel,
    double confidence,
    long processingTimeMs,
    bool isFromCorrection,
    int? parentId,
    string plant)
        {
            try
            {
                var questionEmbedding = await GetEmbeddingAsync(question, embeddingModel);
                var answerEmbedding = await GetEmbeddingAsync(answer, embeddingModel);

                // Extract named entities from the answer
                var namedEntities = await ExtractEntitiesAsync(answer);

                // Determine topic tag (simple keyword-based approach)
                var topicTag = DetermineTopicTag(question, answer);

                var entry = new ConversationEntry
                {
                    SessionId = sessionId,
                    Question = question,
                    Answer = answer,
                    CreatedAt = DateTime.UtcNow,
                    QuestionEmbedding = questionEmbedding,
                    AnswerEmbedding = answerEmbedding,
                    NamedEntities = namedEntities,
                    WasAppreciated = false,
                    TopicTag = topicTag,
                    FollowUpToId = parentId,
                    GenerationModel = generationModel.Name,
                    EmbeddingModel = embeddingModel.Name,
                    Confidence = confidence,
                    ProcessingTimeMs = processingTimeMs,
                    RelevantChunksCount = chunks.Count,
                    Sources = chunks.Select(c => c.Source).Distinct().ToList(),
                    IsFromCorrection = isFromCorrection,
                    Plant = plant
                };

                await _conversationStorage.SaveConversationAsync(entry);

                _logger.LogInformation($"💾 Saved conversation {entry.Id} to database");
                return entry.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save conversation to database");
                return 0; // Return 0 if save fails
            }
        }
        // 🆕 Helper method to determine topic tags
        private string DetermineTopicTag(string question, string answer)
        {
            var lowerQuestion = question.ToLowerInvariant();
            var lowerAnswer = answer.ToLowerInvariant();
            var combinedText = $"{lowerQuestion} {lowerAnswer}";

            // HR Policy topic mapping
            var topicKeywords = new Dictionary<string, string[]>
            {
                ["leave_policy"] = new[] { "leave", "cl", "sl", "casual", "sick", "pto", "vacation", "holiday", "absence" },
                ["attendance"] = new[] { "attendance", "punctuality", "working hours", "shift", "late", "early" },
                ["payroll"] = new[] { "salary", "pay", "payroll", "bonus", "increment", "deduction", "tax" },
                ["benefits"] = new[] { "insurance", "medical", "health", "benefits", "reimbursement", "allowance" },
                ["performance"] = new[] { "appraisal", "performance", "review", "rating", "feedback", "kpi" },
                ["grievance"] = new[] { "complaint", "grievance", "issue", "problem", "dispute", "conflict" },
                ["training"] = new[] { "training", "development", "course", "certification", "skill", "learning" },
                ["policy_general"] = new[] { "policy", "rule", "regulation", "procedure", "guideline", "compliance" }
            };

            foreach (var topic in topicKeywords)
            {
                if (topic.Value.Any(keyword => combinedText.Contains(keyword)))
                {
                    return topic.Key;
                }
            }

            return "general";
        }
        // 🆕 Helper method to check if question is a follow-up
        private bool IsFollowUpQuestion(string question, ConversationContext context)
        {
            var followUpIndicators = new[]
            {
        "what about", "how about", "also", "and", "additionally", "furthermore",
        "he", "she", "it", "they", "this", "that", "same", "similar",
        "phir", "aur", "bhi", "uske", "uska", "iske", "agar"
    };

            var lowerQuestion = question.ToLowerInvariant();
            return followUpIndicators.Any(indicator => lowerQuestion.Contains(indicator));
        }
        public async Task MarkAppreciatedAsync(string sessionId, string question)
        {
            try
            {
                // Find the conversation in database
                var conversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);
                var conversation = conversations.LastOrDefault(c =>
                    c.Question.Equals(question, StringComparison.OrdinalIgnoreCase));

                if (conversation != null)
                {
                    await _conversationStorage.MarkAsAppreciatedAsync(conversation.Id);
                    _logger.LogInformation($"⭐ Marked conversation {conversation.Id} as appreciated in database");
                }

                // Also update in-memory cache for immediate use
                if (_sessionContexts.TryGetValue(sessionId, out var context))
                {
                    var turn = context.History.LastOrDefault(t =>
                        t.Question.Equals(question, StringComparison.OrdinalIgnoreCase));
                    if (turn != null)
                    {
                        var chunks = ConvertToRelevantChunks(context.RelevantChunks ?? new List<EmbeddingData>());
                        _appreciatedTurns.Add((turn.Question, turn.Answer, new List<RelevantChunk>(chunks)));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to mark conversation as appreciated: {question}");
            }
            finally
            {
                await LoadHistoricalAppreciatedAnswersAsync();
            }
        }
        // 🆕 New method to save corrections to database
        public async Task SaveCorrectionToDatabase(string sessionId, string question, string correctedAnswer)
        {
            try
            {
                var conversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);
                var conversation = conversations.LastOrDefault(c =>
                    c.Question.Equals(question, StringComparison.OrdinalIgnoreCase));

                if (conversation != null)
                {
                    await _conversationStorage.SaveCorrectionAsync(conversation.Id, correctedAnswer);
                    _logger.LogInformation($"✏️ Saved correction for conversation {conversation.Id}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save correction: {question}");
            }
        }
        // 🆕 New method to get conversation statistics
        public async Task<ConversationStats> GetConversationStatsAsync()
        {
            return await _conversationStorage.GetConversationStatsAsync();
        }
        // 🆕 New method to get appreciated answers for learning
        public async Task<List<ConversationEntry>> GetAppreciatedAnswersAsync(string? topicTag = null)
        {
            return await _conversationStorage.GetAppreciatedAnswersAsync(topicTag, limit: 100);
        }
        // 🆕 Method to initialize system with historical appreciated answers
        public async Task LoadHistoricalAppreciatedAnswersAsync()
        {
            try
            {
                var appreciatedAnswers = await GetAppreciatedAnswersAsync();

                foreach (var answer in appreciatedAnswers)
                {
                    var chunks = new List<RelevantChunk>(); // You might want to reconstruct these
                    _appreciatedTurns.Add((answer.Question, answer.Answer, chunks));
                }

                _logger.LogInformation($"📚 Loaded {appreciatedAnswers.Count} historical appreciated answers");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load historical appreciated answers");
            }
        }
        private async Task LoadCorrectionCacheAsync()
        {
            try
            {
                var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                var correctedEntries = await _conversationStorage.GetCorrectedConversationsAsync();

                lock (_lockObject)
                {
                    var newBag = new ConcurrentBag<CorrectionEntry>();

                    foreach (var entry in correctedEntries
                        .Where(c => !string.IsNullOrEmpty(c.QuestionEmbeddingJson))
                        .Select(c => new CorrectionEntry
                        {
                            Id = c.Id.ToString(),
                            Question = c.Question,
                            Answer = c.CorrectedAnswer!,
                            Embedding = c.QuestionEmbedding,
                            Model = c.GenerationModel,
                            Date = c.CreatedAt
                        }))
                    {
                        newBag.Add(entry);
                    }

                    // Instead of assigning (if _correctionsCache is readonly)
                    while (_correctionsCache.TryTake(out _)) ; // empty the original

                    foreach (var item in newBag)
                    {
                        _correctionsCache.Add(item); // repopulate it
                    }
                }



                _logger.LogInformation($"✅ Loaded {_correctionsCache.Count} corrections into cache");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load corrections into cache");
            }
        }
        List<RelevantChunk> ConvertToRelevantChunks(List<EmbeddingData> data)
        {
            return data.Select(d => new RelevantChunk
            {
                Text = d.Text,
                Source = d.SourceFile,
                Similarity = d.Similarity // if available in EmbeddingData
            }).ToList();
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
        //private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, ModelConfiguration embeddingModel, int maxResults)
        //{
        //    try
        //    {
        //        var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);
        //        if (string.IsNullOrEmpty(collectionId))
        //        {
        //            throw new InvalidOperationException($"No collection found for embedding model {embeddingModel.Name}");
        //        }

        //        _logger.LogInformation($"Searching with model {embeddingModel.Name} in collection {collectionId}");

        //        var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);

        //        var searchData = new
        //        {
        //            query_embeddings = new List<List<float>> { queryEmbedding },
        //            n_results = maxResults * 2,
        //            include = new[] { "documents", "metadatas", "distances" }
        //        };

        //        var response = await _chromaClient.PostAsJsonAsync(
        //            $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
        //            searchData);

        //        var responseContent = await response.Content.ReadAsStringAsync();
        //        response.EnsureSuccessStatusCode();

        //        using var doc = JsonDocument.Parse(responseContent);
        //        return ParseSearchResults(doc.RootElement, maxResults);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"ChromaDB search failed for model {embeddingModel.Name}");
        //        return new List<RelevantChunk>();
        //    }
        //}
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
    ConversationContext context,
    bool ismeai = true)
        {
            var messages = new List<object>();

            // Improved system prompt
            messages.Add(new
            {
                role = "system",
                content = ismeai ?
@"🧠 You are MEAI HR Policy Assistant — an expert advisor with deep knowledge of MEAI company HR policies.

🔑 DEFINITIONS — NEVER REINTERPRET:
• CL = Casual Leave (**never** 'Continuous Learning')
• SL = Sick Leave
• COFF = Compensatory Off
• EL = Earned Leave
• PL = Privilege Leave
• ML = Maternity Leave

📋 HOW TO STRUCTURE YOUR RESPONSE:
1. Use **all** relevant information from the provided policy excerpts.
2. Organize the answer with:
   - ✅ Clear headings  
   - 🔹 Bullet points  
   - 🕒 Timelines, procedures, and requirements
3. After each fact, **cite the source** clearly in brackets: `[DocumentName]`
4. If the information is **partial or missing**:
   - State what's available.
   - Recommend contacting HR for clarification.
5. Be **comprehensive** — do not ignore useful context.
6. Use a **professional, concise, and helpful tone**.

⚠️ IMPORTANT:
You must base your answers **only** on the provided official policy content. Do not assume, hallucinate, or fabricate information."

:
@"You are an intelligent assistant designed to provide accurate, complete, and helpful responses.

📌 INSTRUCTIONS:
1. Use only the context provided — do not assume or guess beyond it.
2. Focus on clarity and relevance in your answers.
3. If the context is missing something:
   - State clearly what is not available.
   - Avoid fabrication or speculation.
4. Structure responses where possible:
   - Headings for sections
   - Bullet points for clarity
   - Examples if appropriate
5. Maintain a neutral, professional, and informative tone.

⚠️ IMPORTANT:
Do not introduce external information or opinions. Stay strictly within the context provided."
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
                    foreach (var chunk in group.OrderByDescending(c => c.Similarity).Take(10))
                    {
                        contextBuilder.AppendLine($"• {chunk.Text.Trim()}");
                        contextBuilder.AppendLine($"  (Relevance: {chunk.Similarity:F2})");
                        contextBuilder.AppendLine();
                    }
                }

                messages.Add(new { role = "system", content = contextBuilder.ToString() });
            }

            question = ResolvePronouns(question, context);

            // Then add to messages
            messages.Add(new { role = "user", content = question });

            // Add current question
            messages.Add(new { role = "user", content = question });

            var safeOptions = new Dictionary<string, object>();
            foreach (var kvp in generationModel.ModelOptions)
            {
                object value = kvp.Value;

                Type t = value.GetType();
                if (kvp.Key == "num_ctx" && value is String d)
                    value = int.Parse(d);
                else if (kvp.Key == "top_p" && value is string e)
                    value = float.Parse(e);
                else if (kvp.Key == "temperature" && value is string f)
                    value = float.Parse(f);

                safeOptions[kvp.Key] = value;

            }

            var requestData = new
            {
                model = generationModel.Name,
                messages,
                temperature = generationModel.Temperature,
                stream = false,
                options = safeOptions
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/chat", requestData);
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Raw Ollama response: {Content}", responseContent);
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
        private Dictionary<string, object> CreateChunkMetadata(string sourceFile, DateTime lastModified, string modelName, string text, string plant)
        {
            var metadata = new Dictionary<string, object>
            {
                { "source_file", sourceFile },
                { "last_modified", lastModified.ToString("O") },
                { "model", modelName },
                { "chunk_size", text.Length },
                { "processed_at", DateTime.UtcNow.ToString("O") },
                { "processed_by", _currentUser },
                { "plant", plant }
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
                "", "",
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
        public static int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            var d = new int[s.Length + 1, t.Length + 1];

            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;

            for (int i = 1; i <= s.Length; i++)
            {
                for (int j = 1; j <= t.Length; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = new[] {
                d[i - 1, j] + 1,
                d[i, j - 1] + 1,
                d[i - 1, j - 1] + cost
            }.Min();
                }
            }

            return d[s.Length, t.Length];
        }
        private bool IsFollowUpPhraseFuzzy(string question, string[] knownPhrases, int maxDistance = 2)
        {
            string lowerQ = question.ToLowerInvariant();

            return knownPhrases.Any(phrase =>
            {
                if (lowerQ.Length < phrase.Length)
                    return false;

                var sub = lowerQ.Substring(0, Math.Min(phrase.Length + 3, lowerQ.Length));
                return LevenshteinDistance(sub, phrase) <= maxDistance;
            });
        }
        private string ResolvePronouns(string question, ConversationContext context)
        {
            if (context.NamedEntities.Count == 0) return question;

            string lastEntity = context.NamedEntities.Last();

            question = question.Replace("her", lastEntity, StringComparison.OrdinalIgnoreCase);
            question = question.Replace("his", lastEntity, StringComparison.OrdinalIgnoreCase);
            question = question.Replace("their", lastEntity, StringComparison.OrdinalIgnoreCase);
            return question;
        }
        private string BuildContextualQuery(string currentQuestion, List<ConversationTurn> history)
        {
            if (history.Count == 0) return currentQuestion;

            var contextualPhrases = new[]
{
    // Pronouns
    "he", "she", "him", "her", "they", "them", "his", "hers", "their", "theirs", "it", "its",

    // Demonstratives
    "this", "that", "those", "these",

    // Follow-up / connective words
    "also", "and", "but", "or", "then", "next", "after", "before", "furthermore", "moreover", "besides",

    // Question prompts
    "what about", "who else", "anything else", "how about", "can i also", "does it mean", "in that case",
    "how?", "why?", "when?", "where?", "what if?", "which one?", "how many?", "how long?", "what now?",

    // Anaphoric phrases
    "same", "as before", "previous one", "last one", "mentioned", "earlier", "above", "following that",
    "that one", "the same", "that case", "it again", "same thing", "another one", "one more",

    // Roles or objects
    "the person", "the policy", "the rule", "the regulation", "the requirement", "the document", "the clause",

    // Quantifiers
    "some", "any", "all", "none", "more", "less", "other", "another", "rest",

    // Clarifiers / corrections
    "not that", "actually", "i meant", "no, i mean", "what i meant was"
};

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
        private async Task<List<string>> ExtractEntitiesAsync(string text)
        {
            var prompt = $"Extract all named entities (people, organizations, titles, etc.) from the following text:\n\n\"{text}\"\n\nEntities:";

            var request = new
            {
                model = "mistral:latest", // or your current generation model
                messages = new[]
                {
            new { role = "user", content = prompt }
        },
                temperature = 0.1
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/chat", request);
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

                return content.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(e => e.Trim())
                              .Where(e => e.Length > 1)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract entities");
                return new List<string>();
            }
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
            // If meaiInfo is false, return empty chunks (no embeddings needed)
            if (!meaiInfo)
            {
                _logger.LogInformation("🚫 Skipping relevant chunks retrieval (meaiInfo = false)");
                return new List<RelevantChunk>();
            }

            try
            {
                // Check corrections first (only for MEAI queries)
                var correction = await CheckCorrectionsAsync(query);
                if (correction != null)
                    return new List<RelevantChunk>();

                // Perform ChromaDB search (only for MEAI queries)
                var chunks = await SearchChromaDBAsync(query, embeddingModel, maxResults);
                return chunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get relevant chunks");
                return new List<RelevantChunk>();
            }
        }
        private async Task UpdateConversationHistory(
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

                // Smart topic anchor update
                var currentTopics = ExtractKeyTopics(question);
                if (currentTopics.Any())
                {
                    // Update anchor only if this seems like a main topic question (not a follow-up)
                    var isMainTopic = !IsQuestionPatternContinuation(question, context) &&
                                     question.Split(' ').Length >= 4; // Substantial question

                    if (isMainTopic)
                    {
                        context.LastTopicAnchor = question;
                        _logger.LogDebug($"Updated topic anchor: {question}");
                    }
                }

                context.LastAccessed = DateTime.Now;

                // Extract entities as before
                var extracted = await ExtractEntitiesAsync(answer);
                foreach (var entity in extracted)
                {
                    if (!context.NamedEntities.Contains(entity, StringComparer.OrdinalIgnoreCase))
                        context.NamedEntities.Add(entity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update conversation history");
            }
        }
        private async Task<(string Answer, List<RelevantChunk> Chunks)?> CheckAppreciatedAnswerAsync(string question)
        {
            try
            {
                var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                var inputEmbedding = await GetEmbeddingAsync(question, embeddingModel);
                if (inputEmbedding == null || inputEmbedding.Count == 0)
                    return null;

                var matches = _appreciatedTurns
                    .Select(entry => new
                    {
                        entry.Answer,
                        entry.Chunks,
                        Similarity = CosineSimilarity(inputEmbedding, GetEmbeddingAsync(entry.Question, embeddingModel).Result)
                    })
                    .Where(x => x.Similarity >= 0.8) // Threshold can be tuned
                    .OrderByDescending(x => x.Similarity)
                    .ToList();

                if (matches.Any())
                {
                    var best = matches.First();
                    _logger.LogInformation($"✅ Appreciated answer match found for \"{question}\" with similarity {best.Similarity:F2}");
                    return (best.Answer, best.Chunks);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check appreciated match");
                return null;
            }
        }
        private TEntry? FindBestSemanticMatch<TEntry>(
    List<TEntry> entries,
    List<float> inputEmbedding,
    Func<TEntry, List<float>> embeddingSelector,
    double threshold,
    out double similarity)
        {
            similarity = 0;

            if (inputEmbedding == null || inputEmbedding.Count == 0)
                return default;

            var matches = entries
                .Where(e => embeddingSelector(e) is List<float> emb && emb.Count == inputEmbedding.Count)
                .Select(e => new
                {
                    Entry = e,
                    Similarity = CosineSimilarity(inputEmbedding, embeddingSelector(e))
                })
                .Where(x => x.Similarity >= threshold)
                .OrderByDescending(x => x.Similarity)
                .ToList();

            if (matches.Any())
            {
                similarity = matches.First().Similarity;
                return matches.First().Entry;
            }

            return default;
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
        public async Task SaveCorrectionAsync(string question, string correctAnswer, string model)
        {
            var modelConfig = await _modelManager.GetModelAsync(model);
            var embedding = await GetEmbeddingAsync(question, modelConfig);

            var entry = new CorrectionEntry
            {
                Question = question,
                Answer = correctAnswer,
                Embedding = embedding,
                Model = model,
                Date = DateTime.UtcNow
            };

            lock (_lockObject)
            {
                _correctionsCache.Add(entry);
            }

            _logger.LogInformation("✅ Correction saved for question: {Question}", question);
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
        private List<string> ExtractKeyTopics(string text)
        {
            var lowerText = text.ToLowerInvariant();
            var topics = new HashSet<string>();

            // Common topic indicators - domain nouns
            string[] topicKeywords = new[]
            {
        // General categories
        "names", "suggestions", "options", "ideas", "list", "examples", "types", "kinds",
        "methods", "ways", "approaches", "solutions", "strategies", "techniques", "tips",
        "advice", "recommendations", "guidelines", "rules", "policies", "procedures",
        "steps", "process", "information", "details", "facts", "data", "statistics",
        
        // Specific domains
        "leave", "policy", "salary", "benefits", "training", "attendance", "performance",
        "food", "recipe", "cooking", "health", "exercise", "medicine", "travel",
        "places", "locations", "books", "movies", "music", "technology", "software",
        "business", "career", "education", "learning", "courses", "skills"
    };

            // Extract topic keywords present in the text
            foreach (var keyword in topicKeywords)
            {
                if (lowerText.Contains(keyword))
                {
                    topics.Add(keyword);
                }
            }

            // Extract potential nouns (simple approach)
            var words = lowerText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                // Add words that are likely to be topic nouns (length > 3, not common words)
                if (word.Length > 3 && !IsCommonWord(word))
                {
                    topics.Add(word);
                }
            }

            return topics.ToList();
        }
        private bool IsCommonWord(string word)
        {
            string[] commonWords = new[]
            {
        "this", "that", "with", "have", "will", "from", "they", "know", "want",
        "been", "good", "much", "some", "time", "very", "when", "come", "here",
        "just", "like", "long", "make", "many", "over", "such", "take", "than",
        "them", "well", "were", "what", "your", "could", "should", "would",
        "about", "after", "again", "before", "being", "below", "between",
        "during", "further", "having", "other", "since", "through", "under",
        "until", "while", "above", "against", "because", "down", "into", "off",
        "once", "only", "same", "there", "these", "those", "where", "which",
        "more", "most", "each", "few", "how", "its", "may", "no", "not", "now",
        "old", "see", "two", "way", "who", "boy", "did", "has", "let", "put",
        "too", "use"
    };

            return commonWords.Contains(word.ToLowerInvariant());
        }
        private bool IsTopicChanged(string question, ConversationContext context)
        {
            if (string.IsNullOrWhiteSpace(question)) return true;

            var lowerQuestion = question.ToLowerInvariant();

            // 1. Universal continuation indicators
            string[] universalContinuation = new[]
            {
        // Direct continuation requests
        "more", "other", "different", "additional", "extra", "another", "else",
        "further", "continue", "next", "also", "too", "as well", "besides",
        
        // Modification requests  
        "but", "however", "though", "although", "instead", "rather", "better",
        "alternative", "similar", "like", "unlike", "compared", "versus",
        
        // Expansion requests
        "tell me more", "give me more", "show me more", "any other", "what other",
        "can you", "could you", "would you", "please", "help me", "suggest",
        
        // Clarification/Follow-up
        "what about", "how about", "what if", "suppose", "assuming", "given",
        "in case", "regarding", "concerning", "about", "related", "same",
        
        // Pronouns (strong continuation indicators)
        "it", "this", "that", "these", "those", "they", "them", "he", "she",
        "his", "her", "their", "its"
    };

            // 2. Check for universal continuation phrases
            if (universalContinuation.Any(phrase => lowerQuestion.Contains(phrase)))
            {
                _logger.LogDebug($"Universal continuation detected: {question}");
                return false;
            }

            // 3. Smart topic overlap detection
            if (context.History.Any())
            {
                var currentTopics = ExtractKeyTopics(question);
                var lastQuestion = context.History.Last().Question;
                var lastTopics = ExtractKeyTopics(lastQuestion);

                // Calculate topic overlap
                var commonTopics = currentTopics.Intersect(lastTopics, StringComparer.OrdinalIgnoreCase).ToList();
                var overlapRatio = commonTopics.Count > 0 ?
                    (double)commonTopics.Count / Math.Max(currentTopics.Count, lastTopics.Count) : 0;

                if (overlapRatio >= 0.3) // 30% topic overlap indicates same domain
                {
                    _logger.LogDebug($"Topic overlap detected ({overlapRatio:P0}): {string.Join(", ", commonTopics)}");
                    return false;
                }
            }

            // 4. Semantic similarity with conversation history
            if (context.History.Count > 0)
            {
                var recentQuestions = context.History.TakeLast(3).Select(h => h.Question).ToList();

                foreach (var recentQ in recentQuestions)
                {
                    double similarity = CalculateAdvancedSimilarity(question, recentQ);
                    if (similarity >= 0.25) // Lower threshold for better continuity
                    {
                        _logger.LogDebug($"Semantic similarity detected ({similarity:P0}) with: {recentQ}");
                        return false;
                    }
                }
            }

            // 5. Pattern-based continuation detection
            if (IsQuestionPatternContinuation(question, context))
            {
                return false;
            }

            // 6. Use topic anchor (last resort)
            var anchor = context.LastTopicAnchor ?? "";
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                double anchorSim = CalculateAdvancedSimilarity(question, anchor);
                if (anchorSim >= 0.2)
                {
                    _logger.LogDebug($"Topic anchor similarity ({anchorSim:P0}): {anchor}");
                    return false;
                }
            }

            // 7. Default: Topic changed
            _logger.LogDebug($"Topic change detected for: {question}");
            return true;
        }
        private double CalculateAdvancedSimilarity(string text1, string text2)
        {
            // Jaccard similarity for word overlap
            var words1 = text1.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !IsCommonWord(w))
                .ToHashSet();

            var words2 = text2.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !IsCommonWord(w))
                .ToHashSet();

            if (!words1.Any() || !words2.Any()) return 0;

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            var jaccardSim = (double)intersection / union;

            // Boost similarity if question structures are similar
            var structuralBoost = GetStructuralSimilarity(text1, text2);

            return Math.Min(1.0, jaccardSim + structuralBoost);
        }
        private double GetStructuralSimilarity(string q1, string q2)
        {
            var q1Lower = q1.ToLowerInvariant().Trim();
            var q2Lower = q2.ToLowerInvariant().Trim();

            // Question starters
            string[] questionStarters = { "what", "how", "can", "could", "would", "tell", "give", "show" };

            var q1Starter = questionStarters.FirstOrDefault(s => q1Lower.StartsWith(s));
            var q2Starter = questionStarters.FirstOrDefault(s => q2Lower.StartsWith(s));

            if (q1Starter != null && q1Starter == q2Starter)
            {
                return 0.1; // Small boost for same question pattern
            }

            return 0;
        }
        private bool IsQuestionPatternContinuation(string question, ConversationContext context)
        {
            if (!context.History.Any()) return false;

            var lowerQ = question.ToLowerInvariant().Trim();

            // Patterns that usually indicate continuation
            string[] continuationPatterns = new[]
            {
        // Request patterns
        @"^(can|could|would|will) you (tell|give|show|suggest|recommend)",
        @"^(tell|give|show|suggest) me (more|other|different|some)",
        @"^(what|which) (other|else|more|about)",
        @"^(any|some) (other|more|different)",
        
        // Comparative patterns  
        @"^(but|however|instead|rather) ",
        @"little (different|more)",
        @"bit (different|more)",
        @"something (else|different|more)",
        
        // Follow-up patterns
        @"^(also|too|as well)",
        @"^(and|plus|additionally)",
        @"^(or|maybe|perhaps)",
    };

            foreach (var pattern in continuationPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lowerQ, pattern))
                {
                    _logger.LogDebug($"Continuation pattern matched: {pattern}");
                    return true;
                }
            }

            return false;
        }
        private async Task<QueryResponse> ProcessNonMeaiQueryFast(
            string question,
            string? sessionId,
            string? generationModel,
            Stopwatch stopwatch)
        {
            try
            {
                _logger.LogInformation("🚀 Processing NON-MEAI query (skipping embeddings but checking corrections)");

                // Get session (lightweight)
                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(), _currentUser);

                var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);

                // Handle history clear request
                if (IsHistoryClearRequest(question))
                {
                    return await HandleHistoryClearRequest(context, dbSession.SessionId);
                }

                // 🆕 CHECK FOR CORRECTIONS (even in non-MEAI mode)
                var correction = await CheckCorrectionsAsync(question);
                if (correction != null)
                {
                    _logger.LogInformation($"🎯 Using correction for NON-MEAI question: {question}");

                    string finalAnswer = correction.Answer;
                    try
                    {
                        finalAnswer = await RephraseWithLLMAsync(correction.Answer, generationModel ?? _config.DefaultGenerationModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rephrase correction — using raw answer.");
                    }

                    // Save correction usage to database (optional - for tracking)
                    try
                    {
                        var genModel = await _modelManager.GetModelAsync(generationModel ?? _config.DefaultGenerationModel);
                        await SaveNonMeaiConversationToDatabase(
                            dbSession.SessionId, question, finalAnswer,
                            genModel, 1.0, stopwatch.ElapsedMilliseconds,
                            isFromCorrection: true, "General");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save non-MEAI correction to database");
                    }

                    stopwatch.Stop();

                    return new QueryResponse
                    {
                        Answer = finalAnswer,
                        IsFromCorrection = true,
                        Sources = new List<string> { "User Correction" },
                        Confidence = 1.0,
                        ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                        RelevantChunks = new List<RelevantChunk>(),
                        SessionId = dbSession.SessionId,
                        ModelsUsed = new Dictionary<string, string>
                        {
                            ["generation"] = generationModel ?? _config.DefaultGenerationModel,
                            ["embedding"] = "skipped"
                        }
                    };
                }

                // Lightweight topic change detection (no embeddings)
                if (IsTopicChangedLightweight(question, context))
                {
                    _logger.LogInformation($"Topic changed for session {context.SessionId}, clearing context");
                    ClearContext(context);
                }

                // Generate response directly (no policy context, no embeddings)
                var answer = await GenerateNonMeaiChatResponseAsync(question, generationModel, context.History);

                // Save conversation to database (optional - for learning)
                try
                {
                    var genModel = await _modelManager.GetModelAsync(generationModel ?? _config.DefaultGenerationModel);
                    await SaveNonMeaiConversationToDatabase(
                        dbSession.SessionId, question, answer,
                        genModel, 0.8, stopwatch.ElapsedMilliseconds,
                        isFromCorrection: false, "General");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save non-MEAI conversation to database");
                }

                // Lightweight conversation tracking
                await UpdateConversationHistoryLightweight(context, question, answer);

                stopwatch.Stop();

                _logger.LogInformation($"✅ NON-MEAI query completed in {stopwatch.ElapsedMilliseconds}ms");

                return new QueryResponse
                {
                    Answer = answer,
                    IsFromCorrection = false,
                    Sources = new List<string> { "General Knowledge" },
                    Confidence = 0.8,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    RelevantChunks = new List<RelevantChunk>(),
                    SessionId = dbSession.SessionId,
                    ModelsUsed = new Dictionary<string, string>
                    {
                        ["generation"] = generationModel ?? _config.DefaultGenerationModel,
                        ["embedding"] = "skipped"
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fast non-MEAI query processing failed");
                throw;
            }
        }
        // ADD THIS METHOD - Chat response generator for non-MEAI queries
        private async Task<string> GenerateNonMeaiChatResponseAsync(
            string question,
            string? modelName,
            List<ConversationTurn> history)
        {
            var messages = new List<object>();

            // System prompt for general conversation (NO policy context)
            messages.Add(new
            {
                role = "system",
                content = @"You are a helpful and knowledgeable AI assistant.

🎯 INSTRUCTIONS:
1. Provide accurate, helpful, and engaging responses on any topic
2. Be conversational and natural in your tone  
3. For general knowledge questions, use your training knowledge
4. For suggestions/recommendations, provide practical and useful options
5. Keep responses well-structured and informative
6. If you don't know something, say so honestly
7. You can discuss any topic - you are NOT limited to any specific domain

✨ Be helpful, friendly, and informative!"
            });

            // Add recent conversation history (limited for speed)
            foreach (var turn in history.TakeLast(6))
            {
                messages.Add(new { role = "user", content = turn.Question });
                messages.Add(new { role = "assistant", content = turn.Answer });
            }

            // Add current question
            messages.Add(new { role = "user", content = question });

            var requestData = new
            {
                model = modelName ?? _config.DefaultGenerationModel,
                messages,
                temperature = 0.7,
                stream = false,
                options = new Dictionary<string, object>
        {
            { "num_ctx", 4096 }, // Reduced context for faster processing
            { "top_p", 0.9 }
        }
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/chat", requestData);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

                return content.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Non-MEAI chat generation failed");
                return "I apologize, but I'm having trouble generating a response right now. Please try again.";
            }
        }
        private bool IsTopicChangedLightweight(string question, ConversationContext context)
        {
            if (!context.History.Any()) return false;

            var lowerQuestion = question.ToLowerInvariant();

            // Quick continuation check
            string[] quickContinuation = { "more", "other", "different", "also", "and", "but" };
            if (quickContinuation.Any(word => lowerQuestion.Contains(word)))
                return false;

            // Simple word overlap with last question
            if (context.History.Any())
            {
                var lastQ = context.History.Last().Question.ToLowerInvariant();
                var words1 = question.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var words2 = lastQ.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var overlap = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();

                if (overlap >= 2) return false; // At least 2 common words = same topic
            }

            return true; // Default to changed for general queries
        }
        // 6. ADD THIS METHOD - Lightweight conversation update
        private async Task UpdateConversationHistoryLightweight(
            ConversationContext context,
            string question,
            string answer)
        {
            try
            {
                var turn = new ConversationTurn
                {
                    Question = question,
                    Answer = answer,
                    Timestamp = DateTime.Now,
                    Sources = new List<string>()
                };

                context.History.Add(turn);
                if (context.History.Count > 10)
                {
                    context.History = context.History.TakeLast(10).ToList();
                }

                context.LastAccessed = DateTime.Now;
                // Skip entity extraction for general queries
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update conversation history (lightweight)");
            }
        }
        private async Task<int> SaveNonMeaiConversationToDatabase(
    string sessionId,
    string question,
    string answer,
    ModelConfiguration generationModel,
    double confidence,
    long processingTimeMs,
    bool isFromCorrection,
    string plant)
        {
            try
            {
                // For non-MEAI queries, we skip embedding generation but still save conversation
                var entry = new ConversationEntry
                {
                    SessionId = sessionId,
                    Question = question,
                    Answer = answer,
                    CreatedAt = DateTime.UtcNow,
                    QuestionEmbedding = new List<float>(), // Empty for non-MEAI
                    AnswerEmbedding = new List<float>(),   // Empty for non-MEAI
                    NamedEntities = new List<string>(),    // Skip entity extraction for speed
                    WasAppreciated = false,
                    TopicTag = "general", // Simple tag for non-MEAI queries
                    FollowUpToId = null,
                    GenerationModel = generationModel.Name,
                    EmbeddingModel = "none", // No embedding model used
                    Confidence = confidence,
                    ProcessingTimeMs = processingTimeMs,
                    RelevantChunksCount = 0,
                    Sources = isFromCorrection ? new List<string> { "User Correction" } : new List<string> { "General Knowledge" },
                    IsFromCorrection = isFromCorrection,
                    Plant = plant
                };

                await _conversationStorage.SaveConversationAsync(entry);

                _logger.LogInformation($"💾 Saved non-MEAI conversation {entry.Id} to database");
                return entry.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save non-MEAI conversation to database");
                return 0;
            }
        }
     public async Task ApplyCorrectionAsync(string sessionId, string question, string correctedAnswer, string model)
        {
            try
            {
                // Find the conversation in database
                var conversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);
                var conversation = conversations.LastOrDefault(c =>
                    c.Question.Equals(question, StringComparison.OrdinalIgnoreCase));

                if (conversation != null)
                {
                    await _conversationStorage.SaveCorrectionAsync(conversation.Id, correctedAnswer);
                    _logger.LogInformation($"✏️ Saved correction for conversation {conversation.Id} in database");
                }

                // Update in-memory corrections cache
                if (_sessionContexts.TryGetValue(sessionId, out var context))
                {
                    var turn = context.History.LastOrDefault(t =>
                        t.Question.Equals(question, StringComparison.OrdinalIgnoreCase));
                    if (turn != null)
                    {
                        // 🆕 For non-MEAI corrections, we can use a simpler approach
                        List<float> inputEmbedding;
                        try
                        {
                            // Try to generate embedding for better matching
                            var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                            inputEmbedding = await GetEmbeddingAsync(question, embeddingModel!);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to generate embedding for correction, using empty embedding");
                            // Fallback: use empty embedding (will still work for exact text matches)
                            inputEmbedding = new List<float>();
                        }

                        var correctionEntry = new CorrectionEntry
                        {
                            Question = turn.Question,
                            Answer = correctedAnswer,
                            Model = model,
                            Date = DateTime.Now,
                            Embedding = inputEmbedding,
                            Id = sessionId
                        };

                        _correctionsCache.Add(correctionEntry);
                        _logger.LogInformation($"✅ Added correction to cache: {question}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply correction: {question}");
            }
            finally
            {
                await LoadCorrectionCacheAsync();
            }
        }
        // UPDATE CheckCorrectionsAsync to work with both MEAI and non-MEAI queries
     private async Task<CorrectionEntry?> CheckCorrectionsAsync(string question)
        {
            try
            {
                List<float> inputEmbedding = new List<float>();

                // Try to generate embedding for semantic matching (if embedding model available)
                try
                {
                    var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                    if (embeddingModel != null)
                    {
                        inputEmbedding = await GetEmbeddingAsync(question, embeddingModel);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not generate embedding for correction check, falling back to text matching");
                }

                List<(CorrectionEntry Entry, double Similarity)> matches = new List<(CorrectionEntry, double)>();

                lock (_lockObject)
                {
                    foreach (var correction in _correctionsCache)
                    {
                        double similarity = 0;

                        // Method 1: Semantic similarity (if embeddings available)
                        if (inputEmbedding.Count > 0 &&
                            correction.Embedding != null &&
                            correction.Embedding.Count == inputEmbedding.Count)
                        {
                            similarity = CosineSimilarity(inputEmbedding, correction.Embedding);
                        }
                        // Method 2: Fallback to text similarity
                        else
                        {
                            similarity = CalculateTextSimilarity(question.ToLowerInvariant(), correction.Question.ToLowerInvariant());
                        }

                        // Accept matches with similarity >= 0.75 for semantic, >= 0.8 for text-only
                        double threshold = (inputEmbedding.Count > 0 && correction.Embedding?.Count > 0) ? 0.75 : 0.8;

                        if (similarity >= threshold)
                        {
                            matches.Add((correction, similarity));
                        }
                    }
                }

                if (matches.Any())
                {
                    var bestMatch = matches.OrderByDescending(x => x.Similarity).First();
                    _logger.LogInformation($"✅ Correction match found: \"{bestMatch.Entry.Question}\" with similarity {bestMatch.Similarity:F2}");
                    return bestMatch.Entry;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check correction match");
                return null;
            }
        }
        // OPTIONAL: Add method to get non-MEAI conversation stats
     public async Task<NonMeaiConversationStats> GetNonMeaiConversationStatsAsync()
        {
            try
            {
                // Optionally, get overall stats if needed
                var stats = await _conversationStorage.GetConversationStatsAsync();

                // Pull all conversations with EmbeddingModel == "none"
                var nonMeaiConversations = await _conversationStorage.GetConversationAsync("none", 1000);

                return new NonMeaiConversationStats
                {
                    TotalNonMeaiConversations = nonMeaiConversations.Count,
                    NonMeaiCorrections = nonMeaiConversations.Count(c => c.IsFromCorrection),
                    NonMeaiAppreciated = nonMeaiConversations.Count(c => c.WasAppreciated),
                    AverageProcessingTime = nonMeaiConversations.Any() ?
                        nonMeaiConversations.Average(c => c.ProcessingTimeMs) : 0,
                    TopGeneralTopics = nonMeaiConversations
                        .Where(c => !string.IsNullOrEmpty(c.TopicTag))
                        .GroupBy(c => c.TopicTag)
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .ToDictionary(g => g.Key!, g => g.Count())
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get non-MEAI conversation stats");
                return new NonMeaiConversationStats();
            }
        }
        // ADD THIS CLASS for non-MEAI stats
     public class NonMeaiConversationStats
        {
            public int TotalNonMeaiConversations { get; set; }
            public int NonMeaiCorrections { get; set; }
            public int NonMeaiAppreciated { get; set; }
            public double AverageProcessingTime { get; set; }
            public Dictionary<string, int> TopGeneralTopics { get; set; } = new();
        }


        private async Task<List<List<float>>> GetEmbeddingsBatchAsync(
        List<string> texts,
        ModelConfiguration model,
        int batchSize = 10)
        {
            var results = new List<List<float>>();

            // Process in parallel batches
            var batches = texts.Chunk(batchSize);
            var tasks = new List<Task<List<List<float>>>>();

            foreach (var batch in batches)
            {
                tasks.Add(ProcessEmbeddingBatchAsync(batch.ToList(), model));
            }

            var batchResults = await Task.WhenAll(tasks);

            foreach (var batchResult in batchResults)
            {
                results.AddRange(batchResult);
            }

            return results;
        }

        private async Task<List<List<float>>> ProcessEmbeddingBatchAsync(
    List<string> texts,
    ModelConfiguration model)
        {
            var embeddings = new List<List<float>>();

            // Use semaphore to limit concurrent requests
            using var semaphore = new SemaphoreSlim(3, 3); // Max 3 concurrent requests

            var tasks = texts.Select(async text =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await GetEmbeddingAsync(text, model);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        private readonly ConcurrentDictionary<string, List<float>> _embeddingCache = new();
        private readonly SemaphoreSlim _embeddingSemaphore = new(5, 5); // Limit concurrent embedding requests

        private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<float>();

            // Generate cache key
            var cacheKey = $"{model.Name}:{text.GetHashCode():X}";

            // Check cache first
            if (_embeddingCache.TryGetValue(cacheKey, out var cachedEmbedding))
            {
                return cachedEmbedding;
            }

            await _embeddingSemaphore.WaitAsync();
            try
            {
                // Double-check cache after acquiring semaphore
                if (_embeddingCache.TryGetValue(cacheKey, out cachedEmbedding))
                {
                    return cachedEmbedding;
                }

                // Optimize text length based on model
                var maxLength = model.MaxContextLength * 3;
                if (text.Length > maxLength)
                    text = text.Substring(0, maxLength);

                var request = new
                {
                    model = model.Name,
                    prompt = text,
                    options = CreateOptimizedModelOptions(model)
                };

                // Use optimized HTTP client settings
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Embedding request failed: {response.StatusCode}");
                    return new List<float>();
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                {
                    var embedding = embeddingProperty.EnumerateArray()
                        .Select(x => x.GetSingle())
                        .ToList();

                    // Cache the result (with size limit)
                    if (_embeddingCache.Count < 10000) // Prevent memory bloat
                    {
                        _embeddingCache.TryAdd(cacheKey, embedding);
                    }

                    return embedding;
                }

                return new List<float>();
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Embedding request timed out for model {model.Name}");
                return new List<float>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Embedding generation failed for model {model.Name}");
                return new List<float>();
            }
            finally
            {
                _embeddingSemaphore.Release();
            }
        }

        private Dictionary<string, object> CreateOptimizedModelOptions(ModelConfiguration model)
        {
            var options = new Dictionary<string, object>();

            foreach (var kvp in model.ModelOptions)
            {
                object value = kvp.Value;

                // Convert string values to proper types
                switch (kvp.Key)
                {
                    case "num_ctx":
                        value = value is string s1 ? Math.Min(int.Parse(s1), 2048) : value; // Limit context
                        break;
                    case "top_p":
                        value = value is string s2 ? float.Parse(s2) : value;
                        break;
                    case "temperature":
                        value = value is string s3 ? Math.Min(float.Parse(s3), 0.1f) : value; // Low temp for embeddings
                        break;
                }

                options[kvp.Key] = value;
            }

            // Add embedding-specific optimizations
            options["num_predict"] = 1; // Embeddings don't need text generation
            options["repeat_penalty"] = 1.0f;

            return options;
        }

        private async Task ProcessChunkBatchForModelAsync(
    List<(string Text, string SourceFile)> chunks,
    ModelConfiguration model,
    string collectionId,
    DateTime lastModified,
    string plant)
        {
            try
            {
                // Filter and prepare chunks in parallel
                var validChunks = chunks
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                    .Select(chunk => new
                    {
                        Text = CleanText(chunk.Text),
                        SourceFile = chunk.SourceFile,
                        ChunkId = GenerateChunkId(chunk.SourceFile, chunk.Text, lastModified, model.Name)
                    })
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                    .ToList();

                if (!validChunks.Any()) return;

                // Check which chunks already exist (batch check)
                var existingChunks = await CheckExistingChunksAsync(collectionId, validChunks.Select(c => c.ChunkId).ToList());
                var newChunks = validChunks.Where(c => !existingChunks.Contains(c.ChunkId)).ToList();

                if (!newChunks.Any())
                {
                    _logger.LogDebug($"All chunks already exist for {model.Name}");
                    return;
                }

                // Generate embeddings in batches
                var texts = newChunks.Select(c => c.Text).ToList();
                var embeddings = await GetEmbeddingsBatchAsync(texts, model, batchSize: 5);

                // Prepare data for ChromaDB
                var documents = new List<string>();
                var metadatas = new List<Dictionary<string, object>>();
                var ids = new List<string>();
                var validEmbeddings = new List<List<float>>();

                for (int i = 0; i < newChunks.Count && i < embeddings.Count; i++)
                {
                    if (embeddings[i].Count == 0) continue;

                    documents.Add(newChunks[i].Text);
                    ids.Add(newChunks[i].ChunkId);
                    validEmbeddings.Add(embeddings[i]);

                    var metadata = CreateChunkMetadata(newChunks[i].SourceFile, lastModified, model.Name, newChunks[i].Text, plant);
                    metadatas.Add(metadata);
                }

                if (validEmbeddings.Any())
                {
                    await AddToChromaDBAsync(collectionId, ids, validEmbeddings, documents, metadatas);
                    _logger.LogInformation($"✅ Added {validEmbeddings.Count} new chunks for model {model.Name}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process chunk batch for model {model.Name}");
            }
        }
        private async Task<HashSet<string>> CheckExistingChunksAsync(string collectionId, List<string> chunkIds)
        {
            try
            {
                var batchSize = 100;
                var existingIds = new HashSet<string>();

                for (int i = 0; i < chunkIds.Count; i += batchSize)
                {
                    var batch = chunkIds.Skip(i).Take(batchSize).ToList();

                    var request = new
                    {
                        ids = batch,
                        include = new[] { "metadatas" }
                    };

                    var response = await _chromaClient.PostAsJsonAsync(
                        $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/get",
                        request);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);

                        if (doc.RootElement.TryGetProperty("ids", out var idsArray))
                        {
                            foreach (var id in idsArray.EnumerateArray())
                            {
                                var idStr = id.GetString();
                                if (!string.IsNullOrEmpty(idStr))
                                    existingIds.Add(idStr);
                            }
                        }
                    }
                }

                return existingIds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check existing chunks");
                return new HashSet<string>();
            }
        }
        private readonly ConcurrentDictionary<string, (List<RelevantChunk> Results, DateTime Timestamp)> _searchCache = new();
        private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, ModelConfiguration embeddingModel, int maxResults)
        {
            var cacheKey = $"{query.GetHashCode():X}:{embeddingModel.Name}:{maxResults}";

            // Check cache (5-minute TTL)
            if (_searchCache.TryGetValue(cacheKey, out var cached) &&
                DateTime.Now - cached.Timestamp < TimeSpan.FromMinutes(5))
            {
                _logger.LogDebug("Using cached search results");
                return cached.Results;
            }

            try
            {
                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);
                if (string.IsNullOrEmpty(collectionId))
                {
                    return new List<RelevantChunk>();
                }

                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);
                if (queryEmbedding.Count == 0)
                {
                    return new List<RelevantChunk>();
                }

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = Math.Min(maxResults * 2, 50), // Limit results
                    include = new[] { "documents", "metadatas", "distances" },
                    where = new Dictionary<string, object>() // Add filters if needed
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"ChromaDB search failed: {response.StatusCode}");
                    return new List<RelevantChunk>();
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseContent);
                var results = ParseSearchResults(doc.RootElement, maxResults);

                // Cache results
                if (_searchCache.Count < 1000) // Prevent memory bloat
                {
                    _searchCache.TryAdd(cacheKey, (results, DateTime.Now));
                }

                return results;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("ChromaDB search timed out");
                return new List<RelevantChunk>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ChromaDB search failed for model {embeddingModel.Name}");
                return new List<RelevantChunk>();
            }
        }
        public static void ConfigureOptimizedHttpClient(IServiceCollection services)
        {
            services.AddHttpClient("OllamaAPI", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
            {
                MaxConnectionsPerServer = 10,
                UseCookies = false
            });

            services.AddHttpClient("ChromaDB", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
            {
                MaxConnectionsPerServer = 20,
                UseCookies = false
            });
        }
        public async Task WarmUpEmbeddingsAsync()
        {
            try
            {
                _logger.LogInformation("🔥 Starting embedding warm-up");

                var models = await _modelManager.DiscoverAvailableModelsAsync();
                var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();

                var warmUpTexts = new[]
                {
            "Sample text for warm-up",
            "Another sample for model initialization",
            "HR policy warm-up text"
        };

                var tasks = embeddingModels.Select(async model =>
                {
                    try
                    {
                        foreach (var text in warmUpTexts)
                        {
                            await GetEmbeddingAsync(text, model);
                        }
                        _logger.LogInformation($"✅ Warmed up model: {model.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to warm up model: {model.Name}");
                    }
                });

                await Task.WhenAll(tasks);
                _logger.LogInformation("🔥 Embedding warm-up completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding warm-up failed");
            }
        }

    }
}
