// Services/DynamicRagService.cs
using DocumentFormat.OpenXml.InkML;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
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
    public static class StringExtensions
    {
        public static string ToTitleCase(this string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return char.ToUpper(input[0]) + input.Substring(1).ToLower();
        }
    }
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
        public bool _isInitialized = false;
        private readonly object _lock = new();
        private static readonly ConcurrentBag<(string Question, string Answer, List<RelevantChunk> Chunks)> _appreciatedTurns = new();
        private readonly PlantSettings _plants;
        private readonly IConversationStorageService _conversationStorage;
        private readonly AbbreviationExpansionService _abbreviationService;
       // NEW: Single embedding cache with better management
        private readonly ConcurrentDictionary<string, (List<float> Embedding, DateTime Cached)> _optimizedEmbeddingCache = new();
        private readonly SemaphoreSlim _globalEmbeddingSemaphore = new(3, 3); // Allow 3 concurrent embedding requests


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
            IConversationStorageService conversationStorage,
            AbbreviationExpansionService abbreviationService)
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
            _abbreviationService = abbreviationService;

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

            // Plant-specific policies
            var plantSpecificPath = Path.Combine(_chromaOptions.PolicyFolder, plant);
            if (Directory.Exists(plantSpecificPath))
            {
                policyFiles.AddRange(Directory.GetFiles(plantSpecificPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => _chromaOptions.SupportedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant())));

                _logger.LogInformation($"📁 Found {policyFiles.Count} plant-specific files for {plant}");
            }

            // Centralized policies
            var centralizedPath = Path.Combine(_chromaOptions.PolicyFolder, "Centralized");
            if (Directory.Exists(centralizedPath))
            {
                var centralizedFiles = Directory.GetFiles(centralizedPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => _chromaOptions.SupportedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()));

                policyFiles.AddRange(centralizedFiles);
                _logger.LogInformation($"📁 Found {centralizedFiles.Count()} centralized policy files");
            }

            // Context files (apply to all plants)
            if (Directory.Exists(_chromaOptions.ContextFolder))
            {
                policyFiles.AddRange(Directory.GetFiles(_chromaOptions.ContextFolder, "*.txt", SearchOption.AllDirectories));
            }

            _logger.LogInformation($"📋 Total files for {plant}: {policyFiles.Count}");
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
                var relevantChunks = await GetRelevantChunksWithExpansionAsync(
                                    contextualQuery, embModel, maxResults, meaiInfo, context, useReRanking, genModel, plant);


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
                            question, genModel, context.History, relevantChunks, context, ismeai: meaiInfo, plant: plant);


                var answerEmbedding = await GetEmbeddingAsync(answer, embModel);

                // Rank chunks by similarity to answer
                var scoredChunks = new List<(RelevantChunk Chunk, double Similarity)>();
                foreach (var chunk in relevantChunks.Where(x=>x.Similarity > 0.5))
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

                var hasSufficientCoverage = CheckPolicyCoverage(relevantChunks, question);

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
                    },
                    Plant = plant,
                    HasSufficientPolicyCoverage = hasSufficientCoverage
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Query processing failed for session {SessionId}", sessionId);
                _metrics.RecordQueryProcessing(stopwatch.ElapsedMilliseconds, 0, false);
                _logger.LogError(ex, "Query processing failed for session {SessionId} and plant {Plant}", sessionId, plant);
                throw new RAGServiceException($"Failed to process query for plant {plant}", ex);
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
                if (embeddingModel == null)
                {
                    _logger.LogError($"❌ Embedding model '{_config.DefaultEmbeddingModel}' not found!");
                    return;
                }
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
                Similarity = d.Similarity, // if available in EmbeddingData
                Embedding = d.Vector // if available in EmbeddingData
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

        private async Task<string> GenerateChatResponseAsync(
    string question,
    ModelConfiguration generationModel,
    List<ConversationTurn> history,
    List<RelevantChunk> chunks,
    ConversationContext context,
    bool ismeai = true,
    string plant = "")
        {
            var messages = new List<object>();

            // OPTIMIZED: Quick coverage check first
            var hasSufficientCoverage = CheckPolicyCoverage(chunks, question);

            // Early return for insufficient coverage
            if (ismeai && !hasSufficientCoverage)
            {
                _logger.LogWarning($"⚠️ Insufficient policy coverage for {plant}, returning fallback message");
                return $"I don't have sufficient policy information to answer this question for {plant}. Please contact your supervisor or HR department for clarification on this matter.";
            }

            // OPTIMIZED: Simplified system prompt
            messages.Add(new
            {
                role = "system",
                content = ismeai ?
                    BuildMeaiSystemPrompt(plant) :
                    BuildGeneralSystemPrompt()
            });

            // OPTIMIZED: Limited conversation history (reduce token usage)
            foreach (var turn in history.TakeLast(4)) // Reduced from 6
            {
                messages.Add(new { role = "user", content = TruncateText(turn.Question, 200) });
                messages.Add(new { role = "assistant", content = TruncateText(turn.Answer, 300) });
            }

            // OPTIMIZED: Build context only when needed and more efficiently
            if (chunks.Any() && ismeai && hasSufficientCoverage)
            {
                var contextContent = BuildOptimizedContext(chunks, plant);
                messages.Add(new { role = "system", content = contextContent });
            }

            // Add current question
            question = ResolvePronouns(question, context);
            messages.Add(new { role = "user", content = question });

            // OPTIMIZED: Request configuration
            var requestData = new
            {
                model = generationModel.Name,
                messages,
                temperature = 0.2, // Fixed temperature for consistency
                stream = false,
                options = new Dictionary<string, object>
        {
            { "num_ctx", Math.Min(4096, generationModel.MaxContextLength) }, // Reduced context
            { "num_predict", 1500 }, // Reduced max tokens
            { "top_p", 0.9 },
            { "repeat_penalty", 1.1 }
        }
            };

            try
            {
                _logger.LogInformation($"🤖 Generating response with model: {generationModel.Name}");

                // OPTIMIZED: Reduced timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var response = await _httpClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ Chat generation failed: {response.StatusCode} - {errorContent}");
                    return GetFallbackMessage(ismeai, plant);
                }

                var json = await response.Content.ReadAsStringAsync();

                // OPTIMIZED: Simplified response parsing
                return await ParseLLMResponse(json, ismeai, plant);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("❌ Response generation timed out");
                return GetTimeoutMessage(ismeai, plant);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Chat generation failed");
                return GetErrorMessage(ismeai, plant);
            }
        }

        // OPTIMIZED: Separate method for building MEAI system prompt
        private string BuildMeaiSystemPrompt(string plant)
        {
            return $@"You are MEAI HR Policy Assistant for {plant}.

KEY DEFINITIONS:
• CL = Casual Leave (never 'Continuous Learning')
• SL = Sick Leave
• COFF = Compensatory Off
• EL = Earned Leave
• PL = Privilege Leave
• ML = Maternity Leave

INSTRUCTIONS:
1. Provide complete, detailed answers using policy information
2. Organize with clear headings and bullet points
3. Cite sources as [DocumentName]
4. If information is partial, state what's available
5. Focus on {plant} specific policies when available

Base answers ONLY on provided policy content.";
        }

        // OPTIMIZED: Separate method for general system prompt
        private string BuildGeneralSystemPrompt()
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

        // OPTIMIZED: Efficient context building
        private string BuildOptimizedContext(List<RelevantChunk> chunks, string plant)
        {
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine($"=== POLICY INFORMATION FOR {plant.ToUpper()} ===");

            // OPTIMIZED: Only use high-quality chunks
            var topChunks = chunks
                .Where(c => c.Similarity >= 0.3)
                .OrderByDescending(c => c.Similarity)
                .Take(3) // Reduced from 5
                .GroupBy(c => c.Source)
                .Take(2); // Max 2 different sources

            foreach (var group in topChunks)
            {
                var policyType = DeterminePolicyTypeSimple(group.Key, plant);
                contextBuilder.AppendLine($"\n📄 {group.Key} ({policyType}):");

                foreach (var chunk in group.Take(3)) // Max 3 chunks per source
                {
                    var text = TruncateText(chunk.Text.Trim(), 400); // Limit chunk size
                    contextBuilder.AppendLine($"• {text}");
                }
            }

            return contextBuilder.ToString();
        }

        // OPTIMIZED: Simple policy type determination
        private string DeterminePolicyTypeSimple(string source, string plant)
        {
            var lowerSource = source.ToLowerInvariant();

            if (lowerSource.Contains("context") || lowerSource.Contains("abbreviation"))
                return "Context Information";

            if (lowerSource.Contains("centralized") || lowerSource.Contains("general"))
                return "Centralized Policy";

            if (lowerSource.Contains(plant.ToLowerInvariant()))
                return $"{plant} Specific Policy";

            return "General Policy";
        }

        // OPTIMIZED: Text truncation helper
        private string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength).TrimEnd() + "...";
        }

        // OPTIMIZED: Simplified response parsing
        private async Task<string> ParseLLMResponse(string json, bool ismeai, string plant)
        {
            try
            {
                // Handle streaming format if present
                if (json.Contains("}\n{") || json.Contains("data:"))
                {
                    return ExtractContentFromStreaming(json);
                }

                // Parse normal JSON response
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("message", out var messageElement) &&
                    messageElement.TryGetProperty("content", out var contentElement))
                {
                    var content = contentElement.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogError("❌ Empty content received from LLM");
                        return GetFallbackMessage(ismeai, plant);
                    }

                    return CleanResponse(content);
                }

                _logger.LogError("❌ Unexpected response format from LLM");
                return GetFallbackMessage(ismeai, plant);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "❌ JSON parsing failed");
                return GetFallbackMessage(ismeai, plant);
            }
        }

        // OPTIMIZED: Simplified streaming content extraction
        private string ExtractContentFromStreaming(string rawResponse)
        {
            var contentBuilder = new StringBuilder();
            var lines = rawResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip empty lines and data prefixes
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine == "[DONE]")
                    continue;

                if (trimmedLine.StartsWith("data:"))
                    trimmedLine = trimmedLine.Substring(5).Trim();

                if (!trimmedLine.StartsWith("{") || !trimmedLine.EndsWith("}"))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(trimmedLine);
                    if (doc.RootElement.TryGetProperty("message", out var msgElem) &&
                        msgElem.TryGetProperty("content", out var contentElem))
                    {
                        var content = contentElem.GetString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            contentBuilder.Append(content);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed JSON lines
                    continue;
                }
            }

            var result = contentBuilder.ToString();
            return string.IsNullOrWhiteSpace(result) ?
                "I apologize, but I couldn't process the response properly. Please try again." :
                result;
        }

        // OPTIMIZED: Response cleaning
        private string CleanResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return "I apologize, but I couldn't generate a proper response. Please try again.";

            // Remove HTML/XML tags
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                response, @"<[^>]*>", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Clean excessive whitespace
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n\s*\n\s*\n", "\n\n");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @" {2,}", " ");

            return cleaned.Trim();
        }

        // Helper methods for error messages
        private string GetFallbackMessage(bool ismeai, string plant)
        {
            return ismeai
                ? $"I apologize, but I'm having trouble generating a response right now. Please contact your supervisor or HR department for assistance regarding {plant} policies."
                : "I apologize, but I'm having trouble generating a response right now. Please try again.";
        }

        private string GetTimeoutMessage(bool ismeai, string plant)
        {
            return ismeai
                ? $"The response generation timed out. Please contact your supervisor or HR department for assistance regarding {plant} policies."
                : "The response generation timed out. Please try a simpler question.";
        }

        private string GetErrorMessage(bool ismeai, string plant)
        {
            return ismeai
                ? $"I apologize, but I'm having trouble generating a response right now. Please contact your supervisor or HR department for assistance regarding {plant} policies."
                : "I apologize, but I'm having trouble generating a response right now. Please try again.";
        }


        private async Task<string> HandleStreamingResponse(string rawResponse)
        {
            try
            {
                var contentBuilder = new StringBuilder();

                // Split by lines and process each JSON object
                var lines = rawResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Skip empty lines and data: prefixes
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("data: "))
                    {
                        if (trimmedLine.StartsWith("data: "))
                            trimmedLine = trimmedLine.Substring(6).Trim();
                        else
                            continue;
                    }

                    // Skip [DONE] markers
                    if (trimmedLine.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (trimmedLine.StartsWith("{") && trimmedLine.EndsWith("}"))
                        {
                            using var doc = JsonDocument.Parse(trimmedLine);

                            if (doc.RootElement.TryGetProperty("message", out var messageElement) &&
                                messageElement.TryGetProperty("content", out var contentElement))
                            {
                                var content = contentElement.GetString();
                                if (!string.IsNullOrEmpty(content))
                                {
                                    contentBuilder.Append(content);
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed JSON lines
                        continue;
                    }
                }

                var result = contentBuilder.ToString();

                if (string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogWarning("⚠️ No content extracted from streaming response");
                    return "I apologize, but I couldn't process the response properly. Please try again.";
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle streaming response");
                return "I apologize, but I couldn't process the response properly. Please try again.";
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
            var chunks = new List<(string Text, string SourceFile)>();

            // Normalize the text
            text = text.Replace("\r", "").Replace("\t", " ").Trim();

            // Split by paragraphs (double newline)
            var paragraphs = text.Split(new[] { "\n\n", "\n\r\n", "\n \n" }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(p => p.Trim())
                                 .Where(p => !string.IsNullOrWhiteSpace(p))
                                 .ToList();

            var sb = new StringBuilder();
            int tokenCount = 0;

            foreach (var paragraph in paragraphs)
            {
                var paragraphTokenCount = EstimateTokenCount(paragraph);

                // If current paragraph fits in the limit, append
                if (tokenCount + paragraphTokenCount <= maxTokens)
                {
                    sb.AppendLine(paragraph);
                    sb.AppendLine(); // Preserve spacing between paragraphs
                    tokenCount += paragraphTokenCount;
                }
                else
                {
                    // Commit current chunk
                    if (sb.Length > 0)
                    {
                        chunks.Add((sb.ToString().Trim(), sourceFile));
                        sb.Clear();
                        tokenCount = 0;
                    }

                    // If single paragraph is too big, split it into sentences
                    if (paragraphTokenCount > maxTokens)
                    {
                        var sentences = paragraph.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
                        var tempSb = new StringBuilder();
                        int tempTokens = 0;

                        foreach (var sentence in sentences)
                        {
                            var sentenceText = sentence.Trim();
                            var sentenceTokens = EstimateTokenCount(sentenceText);
                            if (tempTokens + sentenceTokens > maxTokens && tempSb.Length > 0)
                            {
                                chunks.Add((tempSb.ToString().Trim(), sourceFile));
                                tempSb.Clear();
                                tempTokens = 0;
                            }

                            tempSb.AppendLine(sentenceText);
                            tempTokens += sentenceTokens;
                        }

                        if (tempSb.Length > 0)
                        {
                            chunks.Add((tempSb.ToString().Trim(), sourceFile));
                        }
                    }
                    else
                    {
                        sb.AppendLine(paragraph);
                        tokenCount += paragraphTokenCount;
                    }
                }
            }

            if (sb.Length > 0)
            {
                chunks.Add((sb.ToString().Trim(), sourceFile));
            }

            return chunks;
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
            if (string.IsNullOrWhiteSpace(response))
                return "I apologize, but I couldn't generate a proper response. Please try again.";

            // Remove any XML/HTML-like tags that might be interfering
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                response,
                @"<[^>]*>", "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            // Clean up excessive whitespace
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n\s*\n\s*\n", "\n\n");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @" {2,}", " ");

            // Ensure the response doesn't end abruptly
            cleaned = cleaned.Trim();

            // If response seems truncated (ends with incomplete sentence), add a note
            if (!string.IsNullOrEmpty(cleaned) &&
                !cleaned.EndsWith(".") &&
                !cleaned.EndsWith("!") &&
                !cleaned.EndsWith("?") &&
                !cleaned.EndsWith("]]") &&
                cleaned.Length > 10)
            {
                _logger.LogWarning($"⚠️ Response may be truncated: ends with '{cleaned.Substring(Math.Max(0, cleaned.Length - 20))}'");
            }

            return cleaned;
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
                model = _config.DefaultGenerationModel ?? "mistral:latest",
                messages = new[]
                {
            new { role = "user", content = prompt }
        },
                temperature = 0.1,
                stream = false // Ensure non-streaming response
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/chat", request);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Entity extraction failed: {response.StatusCode}");
                    return new List<string>();
                }

                var json = await response.Content.ReadAsStringAsync();

                // Log the raw response for debugging
                _logger.LogDebug($"Entity extraction response: {json}");

                // Handle potential streaming response format
                if (json.Contains("}\n{"))
                {
                    // Split by newlines and take the last valid JSON object
                    var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var lastValidJson = lines.LastOrDefault(line => line.Trim().StartsWith("{") && line.Trim().EndsWith("}"));

                    if (lastValidJson != null)
                    {
                        json = lastValidJson;
                    }
                }

                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("message", out var messageElement) ||
                    !messageElement.TryGetProperty("content", out var contentElement))
                {
                    _logger.LogWarning("Unexpected response format from entity extraction");
                    return new List<string>();
                }

                var content = contentElement.GetString() ?? "";

                return content.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(e => e.Trim().Trim('"', '\'', '-', '*'))
                             .Where(e => e.Length > 1 && !string.IsNullOrWhiteSpace(e))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Take(10) // Limit to prevent excessive entities
                             .ToList();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, $"JSON parsing failed in entity extraction. Raw response might be malformed.");
                return new List<string>();
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
                var chromaHealth = await _chromaClient.GetAsync("/api/v2/heartbeat");
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
        public async Task<List<CorrectionEntry>> GetRecentCorrections(int limit = 50)
        {
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
            using var semaphore = new SemaphoreSlim(1, 1); // Max 3 concurrent requests

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
        private readonly SemaphoreSlim _embeddingSemaphore = new(1, 1);


        private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("Empty text provided for embedding");
                return new List<float>();
            }

            _logger.LogInformation($"Generating embedding with model: {model.Name} for text: {text.Substring(0, Math.Min(50, text.Length))}...");

            var cacheKey = $"{model.Name}:{text.GetHashCode():X}";

            if (_embeddingCache.TryGetValue(cacheKey, out var cachedEmbedding))
            {
                _logger.LogDebug("Using cached embedding");
                return cachedEmbedding;
            }

            await _embeddingSemaphore.WaitAsync();
            try
            {
                var request = new
                {
                    model = model.Name,
                    prompt = text.Substring(0, Math.Min(1000, text.Length)) // Limit text length
                };

                _logger.LogInformation($"Sending embedding request: {JsonSerializer.Serialize(request)}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // Longer timeout
                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

                _logger.LogInformation($"Embedding response status: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Embedding request failed: {response.StatusCode} - {errorContent}");
                    return new List<float>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger.LogDebug($"Embedding response: {json.Substring(0, Math.Min(200, json.Length))}...");

                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                {
                    var embedding = embeddingProperty.EnumerateArray()
                        .Select(x => x.GetSingle())
                        .ToList();

                    _logger.LogInformation($"Generated embedding with {embedding.Count} dimensions");

                    if (_embeddingCache.Count < 1000)
                    {
                        _embeddingCache.TryAdd(cacheKey, embedding);
                    }

                    return embedding;
                }
                else
                {
                    _logger.LogError("No 'embedding' property found in response");
                    return new List<float>();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogError($"Embedding request timed out for model {model.Name}");
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
        //private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        //{
        //    if (string.IsNullOrWhiteSpace(text))
        //        return new List<float>();

        //    // Generate cache key
        //    var cacheKey = $"{model.Name}:{text.GetHashCode():X}";

        //    // Check cache first
        //    if (_embeddingCache.TryGetValue(cacheKey, out var cachedEmbedding))
        //    {
        //        return cachedEmbedding;
        //    }

        //    await _embeddingSemaphore.WaitAsync();
        //    try
        //    {
        //        // Double-check cache after acquiring semaphore
        //        if (_embeddingCache.TryGetValue(cacheKey, out cachedEmbedding))
        //        {
        //            return cachedEmbedding;
        //        }

        //        // Optimize text length based on model
        //        var maxLength = model.MaxContextLength * 3;
        //        if (text.Length > maxLength)
        //            text = text.Substring(0, maxLength);

        //        var request = new
        //        {
        //            model = model.Name,
        //            prompt = text,
        //            options = CreateOptimizedModelOptions(model)
        //        };

        //        // Use optimized HTTP client settings
        //        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        //        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

        //        if (!response.IsSuccessStatusCode)
        //        {
        //            _logger.LogWarning($"Embedding request failed: {response.StatusCode}");
        //            return new List<float>();
        //        }

        //        var json = await response.Content.ReadAsStringAsync();
        //        using var doc = JsonDocument.Parse(json);

        //        if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
        //        {
        //            var embedding = embeddingProperty.EnumerateArray()
        //                .Select(x => x.GetSingle())
        //                .ToList();

        //            // Cache the result (with size limit)
        //            if (_embeddingCache.Count < 10000) // Prevent memory bloat
        //            {
        //                _embeddingCache.TryAdd(cacheKey, embedding);
        //            }

        //            return embedding;
        //        }

        //        return new List<float>();
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        _logger.LogWarning($"Embedding request timed out for model {model.Name}");
        //        return new List<float>();
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Embedding generation failed for model {model.Name}");
        //        return new List<float>();
        //    }
        //    finally
        //    {
        //        _embeddingSemaphore.Release();
        //    }
        //}

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
        public async Task<QueryResponse> ProcessQueryWithCoverageCheck(string question, string plant)
        {
            var response = await ProcessQueryAsync(
                question: question,
                plant: plant,
                meaiInfo: true
            );

            // Check if we need to show additional UI elements
            if (!response.HasSufficientPolicyCoverage)
            {
                // Frontend can show:
                // - "Contact HR" button
                // - Alternative suggestion box
                // - Policy upload option for admins
                _logger.LogInformation($"⚠️ Insufficient coverage for {plant} - suggest HR contact");
            }

            return response;
        }
        // 1. UPDATE: More lenient policy coverage check
        private bool CheckPolicyCoverage(List<RelevantChunk> chunks, string question)
        {
            if (!chunks.Any())
            {
                _logger.LogWarning($"⚠️ No relevant chunks found for question: {question}");
                return false;
            }

            // More flexible coverage criteria for HR policies
            var veryHighQuality = chunks.Where(c => c.Similarity >= 0.7).ToList();
            var highQualityChunks = chunks.Where(c => c.Similarity >= 0.4).ToList(); // Lowered from 0.5
            var mediumQualityChunks = chunks.Where(c => c.Similarity >= 0.25).ToList(); // Lowered from 0.3
            var anyRelevantChunks = chunks.Where(c => c.Similarity >= 0.15).ToList(); // Very permissive

            // Check for HR policy keywords in the chunks
            var hrPolicyKeywords = new[] { "leave", "cl", "casual", "sick", "policy", "employee", "hr", "rule", "regulation", "procedure" };
            var hasHrPolicyContent = chunks.Any(c =>
                hrPolicyKeywords.Any(keyword => c.Text.ToLowerInvariant().Contains(keyword)) ||
                c.Source.ToLowerInvariant().Contains("policy") ||
                c.Source.ToLowerInvariant().Contains("hr"));

            // Enhanced coverage criteria:
            var hasSufficientCoverage =
                veryHighQuality.Any() ||                           // At least one very high match
                highQualityChunks.Count >= 1 ||                    // At least one good match  
                (mediumQualityChunks.Count >= 2 && hasHrPolicyContent) || // Multiple medium matches with HR content
                (anyRelevantChunks.Count >= 3 && hasHrPolicyContent);      // Many low matches with HR content

            
            if (!hasSufficientCoverage)
            {
                _logger.LogWarning($"⚠️ Insufficient policy coverage for question: {question}. " +
                                  $"Very High: {veryHighQuality.Count}, High: {highQualityChunks.Count}, " +
                                  $"Medium: {mediumQualityChunks.Count}, Any: {anyRelevantChunks.Count}, " +
                                  $"HR Content: {hasHrPolicyContent}");

                // Log chunk details for debugging
                foreach (var chunk in chunks.Take(3))
                {
                    _logger.LogInformation($"📄 Chunk: {chunk.Source} | Similarity: {chunk.Similarity:F3} | Text: {chunk.Text.Substring(0, Math.Min(100, chunk.Text.Length))}...");
                }
            }
            else
            {
                _logger.LogInformation($"✅ Sufficient policy coverage found. " +
                                      $"Very High: {veryHighQuality.Count}, High: {highQualityChunks.Count}, " +
                                      $"Medium: {mediumQualityChunks.Count}");
            }

            return hasSufficientCoverage;
        }

        // 2. UPDATE: More lenient similarity thresholds in search parsing
        private List<RelevantChunk> ParseSearchResults(JsonElement root, int maxResults, string currentPlant)
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
                    if (similarity < 0.10) continue;

                    var metadata = metadatas[i];
                    var sourceFile = metadata.TryGetProperty("source_file", out var sf)
                        ? Path.GetFileName(sf.GetString() ?? "")
                        : "Unknown";

                    var documentText = documents[i].GetString() ?? "";

                    // ✅ FIXED: Correct policy type determination
                    string policyType = DeterminePolicyType(metadata, sourceFile, currentPlant);

                    // Priority boost logic (unchanged)
                    var isPolicyDocument = sourceFile.ToLowerInvariant().Contains("policy") ||
                                         sourceFile.ToLowerInvariant().Contains("hr") ||
                                         documentText.ToLowerInvariant().Contains("policy");

                    if (sourceFile.Contains("abbreviation") || sourceFile.Contains("context") ||
                        documentText.ToUpper().Contains("CL =") || isPolicyDocument)
                    {
                        similarity = Math.Min(0.95, similarity + 0.15);
                    }

                    // Extra boost for plant-specific content
                    if (sourceFile.ToLowerInvariant().Contains(currentPlant.ToLowerInvariant()))
                    {
                        similarity = Math.Min(0.98, similarity + 0.10);
                    }

                    relevantChunks.Add(new RelevantChunk
                    {
                        Text = documentText,
                        Source = sourceFile,
                        Similarity = similarity,
                        PolicyType = policyType, // ✅ NEW: Add policy type to chunk
                    });
                }
            }

            var results = relevantChunks
                .OrderByDescending(c => c.Similarity)
                .Take(maxResults)
                .ToList();

            _logger.LogInformation($"📊 Parsed {results.Count} relevant chunks for plant: {currentPlant}");

            // Log what we found for debugging
            foreach (var chunk in results.Take(3))
            {
                _logger.LogInformation($"🔍 Found: {chunk.Source} ({chunk.PolicyType}) - Similarity: {chunk.Similarity:F3}");
            }

            return results;
        }
        public async Task<DiagnosticInfo> DiagnoseSearchIssueAsync(string question, string plant)
        {
            var diagnostic = new DiagnosticInfo
            {
                Question = question,
                Plant = plant,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Check if files exist
                var policyFiles = GetPolicyFiles(plant);
                diagnostic.PolicyFilesFound = policyFiles.Count;
                diagnostic.PolicyFiles = policyFiles.Select(Path.GetFileName).ToList();

                // Check embedding model
                var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                diagnostic.EmbeddingModel = embeddingModel?.Name ?? "NOT FOUND";

                // Check collection
                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);
                diagnostic.CollectionId = collectionId;

                // Get collection count
                diagnostic.TotalEmbeddings = await GetCollectionCountAsync(collectionId);

                // Perform search
                var chunks = await SearchChromaDBAsync(question, embeddingModel, 20, plant);
                diagnostic.ChunksFound = chunks.Count;
                diagnostic.ChunkDetails = chunks.Take(5).Select(c => new ChunkDiagnostic
                {
                    Source = c.Source,
                    Similarity = c.Similarity,
                    TextPreview = c.Text.Substring(0, Math.Min(200, c.Text.Length))
                }).ToList();

                // Check coverage
                diagnostic.HasSufficientCoverage = CheckPolicyCoverage(chunks, question);

                _logger.LogInformation($"🔍 Diagnostic completed for question: {question}");
                return diagnostic;
            }
            catch (Exception ex)
            {
                diagnostic.Error = ex.Message;
                _logger.LogError(ex, "Diagnostic failed");
                return diagnostic;
            }
        }

        private async Task<List<RelevantChunk>> SearchChromaDBAsync(
    string query,
    ModelConfiguration embeddingModel,
    int maxResults,
    string plant)
        {
            var normalizedPlant = plant.Trim().ToLowerInvariant();
            var cacheKey = $"{query.GetHashCode():X}:{embeddingModel.Name}:{maxResults}:{normalizedPlant}";

            _logger.LogInformation($"🔍 Searching ChromaDB for plant '{plant}' with query: '{query}'");

            // Check cache (5-minute TTL)
            if (_searchCache.TryGetValue(cacheKey, out var cached) &&
                DateTime.Now - cached.Timestamp < TimeSpan.FromMinutes(5))
            {
                _logger.LogDebug($"✅ Using cached search results for plant: {plant}");
                return cached.Results;
            }

            try
            {
                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);
                if (string.IsNullOrEmpty(collectionId))
                {
                    _logger.LogError($"❌ No collection found for embedding model: {embeddingModel.Name}");
                    return new List<RelevantChunk>();
                }

                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);
                if (queryEmbedding.Count == 0)
                {
                    _logger.LogError($"❌ Failed to generate embedding for query: {query}");
                    return new List<RelevantChunk>();
                }

                // ✅ FIXED: More precise plant-specific filter
                var whereFilter = new Dictionary<string, object>
        {
            { "$or", new List<Dictionary<string, object>>
                {
                    // Exact plant match
                    new Dictionary<string, object> { { "plant", normalizedPlant } },
                    // Centralized policies (apply to all plants)
                    new Dictionary<string, object> { { "plant", "centralized" } },
                    new Dictionary<string, object> { { "is_centralized", true } },
                    // Context files (apply to all plants)
                    new Dictionary<string, object> { { "is_context", true } }
                }
            }
        };

                _logger.LogInformation($"🔎 Plant filter for '{normalizedPlant}': {JsonSerializer.Serialize(whereFilter)}");

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = Math.Min(maxResults * 3, 100),
                    include = new[] { "documents", "metadatas", "distances" },
                    where = whereFilter
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ ChromaDB search failed: {response.StatusCode} - {errorContent}");
                    return await FallbackSearchAsync(query, embeddingModel, maxResults, collectionId);
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseContent);
                var results = ParseSearchResults(doc.RootElement, maxResults, plant);

                // ✅ FIXED: Strict plant filtering after search
                results = FilterResultsByPlant(results, normalizedPlant);

                _logger.LogInformation($"📊 Search completed: {results.Count} results found for plant '{plant}'");

                // Cache results
                if (_searchCache.Count < 1000)
                {
                    _searchCache.TryAdd(cacheKey, (results, DateTime.Now));
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ ChromaDB search failed for model {embeddingModel.Name} and plant {plant}");
                return new List<RelevantChunk>();
            }
        }

        private async Task<List<RelevantChunk>> FallbackSearchAsync(
    string query,
    ModelConfiguration embeddingModel,
    int maxResults,
    string collectionId)
        {
            try
            {
                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = maxResults * 2,
                    include = new[] { "documents", "metadatas", "distances" }
                    // No where clause - search everything
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseContent);
                    var results = ParseSearchResults(doc.RootElement, maxResults, ""); // No plant filter

                    _logger.LogInformation($"🔄 Fallback search found {results.Count} results");
                    return results;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallback search also failed");
            }

            return new List<RelevantChunk>();
        }

        private async Task<List<RelevantChunk>> DiagnosticSearchAsync(
    string query,
    ModelConfiguration embeddingModel,
    string collectionId)
        {
            try
            {
                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = 50,
                    include = new[] { "documents", "metadatas", "distances" }
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseContent);
                    return ParseSearchResults(doc.RootElement, 50, "");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Diagnostic search failed");
            }

            return new List<RelevantChunk>();
        }

        // Supporting classes for diagnostics
        public class DiagnosticInfo
        {
            public string Question { get; set; } = "";
            public string Plant { get; set; } = "";
            public DateTime Timestamp { get; set; }
            public int PolicyFilesFound { get; set; }
            public List<string> PolicyFiles { get; set; } = new();
            public string EmbeddingModel { get; set; } = "";
            public string CollectionId { get; set; } = "";
            public int TotalEmbeddings { get; set; }
            public int ChunksFound { get; set; }
            public List<ChunkDiagnostic> ChunkDetails { get; set; } = new();
            public bool HasSufficientCoverage { get; set; }
            public string? Error { get; set; }
        }

        public class ChunkDiagnostic
        {
            public string Source { get; set; } = "";
            public double Similarity { get; set; }
            public string TextPreview { get; set; } = "";
        }

        private List<RelevantChunk> FilterResultsByPlant(List<RelevantChunk> chunks, string targetPlant)
        {
            var filteredChunks = new List<RelevantChunk>();

            foreach (var chunk in chunks)
            {
                var source = chunk.Source.ToLowerInvariant();
                var isValid = false;
                string policyType = "";

                // 1. Target plant-specific files
                if (source.Contains(targetPlant))
                {
                    isValid = true;
                    policyType = $"{targetPlant.ToTitleCase()} Specific Policy";
                }
                // 2. Centralized policies (apply to all plants)
                else if (source.Contains("centralized") || source.Contains("general"))
                {
                    isValid = true;
                    policyType = "Centralized Policy";
                }
                // 3. Context files (abbreviations, etc.)
                else if (source.Contains("abbreviation") || source.Contains("context"))
                {
                    isValid = true;
                    policyType = "Context Information";
                }
                // 4. REJECT other plant-specific files
                else
                {
                    var otherPlants = new[] { "manesar", "sanand" }; // Add all your plants
                    var isOtherPlant = otherPlants.Any(plant =>
                        plant != targetPlant && source.Contains(plant));

                    if (isOtherPlant)
                    {
                        _logger.LogDebug($"🚫 Filtered out {chunk.Source} - belongs to different plant");
                        continue; // Skip this chunk
                    }
                    else
                    {
                        // Unknown source - include with warning
                        isValid = true;
                        policyType = "General Policy";
                        _logger.LogWarning($"⚠️ Unknown source classification: {chunk.Source}");
                    }
                }

                if (isValid)
                {
                    // ✅ FIXED: Set correct policy type label
                    chunk.PolicyType = policyType;
                    filteredChunks.Add(chunk);
                }
            }

            _logger.LogInformation($"🎯 Plant filtering: {chunks.Count} → {filteredChunks.Count} chunks for {targetPlant}");
            return filteredChunks;
        }
        private Dictionary<string, object> CreateChunkMetadata(string sourceFile, DateTime lastModified, string modelName, string text, string plant)
        {
            var fileName = Path.GetFileName(sourceFile).ToLowerInvariant();
            var folderPath = Path.GetDirectoryName(sourceFile)?.ToLowerInvariant() ?? "";
            var normalizedPlant = plant.ToLowerInvariant();

            var metadata = new Dictionary<string, object>
    {
        { "source_file", sourceFile },
        { "last_modified", lastModified.ToString("O") },
        { "model", modelName },
        { "chunk_size", text.Length },
        { "processed_at", DateTime.UtcNow.ToString("O") },
        { "processed_by", _currentUser }
    };

            // ✅ FIXED: Proper plant classification logic
            if (fileName.Contains("abbreviation") || fileName.Contains("context") ||
                folderPath.Contains("context"))
            {
                // Context files apply to all plants
                metadata["plant"] = "context";
                metadata["is_context"] = true;
                metadata["priority"] = "high";
                metadata["applies_to_all_plants"] = true;
            }
            else if (folderPath.Contains("centralized") || fileName.Contains("centralized") ||
                     fileName.Contains("general"))
            {
                // Centralized policies apply to all plants
                metadata["plant"] = "centralized";
                metadata["is_centralized"] = true;
                metadata["applies_to_all_plants"] = true;
            }
            else if (folderPath.Contains(normalizedPlant) || fileName.Contains(normalizedPlant))
            {
                // Plant-specific policies
                metadata["plant"] = normalizedPlant;
                metadata["is_plant_specific"] = true;
                metadata["applies_to_all_plants"] = false;
            }
            else
            {
                // Check if it belongs to another plant
                var knownPlants = new[] { "manesar", "sanand" };
                var detectedPlant = knownPlants.FirstOrDefault(p =>
                    folderPath.Contains(p) || fileName.Contains(p));

                if (detectedPlant != null)
                {
                    metadata["plant"] = detectedPlant;
                    metadata["is_plant_specific"] = true;
                    metadata["applies_to_all_plants"] = false;
                }
                else
                {
                    // Unknown - treat as general
                    metadata["plant"] = "general";
                    metadata["is_general"] = true;
                    metadata["applies_to_all_plants"] = true;
                }
            }

            _logger.LogDebug($"🏷️ Metadata for {fileName}: plant={metadata["plant"]}, applies_to_all={metadata.GetValueOrDefault("applies_to_all_plants", false)}");

            return metadata;
        }
        private string BuildPolicySourcesDisplay(List<RelevantChunk> chunks)
        {
            var sourceGroups = chunks
                .GroupBy(c => c.Source)
                .Select(g => new
                {
                    Source = g.Key,
                    PolicyType = g.First().PolicyType,
                    MaxSimilarity = g.Max(c => c.Similarity)
                })
                .OrderByDescending(g => g.MaxSimilarity)
                .Take(5);

            var sourcesText = new StringBuilder();
            sourcesText.AppendLine("Sources:");

            foreach (var group in sourceGroups)
            {
                sourcesText.AppendLine($" {group.Source} ({group.PolicyType})");
            }

            return sourcesText.ToString();
        }
        private string DeterminePolicyType(JsonElement metadata, string sourceFile, string currentPlant)
        {
            var fileName = sourceFile.ToLowerInvariant();

            // Check metadata first
            if (metadata.TryGetProperty("is_context", out var isContext) && isContext.GetBoolean())
            {
                return "Context Information";
            }

            if (metadata.TryGetProperty("is_centralized", out var isCentralized) && isCentralized.GetBoolean())
            {
                return "Centralized Policy";
            }

            if (metadata.TryGetProperty("plant", out var plantProperty))
            {
                var plantValue = plantProperty.GetString()?.ToLowerInvariant() ?? "";

                if (plantValue == "context")
                    return "Context Information";
                if (plantValue == "centralized" || plantValue == "general")
                    return "Centralized Policy";
                if (plantValue == currentPlant.ToLowerInvariant())
                    return $"{currentPlant.ToTitleCase()} Specific Policy";
                if (plantValue != currentPlant.ToLowerInvariant() && !string.IsNullOrEmpty(plantValue))
                    return $"{plantValue.ToTitleCase()} Policy (Cross-Reference)";
            }

            // Fallback to file name analysis
            if (fileName.Contains("abbreviation") || fileName.Contains("context"))
                return "Context Information";
            if (fileName.Contains("centralized") || fileName.Contains("general"))
                return "Centralized Policy";
            if (fileName.Contains(currentPlant.ToLowerInvariant()))
                return $"{currentPlant.ToTitleCase()} Specific Policy";

            return "General Policy";
        }

        private async Task<List<RelevantChunk>> GetRelevantChunksWithExpansionAsync(
        string query,
        ModelConfiguration embeddingModel,
        int maxResults,
        bool meaiInfo,
        ConversationContext context,
        bool useReRanking,
        ModelConfiguration generationModel,
        string plant)
        {
            if (!meaiInfo) return new List<RelevantChunk>();

            try
            {
                var allChunks = new List<RelevantChunk>();

                // 1. Original query search
                var originalChunks = await SearchChromaDBAsync(query, embeddingModel, maxResults, plant);
                allChunks.AddRange(originalChunks);

                // 2. Expanded query search
                var expandedQuery = _abbreviationService.ExpandQuery(query);
                if (expandedQuery != query)
                {
                    var expandedChunks = await SearchChromaDBAsync(expandedQuery, embeddingModel, maxResults, plant);
                    allChunks.AddRange(expandedChunks);
                }

                // 3. Individual term searches for key abbreviations
                var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in queryWords)
                {
                    var variations = _abbreviationService.GetAllVariations(word);
                    if (variations.Count > 1) // Has variations
                    {
                        foreach (var variation in variations.Take(2)) // Limit to avoid too many queries
                        {
                            if (!variation.Equals(word, StringComparison.OrdinalIgnoreCase))
                            {
                                var variationChunks = await SearchChromaDBAsync(variation, embeddingModel, 5, plant);
                                allChunks.AddRange(variationChunks);
                            }
                        }
                    }
                }

                // 4. Deduplicate and rank by similarity
                var uniqueChunks = allChunks
                    .GroupBy(c => c.Text)
                    .Select(g => g.OrderByDescending(c => c.Similarity).First())
                    .OrderByDescending(c => c.Similarity)
                    .Take(maxResults)
                    .ToList();

                _logger.LogInformation($"🔍 Multi-query search: {originalChunks.Count} original + {allChunks.Count - originalChunks.Count} expanded = {uniqueChunks.Count} final chunks");

                return uniqueChunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get relevant chunks with expansion");
                return await SearchChromaDBAsync(query, embeddingModel, maxResults, plant); // Fallback
            }
        }


        public async Task DeleteModelDataFromChroma(string modelName)
        {
            await _collectionManager.DeleteModelCollectionAsync(modelName);
        }

    }

}
