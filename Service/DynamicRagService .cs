// Services/DynamicRagService.cs
using DocumentFormat.OpenXml.InkML;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        //private readonly ConcurrentDictionary<string, (List<float> Embedding, DateTime Cached)> _optimizedEmbeddingCache = new();
        //private readonly SemaphoreSlim _globalEmbeddingSemaphore = new(3, 3); // Allow 3 concurrent embedding requests
        private readonly ConcurrentDictionary<string, (List<float> Embedding, DateTime Cached, int AccessCount)> _optimizedEmbeddingCache = new();
        private readonly SemaphoreSlim _globalEmbeddingSemaphore = new(5, 5); // Increased concurrency
        private readonly Timer _cacheCleanupTimer;
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _dynamicSectionMappings = new();
        private DateTime _lastMappingRefresh = DateTime.MinValue;
        private readonly SemaphoreSlim _mappingRefreshSemaphore = new(1, 1);
        private readonly ConcurrentDictionary<string, List<string>> _learnedAssociations = new();


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
            _cacheCleanupTimer = new Timer(CleanupEmbeddingCache, null,
                                    TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

            InitializeSessionCleanup();
        }

        // 🆕 Enhanced InitializeAsync method
        //public async Task InitializeAsync()
        //{
        //    if (_isInitialized) return;
        //    lock (_lock)
        //    {
        //        if (_isInitialized) return;
        //        _isInitialized = true;
        //    }
        //    try
        //    {
        //        _logger.LogInformation("🚀 Starting dynamic RAG system initialization");


        //        // Ensure directories exist
        //        EnsureDirectoriesExist();

        //        // Create abbreviation context if needed
        //        EnsureAbbreviationContext();

        //        // Discover available models
        //        var availableModels = await _modelManager.DiscoverAvailableModelsAsync();
        //        _logger.LogInformation($"📋 Found {availableModels.Count} available models");

        //        if (!availableModels.Any())
        //        {
        //            throw new RAGServiceException("No models available for RAG system");
        //        }

        //        // Set default models if not configured
        //        await ConfigureDefaultModelsAsync(availableModels);

        //        // Initialize collections for all embedding models
        //        var embeddingModels = availableModels.Where(m =>
        //            m.Type == "embedding" || m.Type == "both").ToList();

        //        if (!embeddingModels.Any())
        //        {
        //            throw new RAGServiceException("No embedding models available");
        //        }

        //        // Process documents for all embedding models
        //        foreach (var plant in _plants.Plants.Keys)
        //        {
        //            _logger.LogInformation($"Processing documents for plant: {plant}");
        //            await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);
        //        }

        //        // 🆕 Load historical appreciated answers
        //        await LoadHistoricalAppreciatedAnswersAsync();
        //        await LoadCorrectionCacheAsync();

        //        _isInitialized = true;
        //        _logger.LogInformation("✅ Dynamic RAG system initialization completed");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "❌ Failed to initialize dynamic RAG system");
        //        throw;
        //    }
        //}
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
        // In your ProcessFileForModelAsync method, ensure you're using proper document processing
        private async Task ProcessFileForModelAsync(string filePath, ModelConfiguration model, string collectionId, string plant)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);

                // 🔧 IMPORTANT: Use proper document processor instead of copy-paste text
                var content = await _documentProcessor.ExtractTextAsync(filePath);

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning($"⚠️ No content extracted from {filePath}");
                    return;
                }

                // Log the extraction quality
                _logger.LogInformation($"📄 Extracted {content.Length} characters from {Path.GetFileName(filePath)}");

                // Check for section headers in extracted content

                var sectionMatches = System.Text.RegularExpressions.Regex.Matches(
                    content, @"Section\s+\d+", RegexOptions.IgnoreCase);
                _logger.LogInformation($"🔍 Found {sectionMatches.Count} section headers in extracted content");


                var chunks = ChunkText(content, filePath);
                await ProcessChunkBatchForModelAsync(chunks, model, collectionId, fileInfo.LastWriteTime, plant);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process file {filePath} for model {model.Name}");
            }
        }

        private QueryResponse CreateSuccessResponse(
    string answer,
    string source,
    long processingTimeMs,
    double confidence,
    string? sessionId = null,
    bool isFromCorrection = false,
    List<RelevantChunk>? relevantChunks = null,
    Dictionary<string, string>? modelsUsed = null,
    string? plant = null)
        {
            return new QueryResponse
            {
                Answer = answer,
                IsFromCorrection = isFromCorrection,
                Sources = new List<string> { source },
                Confidence = confidence,
                ProcessingTimeMs = processingTimeMs,
                RelevantChunks = relevantChunks ?? new List<RelevantChunk>(),
                SessionId = sessionId,
                ModelsUsed = modelsUsed ?? new Dictionary<string, string>(),
                Plant = plant,
                HasSufficientPolicyCoverage = true // Success responses typically have sufficient coverage
            };
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
            // Early validation to avoid unnecessary processing
            if (string.IsNullOrWhiteSpace(question))
                throw new ArgumentException("Question cannot be empty");
            try
            {
                // Use provided models or defaults (ONLY when meaiInfo = true)
                generationModel ??= _config.DefaultGenerationModel;
                embeddingModel ??= _config.DefaultEmbeddingModel;
                // Get or create session in database
                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(),
                    _currentUser);
                var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);

                var appreciated = await CheckAppreciatedAnswerAsync(question);
                if (appreciated != null)
                {
                    _logger.LogInformation("⚡ Early return: Using appreciated answer");
                    return CreateSuccessResponse(appreciated.Value.Answer, "Appreciated Answer",
                        stopwatch.ElapsedMilliseconds, 0.95);
                }

                // EARLY RETURN 2: Check corrections (second fastest)
                var correction = await CheckCorrectionsAsync(question);
                if (correction != null)
                {
                    _logger.LogInformation("⚡ Early return: Using correction");
                    var rephrasedAnswer = await RephraseWithLLMAsync(correction.Answer, generationModel);
                    return CreateSuccessResponse(rephrasedAnswer, "User Correction",
                        stopwatch.ElapsedMilliseconds, 1.0, isFromCorrection: true);
                }

                // EARLY RETURN 3: History clear request
                if (IsHistoryClearRequest(question))
                {
                    return await HandleHistoryClearRequest(context, sessionId);
                }

                // 🚀 FAST PATH: If meaiInfo is false, skip ALL embedding operations
                if (!meaiInfo)
                {
                    return await ProcessNonMeaiQueryFast(question, sessionId, generationModel, stopwatch);
                }
                // Validate models are available
                var genModel = await _modelManager.GetModelAsync(generationModel!);
                var embModel = await _modelManager.GetModelAsync(embeddingModel!);

                if (genModel == null)
                    throw new ArgumentException($"Generation model {generationModel} not available");
                if (embModel == null)
                    throw new ArgumentException($"Embedding model {embeddingModel} not available");
                if (embModel.EmbeddingDimension == 0)
                    embModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);



                _logger.LogInformation($"Processing MEAI query with models - Gen: {generationModel}, Emb: {embeddingModel}");

                if (IsHistoryClearRequest(question))
                {
                    return await HandleHistoryClearRequest(context, dbSession.SessionId);
                }

                // 🆕 Check for similar conversations in database first (ONLY for MEAI queries)
                var questionEmbedding = await GetEmbeddingAsync(question, embModel);
                var similarConversations = await _conversationStorage.SearchSimilarConversationsAsync(
                    questionEmbedding, plant, threshold: 0.85, limit: 2); // Increased threshold, reduced limit

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

                var sectionQuery = await DetectAndParseSection(question); // Make it async
                if (sectionQuery != null)
                {
                    _logger.LogInformation($"🎯 Detected section query: {sectionQuery.DocumentType} Section {sectionQuery.SectionNumber}");
                    // Use section-specific search logic
                    relevantChunks = await SearchForSpecificSection(
                        sectionQuery, embModel, maxResults, plant,
                        await _collectionManager.GetOrCreateCollectionAsync(embModel));
                }
                else
                {
                    // Your existing general search logic
                    relevantChunks = await GetRelevantChunksWithExpansionAsync(
                        contextualQuery, embModel, maxResults, meaiInfo, context, useReRanking, genModel, plant);
                }


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
    //            var answer = await GenerateChatResponseAsync(
    //                        question, genModel, context.History, relevantChunks, context, ismeai: meaiInfo, plant: plant);

    //            var topRelevantChunks = relevantChunks
    //.Where(x => x.Similarity > 0.4) // Higher threshold
    //.OrderByDescending(x => x.Similarity)
    //.Take(3) // Process fewer chunks
    //.ToList();



                //var answerEmbedding = await GetEmbeddingAsync(answer, embModel);

                //// Rank chunks by similarity to answer
                //var scoredChunks = new List<(RelevantChunk Chunk, double Similarity)>();
                //foreach (var chunk in topRelevantChunks.Where(x => x.Similarity > 0.5))
                //{
                //    var chunkEmbedding = await GetEmbeddingAsync(chunk.Text, embModel);
                //    var similarity = CosineSimilarity(answerEmbedding, chunkEmbedding);
                //    chunk.Similarity = similarity;
                //    scoredChunks.Add((chunk, similarity));
                //}

                //// Sort by similarity
                //var topChunks = scoredChunks
                //    .Where(c => c.Similarity >= 0.5)
                //    .OrderByDescending(c => c.Similarity)
                //    .Take(5)
                //    .Select(c =>
                //    {
                //        c.Chunk.Similarity = c.Similarity;
                //        return c.Chunk;
                //    })
                //    .ToList();

                //// Compute confidence
                //var confidence = topChunks.FirstOrDefault()?.Similarity ?? 0;

                // 1️⃣ Get answer embedding
                var answer = await GenerateChatResponseAsync(
                    question, genModel, context.History, relevantChunks, context, ismeai: meaiInfo, plant: plant);
                var answerEmbedding = await GetEmbeddingAsync(answer, embModel);

                // 2️⃣ Process only top 3 relevant chunks by initial similarity
                var scoredChunks = await Task.WhenAll(
                    relevantChunks
                        .Where(x => x.Similarity > 0.4) // initial threshold
                        .OrderByDescending(x => x.Similarity)
                        .Take(3) // fewer chunks
                        .Select(async chunk =>
                        {
                            var chunkEmbedding = await GetEmbeddingAsync(chunk.Text, embModel);
                            var sim = CosineSimilarity(answerEmbedding, chunkEmbedding);
                            chunk.Similarity = sim;
                            return (chunk, sim);
                        })
                );

                // 3️⃣ Get final top 3 by recalculated similarity
                var topChunks = scoredChunks
                    .Where(c => c.sim > 0.5)
                    .OrderByDescending(c => c.sim)
                    .Take(3)
                    .Select(c => c.chunk)
                    .ToList();

                // 4️⃣ Confidence score
                var confidence = topChunks.FirstOrDefault()?.Similarity ?? 0;

                var questionEmbeddingTask = GetEmbeddingAsync(question, embModel);
                var answerEmbeddingTask = GetEmbeddingAsync(answer, embModel);
                var entitiesTask = ExtractEntitiesAsync(answer);

                await Task.WhenAll(questionEmbeddingTask, answerEmbeddingTask, entitiesTask);

                var namedEntities = await entitiesTask;

                // Save conversation to database
                var conversationId = await SaveConversationToDatabaseFast(
                                    dbSession.SessionId, question, answer, topChunks,
                                    genModel, embModel, confidence, stopwatch.ElapsedMilliseconds,
                                    isFromCorrection: false, parentId, plant,
                                    questionEmbedding, answerEmbedding, namedEntities);

                // Update session metadata with last conversation ID
                dbSession.Metadata["lastConversationId"] = conversationId;
                await _conversationStorage.UpdateSessionAsync(dbSession);

                // Update in-memory context
                await UpdateConversationHistoryFast(context, question, answer, relevantChunks, namedEntities);

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

        private async Task<int> SaveConversationToDatabaseFast(
    string sessionId, string question, string answer, List<RelevantChunk> chunks,
    ModelConfiguration generationModel, ModelConfiguration embeddingModel,
    double confidence, long processingTimeMs, bool isFromCorrection,
    int? parentId, string plant,
    List<float> questionEmbedding, List<float> answerEmbedding, List<string> namedEntities)
        {
            try
            {
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
                return 0;
            }
        }

        private Task UpdateConversationHistoryFast(
    ConversationContext context, string question, string answer,
    List<RelevantChunk> relevantChunks, List<string> namedEntities)
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
                    context.History = context.History.TakeLast(10).ToList();

                var currentTopics = ExtractKeyTopics(question);
                if (currentTopics.Any())
                {
                    var isMainTopic = !IsQuestionPatternContinuation(question, context)
                                      && question.Split(' ').Length >= 4;
                    if (isMainTopic)
                    {
                        context.LastTopicAnchor = question;
                        _logger.LogDebug($"Updated topic anchor: {question}");
                    }
                }

                context.LastAccessed = DateTime.Now;

                foreach (var entity in namedEntities)
                {
                    if (!context.NamedEntities.Contains(entity, StringComparer.OrdinalIgnoreCase))
                        context.NamedEntities.Add(entity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update conversation history");
            }

            return Task.CompletedTask;
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
            // In GenerateChatResponseAsync method, around line 829:
            messages.Add(new
            {
                role = "system",
                content = ismeai ?
                    await BuildMeaiSystemPrompt(plant, chunks, question) : // Add await
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
                temperature = 0.1, // Lower temperature for more factual responses
                stream = false,
                options = new Dictionary<string, object>
                        {
                            { "num_ctx", 6144 }, // Increased context window
                            { "num_predict", 2000 }, // Allow longer responses
                            { "top_p", 0.9 },
                            { "repeat_penalty", 1.05 },
                            { "stop", new string[] {} } // Don't stop early
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

                // Add this RIGHT BEFORE calling the LLM
                if (chunks.Any() && ismeai && hasSufficientCoverage)
                {
                    var contextContent = BuildOptimizedContext(chunks, plant);
                    messages.Add(new { role = "system", content = contextContent });
                }


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
        private async Task<string> BuildMeaiSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            // Check if this is a section query first
            if (HasSectionReference(query))
            {
                return await BuildDynamicSectionSystemPrompt(plant, chunks, query);
            }
            else if (HasManyAbbreviations(query))
            {
                var variables = new Dictionary<string, object>
                {
                    ["plant"] = plant,
                    ["abbreviations"] = FormatAbbreviations(ExtractAbbreviationsFromQuery(query, chunks))
                };
                return BuildSystemPromptFromTemplate(plant, "abbreviation_heavy", variables);
            }
            else
            {
                return BuildContextAwareSystemPrompt(plant, chunks, query);
            }
        }

        private async Task<string> BuildDynamicSectionSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var sectionQuery = await DetectAndParseSection(query);
            if (sectionQuery == null) return BuildContextAwareSystemPrompt(plant, chunks, query);

            var sectionRef = $"Section {sectionQuery.SectionNumber}";
            var docType = string.IsNullOrEmpty(sectionQuery.DocumentType)
                ? "Policy"
                : sectionQuery.DocumentType;

            // Detect what sections are actually available in the chunks
            var availableSections = DetectAvailableSections(chunks, sectionQuery.SectionNumber);

            return $@"You are MEAI Policy Assistant for {plant}.

**PRIMARY OBJECTIVE**: Answer questions using ONLY the provided policy context, regardless of topic domain.


🎯 USER IS ASKING ABOUT: {sectionRef} of {docType}

CRITICAL INSTRUCTIONS FOR DYNAMIC SECTION QUERIES:
1. **POLICY-SPECIFIC APPROACH**: Different policies have different section structures
   - ISMS policies may have different {sectionRef} content than HR policies
   - Safety policies may structure sections differently than Quality policies
   - Always specify which policy type you're referencing

2. **AVAILABLE CONTENT ANALYSIS**:
{BuildAvailableContentSummary(availableSections, sectionRef)}

3. **COMPREHENSIVE COVERAGE**: 
   - Look for ""{sectionRef}"" in ALL provided policy contexts
   - Include content from {docType} policies specifically
   - Cover all subsections (e.g., {sectionQuery.SectionNumber}.1, {sectionQuery.SectionNumber}.2, etc.)

4. **MULTI-POLICY HANDLING**:
   - If {sectionRef} exists in multiple policy types, clearly separate them
   - Format: ""## {sectionRef} in ISMS Policy"", ""## {sectionRef} in HR Policy"", etc.
   - Highlight differences between policy types

5. **STRUCTURE YOUR RESPONSE**:
   - Start with policy type identification
   - Main section overview with exact section title
   - All relevant subsections with full content
   - Procedures and requirements specific to that policy type

6. **CONTEXT VALIDATION**:
   - Always mention which document/policy type contains the information
   - If section doesn't exist in a particular policy, clearly state it
   - Cite sources with policy type: ""[{docType} Policy - {sectionRef}: filename]""

7. **COMPLETENESS**: Provide COMPLETE and DETAILED information for the specific policy type
   - Don't mix content from different policy types
   - If multiple policies have the same section, clearly separate them

8. **CRITICAL RULES**:
    8.1. **READ CAREFULLY**: The context contains actual policy content - use it completely
    8.2. **BE COMPREHENSIVE**: If policy content exists, provide COMPLETE details
    8.3. **STAY FACTUAL**: Base answers ONLY on provided context
    8.4. **QUOTE DIRECTLY**: Use exact wording from policies when possible
    8.5. **CITE SOURCES**: Always mention document names

**RESPONSE APPROACH**:
- If context contains relevant information → Provide detailed, complete answer
- If context has partial information → Use what's available and note limitations  
- If no relevant context → State clearly that information is not available

**FORMATTING**:
- Use clear headings and structure
- Quote exact policy text when applicable
- Always cite: [Source: Document Name]
- Be thorough - don't summarize if full details are available

**REMEMBER**: You handle ALL policy domains - HR, Safety, Quality, ISMS, Environment, etc.
Your job is to extract and present policy information accurately, regardless of the topic.

Check the provided context thoroughly before responding.

Remember: Section numbers may represent completely different topics across policy types!
Current context contains: {string.Join(", ", chunks.Select(c => DeterminePolicyTypeFromSource(c.Source)).Distinct())} policies.";
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


        private string BuildUniversalSectionSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var sectionRef = ExtractSectionReference(query);

            return $@"You are MEAI Policy Assistant for {plant}.

🎯 USER IS ASKING ABOUT: {sectionRef}

CRITICAL INSTRUCTIONS FOR ANY SECTION:
1. **COMPREHENSIVE COVERAGE**: Provide complete information for the requested section
2. **INCLUDE ALL SUBSECTIONS**: Look for {sectionRef}.1, {sectionRef}.2, etc. and provide complete details
3. **STRUCTURE YOUR RESPONSE**:
   - Main section overview
   - All subsections with their full content
   - Procedures and requirements
   - Any related information

4. **SECTION-SPECIFIC GUIDANCE**:
   - For Section 1: Cover introduction, purpose, scope
   - For Section 2: Cover scope and application
   - For Section 3: Cover definitions and terms
   - For Section 4: Cover organizational context
   - For Section 5: Cover leadership and policy
   - For Section 6: Cover physical security and secure areas
   - For Section 7: Cover planning and risk assessment
   - For Section 8: Cover operations and controls
   - For Section 9: Cover performance evaluation
   - For Section 10: Cover improvement processes

5. **FORMATTING**: Use clear headings like:
   ## {sectionRef}: [Section Title]
   ### {sectionRef}.1 [Subsection Title]
   ### {sectionRef}.2 [Subsection Title]

6. **CITE SOURCES**: Always mention the document name where information is found

7. **COMPLETENESS**: If {sectionRef} content exists in the context, provide COMPLETE and DETAILED information. Don't summarize - give full policy details including procedures, requirements, and guidelines.

Check the provided context thoroughly for ALL {sectionRef} related content before responding.";
        }

        private string BuildSystemPromptFromTemplate(string plant, string templateType, Dictionary<string, object> variables)
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

        private string BuildContextAwareSystemPrompt(string plant, List<RelevantChunk> chunks, string query)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine($"You are MEAI Policy Assistant for {plant}.");
            prompt.AppendLine();
            prompt.AppendLine("🎯 CRITICAL INSTRUCTIONS:");
            prompt.AppendLine("1. **READ ALL PROVIDED CONTEXT CAREFULLY** - The context contains actual policy content");
            prompt.AppendLine("2. **USE EXACT CONTENT**: Base answers ONLY on the provided policy context");
            prompt.AppendLine("3. **COMPREHENSIVE ANSWERS**: When content exists, provide complete details");
            prompt.AppendLine("4. **ACCURATE CITATIONS**: Always cite source documents");
            prompt.AppendLine();

            // Add query-specific guidance
            if (HasSectionReference(query))
            {
                var sectionRef = ExtractSectionReference(query);
                prompt.AppendLine($"🔍 USER IS ASKING ABOUT: {sectionRef}");
                prompt.AppendLine("- Look carefully for this specific section in the context");
                prompt.AppendLine("- Include all subsections and details if found");
                prompt.AppendLine("- If section exists in context, provide complete information");
                prompt.AppendLine();
            }

            // Add document-specific guidance based on found content
            var policyTypes = chunks.Select(c => DetermineDocumentType(c.Source)).Distinct().ToList();
            if (policyTypes.Any())
            {
                prompt.AppendLine("📋 AVAILABLE POLICY INFORMATION:");
                foreach (var policyType in policyTypes)
                {
                    prompt.AppendLine($"• {policyType}");
                }
                prompt.AppendLine();
            }

            // Add common abbreviations found in context
            var abbreviations = ExtractAbbreviationsFromQuery(query, chunks);
            if (abbreviations.Any())
            {
                prompt.AppendLine("📖 RELEVANT ABBREVIATIONS:");
                foreach (var abbrev in abbreviations)
                {
                    prompt.AppendLine($"• {abbrev.Key} = {abbrev.Value}");
                }
                prompt.AppendLine();
            }

            prompt.AppendLine("ANSWER FORMAT:");
            prompt.AppendLine("- Use clear headings and bullet points");
            prompt.AppendLine("- Cite sources as [DocumentName: filename]");
            prompt.AppendLine("- Be specific about section numbers");
            prompt.AppendLine("- Provide complete information when available");
            prompt.AppendLine();
            prompt.AppendLine("Remember: Check the context thoroughly before saying any section or information doesn't exist.");

            return prompt.ToString();
        }

        private Dictionary<string, string> ExtractAbbreviationsFromQuery(string query, List<RelevantChunk> chunks)
        {
            var abbreviations = new Dictionary<string, string>();
            var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Check if query contains common abbreviations
            var commonAbbrevs = new Dictionary<string, string>
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
                ["sop"] = "Standard Operating Procedure"
            };

            foreach (var word in queryWords)
            {
                if (commonAbbrevs.ContainsKey(word))
                {
                    abbreviations[word.ToUpper()] = commonAbbrevs[word];
                }
            }

            return abbreviations;
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

        private bool HasManyAbbreviations(string query)
        {
            var abbreviations = new[] { "cl", "sl", "coff", "el", "pl", "ml", "hr", "isms", "ehs", "sop" };
            var queryLower = query.ToLowerInvariant();
            return abbreviations.Count(abbr => queryLower.Contains(abbr)) >= 2;
        }

        private string FormatAbbreviations(Dictionary<string, string> abbreviations)
        {
            if (!abbreviations.Any()) return "";

            return string.Join("\n", abbreviations.Select(kvp => $"• {kvp.Key} = {kvp.Value}"));
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
        // In your BuildOptimizedContext method, ensure critical policy information is highlighted
        private string BuildOptimizedContext(List<RelevantChunk> chunks, string plant)
        {
            if (!chunks.Any()) return "";

            var contextBuilder = new StringBuilder(2048);
            contextBuilder.AppendLine($"=== POLICY INFORMATION FOR {plant.ToUpper()} ===");

            // Process high-relevance chunks first and highlight key restrictions
            var topChunks = chunks
                .Where(c => c.Similarity >= 0.3)
                .OrderByDescending(c => c.Similarity)
                .Take(5)
                .ToList();

            foreach (var chunk in topChunks)
            {
                contextBuilder.AppendLine($"\n📄 {chunk.Source}:");

                // Highlight important restrictions/rules
                var text = chunk.Text;
                if (text.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("restricted", StringComparison.OrdinalIgnoreCase))
                {
                    contextBuilder.AppendLine("⚠️ IMPORTANT RESTRICTION:");
                }

                contextBuilder.AppendLine(text);
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
        private string BuildContextualizedChunk(
    string chunkContent,
    string sectionId,
    string title,
    string documentType,
    string sourceFile)
        {
            var contextBuilder = new StringBuilder();

            // Add document context header
            contextBuilder.AppendLine($"=== {documentType.ToUpper()} DOCUMENT ===");
            contextBuilder.AppendLine($"Source: {Path.GetFileNameWithoutExtension(sourceFile)}");

            if (!string.IsNullOrEmpty(sectionId))
            {
                contextBuilder.AppendLine($"Section: {sectionId}");
                if (!string.IsNullOrEmpty(title))
                {
                    contextBuilder.AppendLine($"Title: {title}");
                }
            }

            contextBuilder.AppendLine("=== CONTENT ===");
            contextBuilder.AppendLine(chunkContent.Trim());

            // Add searchable keywords
            contextBuilder.AppendLine("=== KEYWORDS ===");
            var keywords = GenerateSearchKeywords(sectionId, title, documentType, chunkContent);
            contextBuilder.AppendLine(string.Join(", ", keywords));

            return contextBuilder.ToString();
        }

        private List<string> GenerateSearchKeywords(string sectionId, string title, string documentType, string content)
        {
            var keywords = new List<string>();

            // Document type keywords
            keywords.Add(documentType.ToLower());

            // Section keywords
            if (!string.IsNullOrEmpty(sectionId))
            {
                keywords.Add(sectionId.ToLower());
                keywords.Add(sectionId.Replace("Section ", "section ").ToLower());
                keywords.Add(sectionId.Replace(" ", "").ToLower()); // "section6"

                // Extract number
                var match = Regex.Match(sectionId, @"(\d+(?:\.\d+)*)");
                if (match.Success)
                {
                    keywords.Add($"section {match.Groups[1].Value}");
                    keywords.Add($"section{match.Groups[1].Value}");
                }
            }

            // Title keywords
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = title.ToLower()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2);
                keywords.AddRange(titleWords);
            }

            // Content-based keywords
            var contentKeywords = ExtractImportantTerms(content);
            keywords.AddRange(contentKeywords);

            return keywords.Distinct().ToList();
        }

        private List<string> ExtractImportantTerms(string content)
        {
            var terms = new List<string>();
            var lowerContent = content.ToLower();

            // Common policy terms
            var policyTerms = new[]
            {
        "policy", "procedure", "rule", "regulation", "guideline",
        "employee", "management", "security", "information",
        "leave", "attendance", "performance", "training"
    };

            foreach (var term in policyTerms)
            {
                if (lowerContent.Contains(term))
                {
                    terms.Add(term);
                }
            }

            return terms;
        }

        private string DetermineDocumentType(string sourceFile)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourceFile).ToLower();

            if (fileName.Contains("isms")) return "ISMS";
            if (fileName.Contains("hr")) return "HR Policy";
            if (fileName.Contains("safety")) return "Safety Policy";
            if (fileName.Contains("security")) return "Security Policy";
            if (fileName.Contains("employee")) return "Employee Handbook";
            if (fileName.Contains("general")) return "General Policy";

            return "Policy Document";
        }

        private string CleanCopyPasteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 🔧 STEP 1: Fix common Word-to-Notepad conversion issues
            var cleaned = text
                // Fix dash variations (common in Word-to-Notepad copy
                .Replace("â€œ", "\"")     // Common encoding for smart quotes
                .Replace("â€", "\"")      // Another smart quote variant
                .Replace("â€™", "'")      // Smart apostrophe
                .Replace("â€¢", "•")      // Bullet point
                .Replace("Â ", " ")       // Non-breaking space issues

                // Normalize different dash types to standard em dash
                .Replace("—", "–")        // En dash to em dash
                .Replace("--", "–")       // Double hyphen to em dash
                .Replace(" - ", " – ")    // Spaced hyphen to spaced em dash

                // Fix section header patterns that might be broken
                .Replace("Section1", "Section 1")
                .Replace("Section2", "Section 2")
                .Replace("Section3", "Section 3")
                .Replace("Section4", "Section 4")
                .Replace("Section5", "Section 5")
                .Replace("Section6", "Section 6")
                .Replace("Section7", "Section 7")
                .Replace("Section8", "Section 8")
                .Replace("Section9", "Section 9")
                .Replace("Section10", "Section 10")
                .Replace("Section11", "Section 11")
                .Replace("Section12", "Section 12")
                .Replace("Section13", "Section 13")
                .Replace("Section14", "Section 14")
                .Replace("Section15", "Section 15")
                .Replace("Section16", "Section 16")
                .Replace("Section17", "Section 17")
                .Replace("Section18", "Section 18")
                .Replace("Section19", "Section 19")
                .Replace("Section20", "Section 20")
                .Replace("Section10", "Section 10")

                ;

            // 🔧 STEP 2: Fix line break issues
            cleaned = cleaned
                .Replace("\r\n", "\n")    // Normalize line endings
                .Replace("\r", "\n")      // Handle old Mac line endings

                // Fix cases where section headers got split across lines
                .Replace("Section\n1", "Section 1")
                .Replace("Section\n2", "Section 2")

                .Replace("Section\n3", "Section 3")
                .Replace("Section\n4", "Section 4")
                .Replace("Section\n5", "Section 5")
                .Replace("Section\n6", "Section 6")
                .Replace("Section\n7", "Section 7")
                .Replace("Section\n8", "Section 8")
                .Replace("Section\n9", "Section 9")
                .Replace("Section\n10", "Section 10")
                .Replace("Section\n11", "Section 11")
                .Replace("Section\n12", "Section 12")
                .Replace("Section\n13", "Section 13")
                .Replace("Section\n14", "Section 14")
                .Replace("Section\n15", "Section 15")
                .Replace("Section\n16", "Section 16")
                .Replace("Section\n17", "Section 17")
                .Replace("Section\n18", "Section 18")
                .Replace("Section\n19", "Section 19")
                .Replace("Section\n20", "Section 20")

                ;

            // 🔧 STEP 3: Fix broken section numbering
            var sectionHeaderPattern = @"Section\s*(\d+)\s*[–\-—]?\s*(.+)";
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                sectionHeaderPattern,
                "Section $1 – $2",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            // 🔧 STEP 4: Remove excessive whitespace but preserve structure
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[ \t]+", " ");  // Multiple spaces to single
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n"); // Multiple newlines to double

            return cleaned.Trim();
        }

        private List<(string Text, string SourceFile, string SectionId, string Title)> ChunkText(
            string text, string sourceFile, int maxTokens = 2500)
        {
            text = CleanCopyPasteText(text);

            // ENHANCED: Comprehensive section patterns for ALL sections
            var sectionPatterns = new[]
            {
        @"^Section\s+(\d+)\s*[–\-—\u2013\u2014\u002D]?\s*(.+)$", // Standard format
        @"^Section\s*(\d+)\s*[:\-]?\s*(.*)$", // More flexible section format
        @"^SECTION\s+(\d+)\s*[:\-]?\s*(.*)$", // Uppercase section
        @"^(\d+)\.\s+(.+)$", // "1. Introduction", "2. Scope"
        @"^(\d+)\s+(.+)$", // "1 Introduction"
        @"^(\d+\.\d+)\s+(.+)$", // "1.1 Purpose"
        @"^(\d+\.\d+\.\d+)\s+(.+)$", // "1.1.1 Definition"
        @"^(\d+)\s*[:\-\.]\s*(.+)$" // Number with colon/dash/dot
    };

            // Rest of your chunking logic with enhanced logging
            var chunks = new List<(string Text, string SourceFile, string SectionId, string Title)>();
            var lines = text.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            var currentChunk = new StringBuilder();
            string currentSectionId = "";
            string currentTitle = "";
            int tokenCount = 0;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                bool foundSection = false;
                string detectedSectionId = "";
                string detectedTitle = "";

                // Check for section headers
                foreach (var pattern in sectionPatterns)
                {
                    var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        foundSection = true;
                        if (trimmed.ToLowerInvariant().StartsWith("section"))
                        {
                            detectedSectionId = $"Section {match.Groups[1].Value}";
                            detectedTitle = match.Groups[2].Value.Trim();
                        }
                        else
                        {
                            detectedSectionId = match.Groups[1].Value;
                            detectedTitle = match.Groups[2].Value.Trim();
                        }

                        _logger.LogInformation($"✅ Detected section: {detectedSectionId} - {detectedTitle}");
                        break;
                    }
                }

                if (foundSection)
                {
                    // Save previous chunk
                    if (currentChunk.Length > 0)
                    {
                        var completeChunk = BuildComprehensiveChunk(
                            currentSectionId, currentTitle, currentChunk.ToString(), "");
                        chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));

                        // Debug logging for any section
                        _logger.LogInformation($"🔍 STORING CHUNK:");
                        _logger.LogInformation($"Section: {currentSectionId} - {currentTitle}");
                        _logger.LogInformation($"Length: {completeChunk.Length}");
                    }

                    // Start new section
                    currentSectionId = detectedSectionId;
                    currentTitle = detectedTitle;
                    currentChunk.Clear();
                    currentChunk.AppendLine($"=== {currentSectionId}: {currentTitle} ===");
                    tokenCount = EstimateTokenCount($"{currentSectionId}: {currentTitle}");
                }

                // Add line to current chunk
                int lineTokens = EstimateTokenCount(trimmed);
                if (tokenCount + lineTokens > maxTokens && currentChunk.Length > 0)
                {
                    // Split large sections
                    var completeChunk = BuildComprehensiveChunk(
                        currentSectionId, currentTitle, currentChunk.ToString(), "");
                    chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));

                    currentChunk.Clear();
                    currentChunk.AppendLine($"=== {currentSectionId}: {currentTitle} (continued) ===");
                    tokenCount = EstimateTokenCount($"{currentSectionId}: {currentTitle} (continued)");
                }

                currentChunk.AppendLine(trimmed);
                tokenCount += lineTokens;
            }

            // Add final chunk
            if (currentChunk.Length > 0)
            {
                var completeChunk = BuildComprehensiveChunk(
                    currentSectionId, currentTitle, currentChunk.ToString(), "");
                chunks.Add((completeChunk, sourceFile, currentSectionId, currentTitle));
            }

            _logger.LogInformation($"📄 Created {chunks.Count} enhanced chunks from {sourceFile}");

            // Log all sections found
            var allSections = chunks.Where(c => !string.IsNullOrEmpty(c.SectionId))
                .Select(c => c.SectionId).Distinct().ToList();
            _logger.LogInformation($"🎯 All sections found: {string.Join(", ", allSections)}");

            return chunks;
        }


        private List<(string Text, string SourceFile, string SectionId, string Title)> ChunkByParagraphs(
    string text, string sourceFile, int maxTokens = 2500)
        {
            var chunks = new List<(string Text, string SourceFile, string SectionId, string Title)>();
            var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" },
                StringSplitOptions.RemoveEmptyEntries);

            var currentChunk = new StringBuilder();
            int chunkNumber = 1;
            int tokenCount = 0;

            foreach (var paragraph in paragraphs)
            {
                var trimmed = paragraph.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                int paragraphTokens = EstimateTokenCount(trimmed);

                if (tokenCount + paragraphTokens > maxTokens && currentChunk.Length > 0)
                {
                    chunks.Add((
                        currentChunk.ToString().Trim(),
                        sourceFile,
                        $"Chunk {chunkNumber}",
                        $"Content Block {chunkNumber}"
                    ));

                    currentChunk.Clear();
                    tokenCount = 0;
                    chunkNumber++;
                }

                currentChunk.AppendLine(trimmed);
                tokenCount += paragraphTokens;
            }

            if (currentChunk.Length > 0)
            {
                chunks.Add((
                    currentChunk.ToString().Trim(),
                    sourceFile,
                    $"Chunk {chunkNumber}",
                    $"Content Block {chunkNumber}"
                ));
            }

            _logger.LogInformation($"📄 Created {chunks.Count} paragraph-based chunks from {sourceFile}");
            return chunks;
        }


        private string BuildComprehensiveChunk(string sectionId, string title, string content, string pendingContent)
        {
            var chunkBuilder = new StringBuilder();

            // Add comprehensive header
            chunkBuilder.AppendLine($"DOCUMENT SECTION: {sectionId}");
            chunkBuilder.AppendLine($"SECTION TITLE: {title}");
            chunkBuilder.AppendLine($"DOCUMENT TYPE: ISMS Policy");
            chunkBuilder.AppendLine("===== CONTENT =====");

            // Add any pending content from previous processing
            if (!string.IsNullOrEmpty(pendingContent))
            {
                chunkBuilder.AppendLine(pendingContent);
            }

            // Add main content
            chunkBuilder.AppendLine(content);

            // Add searchable keywords
            chunkBuilder.AppendLine("===== KEYWORDS =====");
            var keywords = GenerateSearchableKeywords(sectionId, title, content);
            chunkBuilder.AppendLine(string.Join(", ", keywords));

            return chunkBuilder.ToString();
        }

        private List<string> GenerateSearchableKeywords(string sectionId, string title, string content)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Section-specific keywords
            if (sectionId.Contains("6"))
            {
                keywords.Add("Section 6");
                keywords.Add("section six");
                keywords.Add("Physical Security");
                keywords.Add("physical security");
                keywords.Add("Secure Areas");
                keywords.Add("secure areas");
                keywords.Add("Area Level");
                keywords.Add("area level");
                keywords.Add("Access Control");
                keywords.Add("access control");
                keywords.Add("Device Protection");
                keywords.Add("device protection");
            }

            // Extract keywords from title
            if (!string.IsNullOrEmpty(title))
            {
                var titleWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in titleWords.Where(w => w.Length > 2))
                {
                    keywords.Add(word);
                }
            }

            // Extract important terms from content
            var importantTerms = new[]
            {
        "ISMS", "Information Security", "Management System", "Policy", "Procedure",
        "Employee", "Access", "Control", "Security", "Management", "Protection"
    };

            foreach (var term in importantTerms)
            {
                if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    keywords.Add(term);
                }
            }

            return keywords.Take(20).ToList(); // Limit keywords to prevent bloat
        }

        // Enhanced token estimation for better accuracy
        private int EstimateTokenCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            // More accurate token estimation
            // Average ~4 characters per token, but adjust for punctuation and formatting
            var wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries).Length;
            var charCount = text.Length;

            // Use a hybrid approach: word count * 1.3 + char count / 5
            return (int)Math.Ceiling(wordCount * 1.3 + charCount / 5.0);
        }

        // Alternative: Semantic chunking for policy documents
        private List<(string Text, string SourceFile, string Context)> ChunkBySemanticBlocks(
            string text, string sourceFile, int maxTokens = 3000)
        {
            var chunks = new List<(string Text, string SourceFile, string Context)>();
            var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" },
                StringSplitOptions.RemoveEmptyEntries);

            var currentBlock = new StringBuilder();
            string currentContext = "";
            int tokenCount = 0;

            foreach (var paragraph in paragraphs)
            {
                string trimmed = paragraph.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Detect context changes (policy sections, rules, procedures)
                string detectedContext = DetectContext(trimmed);
                int paragraphTokens = EstimateTokenCount(trimmed);

                // If context changes or token limit reached, create new chunk
                if ((detectedContext != currentContext && currentBlock.Length > 0) ||
                    (tokenCount + paragraphTokens > maxTokens && currentBlock.Length > 0))
                {
                    chunks.Add((
                        currentBlock.ToString().Trim(),
                        sourceFile,
                        currentContext
                    ));
                    currentBlock.Clear();
                    tokenCount = 0;
                }

                if (!string.IsNullOrEmpty(detectedContext))
                    currentContext = detectedContext;

                currentBlock.AppendLine(trimmed);
                tokenCount += paragraphTokens;
            }

            if (currentBlock.Length > 0)
            {
                chunks.Add((
                    currentBlock.ToString().Trim(),
                    sourceFile,
                    currentContext
                ));
            }

            return chunks;
        }

        private string DetectContext(string text)
        {
            var lowerText = text.ToLower();

            if (lowerText.Contains("leave") || lowerText.Contains("vacation") || lowerText.Contains("attendance"))
                return "Leave Policies";
            if (lowerText.Contains("safety") || lowerText.Contains("emergency") || lowerText.Contains("health"))
                return "Safety & Health";
            if (lowerText.Contains("disciplinary") || lowerText.Contains("misconduct") || lowerText.Contains("termination"))
                return "Disciplinary Actions";
            if (lowerText.Contains("benefit") || lowerText.Contains("insurance") || lowerText.Contains("welfare"))
                return "Benefits & Welfare";
            if (lowerText.Contains("recruitment") || lowerText.Contains("promotion") || lowerText.Contains("training"))
                return "HR Policies";
            if (lowerText.Contains("compliance") || lowerText.Contains("ethics") || lowerText.Contains("conduct"))
                return "Compliance & Ethics";

            return "General Policies";
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
            var commonWords = new[]
            {
        // Articles
        "a", "an", "the",
        // Pronouns  
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
        "my", "your", "his", "her", "its", "our", "their", "mine", "yours", "hers", "ours", "theirs",
        "this", "that", "these", "those", "who", "what", "which", "when", "where", "why", "how",
        // Prepositions
        "in", "on", "at", "by", "for", "with", "about", "to", "from", "of", "into", "onto", "upon",
        "over", "under", "above", "below", "between", "among", "through", "during", "before", "after",
        // Conjunctions  
        "and", "or", "but", "so", "yet", "nor", "for", "because", "since", "although", "though",
        "while", "whereas", "unless", "until", "if", "whether",
        // Common verbs
        "is", "am", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does",
        "did", "will", "would", "could", "should", "may", "might", "can", "must", "shall",
        "get", "got", "go", "goes", "went", "come", "came", "make", "made", "take", "took", "give", "gave",
        // Common adjectives/adverbs
        "good", "bad", "big", "small", "new", "old", "first", "last", "long", "short", "high", "low",
        "much", "many", "some", "any", "all", "no", "more", "most", "less", "few", "little",
        "very", "too", "also", "just", "only", "even", "still", "already", "yet", "again",
        // Question words and common query terms
        "tell", "show", "explain", "describe", "list", "give", "provide", "find", "search",
        "want", "need", "like", "know", "think", "see", "look", "help", "please", "thanks", "thank"
    };

            return commonWords.Contains(word.ToLowerInvariant());
        }

        private bool HasSectionReference(string text)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(text,
                @"\b(section|clause|part|paragraph)\s+\d+|^\d+\.\d+|\b\d+\s+(introduction|scope|definitions|context|leadership|physical|planning|operation|performance|improvement)");
        }

        private string ExtractSectionReference(string text)
        {
            // Try different patterns
            var patterns = new[]
            {
        @"(section|clause|part|paragraph)\s+(\d+(?:\.\d+)?)",
        @"(\d+\.\d+(?:\.\d+)?)",
        @"section\s*(\d+)",
        @"(\d+)\s+(introduction|scope|definitions|context|leadership|physical|planning|operation|performance|improvement)"
    };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (match.Groups.Count > 2)
                        return $"{match.Groups[1].Value} {match.Groups[2].Value}";
                    else
                        return $"Section {match.Groups[1].Value}";
                }
            }

            return "";
        }


        
        private bool IsTopicChanged(string question, ConversationContext context)
        {
            if (string.IsNullOrWhiteSpace(question)) return true;

            var lowerQuestion = question.ToLowerInvariant();

            // 🆕 ADD THIS AT THE BEGINNING - Generic section change detection
            if (HasSectionReference(lowerQuestion))
            {
                var currentSection = ExtractSectionReference(lowerQuestion);
                if (context.History.Any())
                {
                    var lastQuestion = context.History.Last().Question.ToLowerInvariant();
                    if (HasSectionReference(lastQuestion))
                    {
                        var lastSection = ExtractSectionReference(lastQuestion);
                        if (currentSection != lastSection)
                        {
                            _logger.LogDebug($"🚫 Section change detected: {lastSection} → {currentSection}");
                            return true;
                        }
                    }
                }
            }
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
        //private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        //{
        //    if (string.IsNullOrWhiteSpace(text))
        //        return new List<float>();

        //    var cacheKey = $"{model.Name}:{text.GetHashCode():X}";

        //    // Single cache check with time-based expiration
        //    if (_optimizedEmbeddingCache.TryGetValue(cacheKey, out var cached))
        //    {
        //        if (DateTime.Now - cached.Cached < TimeSpan.FromHours(24))
        //            return cached.Embedding;
        //        else
        //            _optimizedEmbeddingCache.TryRemove(cacheKey, out _);
        //    }
        //    using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Reduced timeout

        //    if (!await _globalEmbeddingSemaphore.WaitAsync(100, cts1.Token)) // Quick timeout
        //    {
        //        _logger.LogWarning($"⚠️ Embedding semaphore timeout, using fallback");
        //        return new List<float>(); // Return empty to fail fast
        //    }
        //    try
        //    {

        //        // 🔧 ENHANCED: Better text cleaning to prevent encoding issues
        //        var processedText = CleanTextForEmbedding(text, model);

        //        var request = new
        //        {
        //            model = model.Name,
        //            prompt = processedText,
        //            options = new
        //            {
        //                num_ctx = 1024, // Reduced context
        //                temperature = 0 // Deterministic for caching
        //            }
        //        };

        //        _logger.LogDebug($"🔤 Sending embedding request for model {model.Name}, text length: {processedText.Length}");

        //        using var requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        //        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, requestCts.Token);

        //        if (!response.IsSuccessStatusCode)
        //        {
        //            var errorContent = await response.Content.ReadAsStringAsync();
        //            _logger.LogError($"❌ Embedding request failed: {response.StatusCode} - {errorContent}");
        //            _logger.LogError($"❌ Failed text preview: {processedText.Substring(0, Math.Min(200, processedText.Length))}");
        //            return new List<float>();
        //        }

        //        var json = await response.Content.ReadAsStringAsync();
        //        using var doc = JsonDocument.Parse(json);

        //        if (!doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
        //        {
        //            _logger.LogError($"❌ No embedding property in response for model {model.Name}");
        //            return new List<float>();
        //        }

        //        var embedding = embeddingProperty.EnumerateArray()
        //            .Select(x => x.GetSingle())
        //            .ToList();

        //        // Validate embedding dimensions
        //        var isNomicModel = model.Name.Contains("nomic", StringComparison.OrdinalIgnoreCase);
        //        if (isNomicModel && embedding.Count != 768)
        //        {
        //            _logger.LogError($"❌ nomic-embed-text should produce 768 dimensions, got {embedding.Count}");
        //            return new List<float>();
        //        }

        //        // Cache with automatic cleanup
        //        if (_optimizedEmbeddingCache.Count < 5000)
        //        {
        //            _optimizedEmbeddingCache.TryAdd(cacheKey, (embedding, DateTime.Now, 5));
        //        }
        //        else
        //        {
        //            // Clean old entries if cache is full
        //            var expiredKeys = _optimizedEmbeddingCache
        //                .Where(kvp => DateTime.UtcNow - kvp.Value.Cached > TimeSpan.FromHours(12))
        //                .Select(kvp => kvp.Key)
        //                .Take(1000)
        //                .ToList();
        //            foreach (var key in expiredKeys)
        //                _optimizedEmbeddingCache.TryRemove(key, out _);
        //            _optimizedEmbeddingCache.TryAdd(cacheKey, (embedding, DateTime.Now, 5));
        //        }

        //        return embedding;
        //    }
        //    finally
        //    {
        //        _globalEmbeddingSemaphore.Release();
        //    }
        //}

        private string CleanTextForEmbedding(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var isNomicModel = model.Name.Contains("nomic", StringComparison.OrdinalIgnoreCase);

            // 🔧 STEP 1: Remove problematic characters that might cause encoding issues
            var cleaned = text
                // Replace various dash types with standard hyphen
                .Replace("–", "-")  // em dash
                .Replace("—", "-")  // en dash  
                .Replace("−", "-")  // minus sign
                                    // Replace smart quotes - FIXED with Unicode escapes
                .Replace("\u201C", "\"")  // Left double quotation mark
                .Replace("\u201D", "\"")  // Right double quotation mark
                .Replace("\u2018", "'")   // Left single quotation mark
                .Replace("\u2019", "'")   // Right single quotation mark
                                          // Remove other problematic Unicode characters
                .Replace("…", "...")
                .Replace("•", "*")
                .Replace("▪", "*");

            // Rest of your existing method...

            // 🔧 STEP 2: Normalize whitespace
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            cleaned = cleaned.Trim();

            // 🔧 STEP 3: Remove non-printable characters
            cleaned = Regex.Replace(cleaned, @"[^\x20-\x7E\t\n\r]", " ");

            // 🔧 STEP 4: Ensure proper length limits for different models
            int maxLength = isNomicModel ? 2000 : 1000;
            if (cleaned.Length > maxLength)
            {
                // Try to cut at sentence boundary
                var sentences = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var truncated = "";
                foreach (var sentence in sentences)
                {
                    if (truncated.Length + sentence.Length + 1 <= maxLength)
                    {
                        truncated += sentence + ".";
                    }
                    else
                    {
                        break;
                    }
                }

                if (truncated.Length > 10)
                {
                    cleaned = truncated;
                }
                else
                {
                    cleaned = cleaned.Substring(0, maxLength);
                }
            }

            // 🔧 STEP 5: Final validation
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                _logger.LogWarning($"⚠️ Text cleaning resulted in empty string for model {model.Name}");
                return "empty content";
            }

            _logger.LogDebug($"✅ Cleaned text for {model.Name}: {cleaned.Length} chars");
            return cleaned;
        }




        private string PreprocessForNomic(string text)
        {
            // Clean text for better nomic performance
            var cleaned = text.Trim();
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
            // Nomic works well with up to 2048 tokens
            if (cleaned.Length > 2000)
            {
                cleaned = cleaned.Substring(0, 2000);
            }
            return cleaned;
        }

        private string CleanText(string text)
        {
            // Replace problematic Unicode characters first
            text = text
        // Replace various dash types with standard hyphen
        .Replace("–", "-")  // em dash
        .Replace("—", "-")  // en dash  
        .Replace("−", "-")  // minus sign
                            // Replace smart quotes - FIXED with Unicode escapes
        .Replace("\u201C", "\"")  // Left double quotation mark
        .Replace("\u201D", "\"")  // Right double quotation mark
        .Replace("\u2018", "'")   // Left single quotation mark
        .Replace("\u2019", "'")   // Right single quotation mark
                                  // Remove other problematic Unicode characters
        .Replace("…", "...")
        .Replace("•", "*")
        .Replace("▪", "*");

            // Clean whitespace and special characters
            text = Regex.Replace(text, @"\s+", " ");
            text = Regex.Replace(text, @"[^\w\s.,!?-]", "");
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            return text.Trim();
        }


        private async Task ProcessChunkBatchForModelAsync(
    List<(string Text, string SourceFile, string SectionId, string Title)> chunks,
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

                // 🔧 DEBUG: Log problematic chunks
                _logger.LogInformation($"🔄 Processing {validChunks.Count} chunks for model {model.Name}");

                // Check which chunks already exist (batch check)
                var existingChunks = await CheckExistingChunksAsync(collectionId, validChunks.Select(c => c.ChunkId).ToList());
                var newChunks = validChunks.Where(c => !existingChunks.Contains(c.ChunkId)).ToList();

                if (!newChunks.Any())
                {
                    _logger.LogDebug($"All chunks already exist for {model.Name}");
                    return;
                }

                _logger.LogInformation($"📝 Generating embeddings for {newChunks.Count} new chunks");

                // 🔧 Process chunks individually to identify problematic ones
                var successfulChunks = new List<(string Text, string ChunkId, List<float> Embedding)>();

                for (int i = 0; i < newChunks.Count; i++)
                {
                    try
                    {
                        _logger.LogDebug($"🔤 Processing chunk {i + 1}/{newChunks.Count}: {newChunks[i].ChunkId}");
                        var embedding = await GetEmbeddingAsync(newChunks[i].Text, model);

                        if (embedding.Count > 0)
                        {
                            successfulChunks.Add((newChunks[i].Text, newChunks[i].ChunkId, embedding));
                        }
                        else
                        {
                            _logger.LogWarning($"⚠️ Failed to generate embedding for chunk: {newChunks[i].ChunkId}");
                            _logger.LogWarning($"⚠️ Problematic text preview: {newChunks[i].Text.Substring(0, Math.Min(100, newChunks[i].Text.Length))}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Failed to process individual chunk: {newChunks[i].ChunkId}");
                        _logger.LogError($"❌ Problematic text: {newChunks[i].Text.Substring(0, Math.Min(200, newChunks[i].Text.Length))}");
                    }
                }

                if (successfulChunks.Any())
                {
                    // Prepare data for ChromaDB
                    var documents = successfulChunks.Select(c => c.Text).ToList();
                    var ids = successfulChunks.Select(c => c.ChunkId).ToList();
                    var embeddings = successfulChunks.Select(c => c.Embedding).ToList();
                    var metadatas = successfulChunks.Select(c =>
                        CreateChunkMetadata(newChunks.First(nc => nc.ChunkId == c.ChunkId).SourceFile,
                                          lastModified, model.Name, c.Text, plant)).ToList();

                    await AddToChromaDBAsync(collectionId, ids, embeddings, documents, metadatas);
                    _logger.LogInformation($"✅ Added {successfulChunks.Count} new chunks for model {model.Name}");
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
                client.Timeout = TimeSpan.FromSeconds(15); // Reduced from 30
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");
                client.DefaultRequestHeaders.Add("Keep-Alive", "timeout=30, max=100");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
            {
                MaxConnectionsPerServer = 50, // Increased
                UseCookies = false,
                UseProxy = false // Disable proxy if not needed
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
                    _logger.LogInformation($"📄 Chunk: {chunk.Source} | Similarity: {chunk.Similarity:F3} | Text: {chunk.Text.Substring(0, Math.Max(100, chunk.Text.Length - 1))}");
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

            _logger.LogInformation($"🔍 Enhanced search for: '{query}' in plant: '{plant}'");

            try
            {
                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);

                // Detect if this is a section-based query
                var sectionQuery = await DetectAndParseSection(query);

                if (sectionQuery != null)
                {
                    return await SearchForSpecificSection(sectionQuery, embeddingModel, maxResults, plant, collectionId);
                }
                else
                {
                    return await SearchGeneral(query, embeddingModel, maxResults, plant, collectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Enhanced search failed for: {query}");
                return new List<RelevantChunk>();
            }
        }

        private async Task<SectionQuery?> DetectAndParseSection(string query)
        {
            var lowerQuery = query.ToLower();

            // Pattern 1: "section X of [document type]" 
            var match1 = Regex.Match(lowerQuery, @"section\s+(\d+(?:\.\d+)*)\s+of\s+(\w+)");
            if (match1.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match1.Groups[1].Value,
                    DocumentType = match1.Groups[2].Value.ToUpper(),
                    OriginalQuery = query
                };
            }

            // Pattern 2: "[Document type] section X"
            var match2 = Regex.Match(lowerQuery, @"(\w+)\s+section\s+(\d+(?:\.\d+)*)");
            if (match2.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match2.Groups[2].Value,
                    DocumentType = match2.Groups[1].Value.ToUpper(),
                    OriginalQuery = query
                };
            }

            // Pattern 3: "section X" (any number - will search across all policy types)
            var match3 = Regex.Match(lowerQuery, @"section\s+(\d+(?:\.\d+)*)");
            if (match3.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match3.Groups[1].Value,
                    DocumentType = DetectDocumentTypeFromContext(lowerQuery), // Dynamic detection
                    OriginalQuery = query
                };
            }

            // Pattern 4: Just number references like "what is 5.2"
            var match4 = Regex.Match(lowerQuery, @"(?:what\s+is\s+|tell\s+me\s+about\s+)?(\d+\.\d+(?:\.\d+)*)");
            if (match4.Success)
            {
                return new SectionQuery
                {
                    SectionNumber = match4.Groups[1].Value,
                    DocumentType = DetectDocumentTypeFromContext(lowerQuery),
                    OriginalQuery = query
                };
            }

            // Pattern 5: Topic-based dynamic section detection - NOW AWAITED
            var topicBasedSection = await DetectSectionByTopicDynamic(lowerQuery);
            if (topicBasedSection != null)
            {
                return topicBasedSection;
            }

            return null;
        }

        private async Task RefreshDynamicMappings()
        {
            if (!await _mappingRefreshSemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
                return; // Skip if another refresh is in progress

            try
            {
                _logger.LogInformation("🔄 Refreshing dynamic section mappings...");

                var discoveredMappings = await DiscoverPolicySectionsFromDocuments();

                // Clear and update cache
                _dynamicSectionMappings.Clear();
                foreach (var mapping in discoveredMappings)
                {
                    _dynamicSectionMappings.TryAdd(mapping.Key, mapping.Value);
                }

                _lastMappingRefresh = DateTime.UtcNow;
                _logger.LogInformation($"✅ Refreshed mappings for {_dynamicSectionMappings.Count} policy types");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh dynamic mappings");
            }
            finally
            {
                _mappingRefreshSemaphore.Release();
            }
        }
        private async Task<Dictionary<string, Dictionary<string, string>>> GetDynamicSectionMappings()
        {
            // Refresh mappings every 6 hours or if empty
            if (DateTime.Now - _lastMappingRefresh > TimeSpan.FromHours(6) || !_dynamicSectionMappings.Any())
            {
                await RefreshDynamicMappings();
            }

            return _dynamicSectionMappings.ToDictionary(k => k.Key, v => v.Value);
        }

        private async Task<SectionQuery?> DetectSectionByTopicDynamic(string lowerQuery)
        {
            // Get dynamic mappings (will auto-refresh if needed)
            var dynamicMappings = await GetDynamicSectionMappings();

            // Try to detect document type first
            string detectedDocType = DetectDocumentTypeFromContext(lowerQuery);

            if (!string.IsNullOrEmpty(detectedDocType) && dynamicMappings.ContainsKey(detectedDocType))
            {
                var mappings = dynamicMappings[detectedDocType];
                foreach (var mapping in mappings)
                {
                    var keywords = mapping.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(k => k.Trim().ToLowerInvariant());

                    if (keywords.Any(keyword => lowerQuery.Contains(keyword)))
                    {
                        return new SectionQuery
                        {
                            SectionNumber = mapping.Key,
                            DocumentType = detectedDocType,
                            OriginalQuery = lowerQuery
                        };
                    }
                }
            }
            else
            {
                // Search across all document types dynamically
                foreach (var docType in dynamicMappings)
                {
                    foreach (var mapping in docType.Value)
                    {
                        var keywords = mapping.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(k => k.Trim().ToLowerInvariant());

                        if (keywords.Any(keyword => lowerQuery.Contains(keyword)))
                        {
                            return new SectionQuery
                            {
                                SectionNumber = mapping.Key,
                                DocumentType = docType.Key,
                                OriginalQuery = lowerQuery
                            };
                        }
                    }
                }
            }

            // Fallback: Use universal pattern analysis
            return AnalyzeQueryForSectionContent(lowerQuery);
        }

        public async Task LearnFromSuccessfulQuery(string query, string sectionNumber, string documentType, List<RelevantChunk> chunks)
        {
            try
            {
                // Extract successful keywords from the query
                var queryKeywords = ExtractTopicsFromQuery(query.ToLowerInvariant());

                // Learn associations
                var key = $"{documentType}_{sectionNumber}";
                if (!_learnedAssociations.ContainsKey(key))
                {
                    _learnedAssociations[key] = new List<string>();
                }

                // Add new keywords that led to successful results
                foreach (var keyword in queryKeywords.Take(3)) // Limit to avoid noise
                {
                    if (!_learnedAssociations[key].Contains(keyword, StringComparer.OrdinalIgnoreCase))
                    {
                        _learnedAssociations[key].Add(keyword);
                    }
                }

                _logger.LogDebug($"📚 Learned association: {key} -> {string.Join(", ", queryKeywords)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to learn from successful query");
            }
        }

        private List<string> ExtractTopicsFromQuery(string query)
        {
            var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lowerQuery = query.ToLowerInvariant();

            // Split query into words and filter meaningful terms
            var words = lowerQuery
                .Split(new char[] { ' ', ',', '.', '?', '!', ':', ';', '-', '_', '(', ')', '[', ']' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length > 2 && !IsCommonWord(word))
                .ToList();

            // Add significant words as topics
            topics.UnionWith(words);

            // Extract compound terms (phrases)
            var compoundTerms = ExtractCompoundTerms(lowerQuery);
            topics.UnionWith(compoundTerms);

            // Extract policy-specific terms
            var policyTerms = ExtractPolicySpecificTerms(lowerQuery);
            topics.UnionWith(policyTerms);

            return topics.Take(10).ToList(); // Limit to most relevant topics
        }

        private List<string> ExtractCompoundTerms(string query)
        {
            var compounds = new List<string>();

            // Common HR/Policy compound terms
            var compoundPatterns = new[]
            {
        @"\b(casual\s+leave)\b",
        @"\b(sick\s+leave)\b",
        @"\b(earned\s+leave)\b",
        @"\b(maternity\s+leave)\b",
        @"\b(paternity\s+leave)\b",
        @"\b(compensatory\s+off)\b",
        @"\b(working\s+hours)\b",
        @"\b(overtime\s+policy)\b",
        @"\b(performance\s+appraisal)\b",
        @"\b(disciplinary\s+action)\b",
        @"\b(grievance\s+procedure)\b",
        @"\b(exit\s+interview)\b",
        @"\b(probation\s+period)\b",
        @"\b(notice\s+period)\b",
        @"\b(physical\s+security)\b",
        @"\b(information\s+security)\b",
        @"\b(access\s+control)\b",
        @"\b(risk\s+assessment)\b",
        @"\b(management\s+system)\b",
        @"\b(quality\s+management)\b",
        @"\b(safety\s+procedure)\b",
        @"\b(emergency\s+response)\b"
    };

            foreach (var pattern in compoundPatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(query, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    compounds.Add(match.Groups[1].Value);
                }
            }

            return compounds;
        }

        private List<string> ExtractPolicySpecificTerms(string query)
        {
            var policyTerms = new List<string>();
            var lowerQuery = query.ToLowerInvariant();

            // Policy domain keywords
            var domainMappings = new Dictionary<string[], string[]>
            {
                // HR Policy terms
                [new[] { "leave", "attendance", "payroll", "salary" }] = new[] { "hr policy", "employee handbook", "leave management" },

                // Security Policy terms  
                [new[] { "security", "access", "password", "data" }] = new[] { "information security", "isms policy", "access control" },

                // Safety Policy terms
                [new[] { "safety", "emergency", "hazard", "incident" }] = new[] { "safety policy", "emergency procedure", "risk management" },

                // Quality Policy terms
                [new[] { "quality", "audit", "compliance", "standard" }] = new[] { "quality management", "qms policy", "compliance procedure" }
            };

            foreach (var mapping in domainMappings)
            {
                if (mapping.Key.Any(keyword => lowerQuery.Contains(keyword)))
                {
                    policyTerms.AddRange(mapping.Value);
                }
            }

            // Common abbreviations expansion
            var abbreviations = new Dictionary<string, string[]>
            {
                ["cl"] = new[] { "casual leave", "leave policy" },
                ["sl"] = new[] { "sick leave", "medical leave" },
                ["el"] = new[] { "earned leave", "privilege leave" },
                ["ml"] = new[] { "maternity leave", "parental leave" },
                ["isms"] = new[] { "information security", "management system" },
                ["hr"] = new[] { "human resources", "employee policy" },
                ["ehs"] = new[] { "environment health safety", "safety policy" },
                ["qms"] = new[] { "quality management system", "quality policy" }
            };

            var queryWords = lowerQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in queryWords)
            {
                if (abbreviations.ContainsKey(word))
                {
                    policyTerms.AddRange(abbreviations[word]);
                }
            }

            return policyTerms.Distinct().ToList();
        }

        private SectionQuery? AnalyzeQueryForSectionContent(string lowerQuery)
        {
            // Extract key topics from the query
            var queryTopics = ExtractTopicsFromQuery(lowerQuery);

            // Try to match with common section patterns (universal across policies)
            var universalSectionPatterns = new Dictionary<string[], int>
            {
                [new[] { "introduction", "purpose", "overview", "objective" }] = 1,
                [new[] { "scope", "application", "applicability", "coverage" }] = 2,
                [new[] { "definitions", "terms", "abbreviations", "glossary" }] = 3,
                [new[] { "responsibilities", "roles", "authority", "accountability" }] = 4,
                [new[] { "procedures", "process", "workflow", "steps" }] = 5,
                [new[] { "requirements", "standards", "criteria", "specifications" }] = 6,
                [new[] { "training", "competence", "awareness", "education" }] = 7,
                [new[] { "monitoring", "measurement", "evaluation", "assessment" }] = 8,
                [new[] { "review", "audit", "inspection", "compliance" }] = 9,
                [new[] { "improvement", "corrective", "preventive", "enhancement" }] = 10
            };

            foreach (var pattern in universalSectionPatterns)
            {
                if (pattern.Key.Any(keyword => queryTopics.Any(topic =>
                    topic.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
                {
                    var documentType = DetectDocumentTypeFromContext(lowerQuery);
                    return new SectionQuery
                    {
                        SectionNumber = pattern.Value.ToString(),
                        DocumentType = documentType,
                        OriginalQuery = lowerQuery
                    };
                }
            }

            return null;
        }
        private async Task<Dictionary<string, Dictionary<string, string>>> DiscoverPolicySectionsFromDocuments()
        {
            var dynamicMappings = new Dictionary<string, Dictionary<string, string>>();

            try
            {
                // Get all available collections (representing different embedding models)
                var collectionIds = await _collectionManager.GetAllCollectionIdsAsync();

                foreach (var collectionId in collectionIds.Take(1)) // Use one collection to avoid duplication
                {
                    // Query ChromaDB for all documents with section information
                    var sectionData = await DiscoverSectionsInCollection(collectionId);

                    // Group by document type
                    foreach (var docType in sectionData.Keys)
                    {
                        if (!dynamicMappings.ContainsKey(docType))
                        {
                            dynamicMappings[docType] = new Dictionary<string, string>();
                        }

                        // Merge section mappings
                        foreach (var section in sectionData[docType])
                        {
                            dynamicMappings[docType][section.Key] = section.Value;
                        }
                    }
                }

                _logger.LogInformation($"📚 Discovered {dynamicMappings.Count} policy types with dynamic section mappings");
                return dynamicMappings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover policy sections from documents");
                return GetFallbackMappings();
            }
        }

        private async Task<Dictionary<string, Dictionary<string, string>>> DiscoverSectionsInCollection(string collectionId)
        {
            var sectionMappings = new Dictionary<string, Dictionary<string, string>>();

            try
            {
                // Query ChromaDB for documents with section metadata
                var queryData = new
                {
                    query_texts = new[] { "section policy procedure" },
                    n_results = 1000, // Get many documents
                    include = new[] { "metadatas", "documents" },
                    where = new Dictionary<string, object>
                    {
                        ["section_id"] = new Dictionary<string, object> { ["$ne"] = null }
                    }
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    queryData);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);

                    if (doc.RootElement.TryGetProperty("metadatas", out var metadatasArray) &&
                        doc.RootElement.TryGetProperty("documents", out var documentsArray))
                    {
                        var metadatas = metadatasArray[0].EnumerateArray().ToArray();
                        var documents = documentsArray[0].EnumerateArray().ToArray();

                        for (int i = 0; i < metadatas.Length; i++)
                        {
                            var metadata = metadatas[i];
                            var document = documents[i].GetString() ?? "";

                            // Extract document type and section info
                            var docType = ExtractDocumentTypeFromMetadata(metadata);
                            var sectionId = ExtractSectionIdFromMetadata(metadata);
                            var sectionTitle = ExtractSectionTitleFromMetadata(metadata);

                            if (!string.IsNullOrEmpty(docType) && !string.IsNullOrEmpty(sectionId))
                            {
                                if (!sectionMappings.ContainsKey(docType))
                                {
                                    sectionMappings[docType] = new Dictionary<string, string>();
                                }

                                // Extract keywords from section content
                                var keywords = ExtractKeywordsFromSectionContent(document, sectionTitle);
                                sectionMappings[docType][sectionId] = string.Join(", ", keywords);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover sections in collection");
            }

            return sectionMappings;
        }

        private string ExtractDocumentTypeFromMetadata(JsonElement metadata)
        {
            // Try document_type field first
            if (metadata.TryGetProperty("document_type", out var docType))
            {
                return docType.GetString() ?? "";
            }

            // Fallback: Extract from source file
            if (metadata.TryGetProperty("source_file", out var sourceFile))
            {
                var fileName = Path.GetFileNameWithoutExtension(sourceFile.GetString() ?? "").ToLowerInvariant();

                if (fileName.Contains("isms")) return "ISMS";
                if (fileName.Contains("hr")) return "HR";
                if (fileName.Contains("safety")) return "Safety";
                if (fileName.Contains("quality")) return "Quality";
                if (fileName.Contains("environment")) return "Environment";
            }

            return "General";
        }

        private string ExtractSectionIdFromMetadata(JsonElement metadata)
        {
            if (metadata.TryGetProperty("section_id", out var sectionId))
            {
                return sectionId.GetString() ?? "";
            }

            if (metadata.TryGetProperty("section_number", out var sectionNum))
            {
                return sectionNum.GetString() ?? "";
            }

            return "";
        }

        private string ExtractSectionTitleFromMetadata(JsonElement metadata)
        {
            if (metadata.TryGetProperty("section_title", out var title))
            {
                return title.GetString() ?? "";
            }

            return "";
        }

        private Dictionary<string, Dictionary<string, string>> GetFallbackMappings()
        {
            // Minimal fallback mappings for when discovery fails
            return new Dictionary<string, Dictionary<string, string>>
            {
                ["General"] = new Dictionary<string, string>
                {
                    ["1"] = "introduction, purpose, overview",
                    ["2"] = "scope, application",
                    ["3"] = "definitions, terms",
                    ["4"] = "responsibilities, roles",
                    ["5"] = "procedures, process"
                }
            };
        }

        private List<string> ExtractKeywordsFromSectionContent(string content, string sectionTitle)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add title words
            if (!string.IsNullOrEmpty(sectionTitle))
            {
                var titleWords = sectionTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !IsCommonWord(w));
                keywords.UnionWith(titleWords);
            }

            // Extract important nouns and phrases from content
            var importantTerms = ExtractImportantTermsFromContent(content);
            keywords.UnionWith(importantTerms);

            return keywords.Take(10).ToList(); // Limit to most relevant keywords
        }

        private List<string> ExtractImportantTermsFromContent(string content)
        {
            var terms = new List<string>();
            var lowerContent = content.ToLowerInvariant();

            // Look for policy-specific patterns
            var patterns = new[]
            {
        @"\b(\w+)\s+policy\b",           // "leave policy", "safety policy"
        @"\b(\w+)\s+procedure\b",        // "hiring procedure", "audit procedure"
        @"\b(\w+)\s+management\b",       // "risk management", "performance management"
        @"\b(\w+)\s+requirements?\b",    // "training requirements", "compliance requirement"
        @"\b(\w+)\s+process\b",          // "approval process", "review process"
        @"\b(\w+)\s+guidelines?\b",      // "safety guidelines", "conduct guidelines"
    };

            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(lowerContent, pattern);
                foreach (Match match in matches)
                {
                    var term = match.Groups[1].Value;
                    if (term.Length > 2 && !IsCommonWord(term))
                    {
                        terms.Add(term);
                    }
                }
            }

            // Extract frequently mentioned nouns
            var words = lowerContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3 && !IsCommonWord(w))
                .GroupBy(w => w)
                .Where(g => g.Count() > 2) // Mentioned at least 3 times
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key);

            terms.AddRange(words);

            return terms.Distinct().Take(15).ToList();
        }



        private SectionQuery? DetectSectionByTopic(string lowerQuery)
        {
            var topicToSection = new Dictionary<string[], string>
            {
                [new[] { "introduction", "purpose", "scope overview" }] = "1",
                [new[] { "scope", "application" }] = "2",
                [new[] { "definitions", "terms", "abbreviations" }] = "3",
                [new[] { "context", "organization" }] = "4",
                [new[] { "leadership", "management commitment" }] = "5",
                [new[] { "physical security", "secure areas", "access control" }] = "6",
                [new[] { "planning", "risk assessment" }] = "7",
                [new[] { "operation", "operational controls" }] = "8",
                [new[] { "performance", "evaluation", "monitoring" }] = "9",
                [new[] { "improvement", "continual improvement" }] = "10"
            };

            foreach (var mapping in topicToSection)
            {
                if (mapping.Key.Any(topic => lowerQuery.Contains(topic)))
                {
                    return new SectionQuery
                    {
                        SectionNumber = mapping.Value,
                        DocumentType = DetectDocumentTypeFromContext(lowerQuery),
                        OriginalQuery = lowerQuery
                    };
                }
            }

            return null;
        }

        private string DetectDocumentTypeFromContext(string query)
        {
            var lowerQuery = query.ToLowerInvariant();

            // Explicit mentions
            if (lowerQuery.Contains("isms")) return "ISMS";
            if (lowerQuery.Contains("hr") || lowerQuery.Contains("human resource")) return "HR";
            if (lowerQuery.Contains("safety") || lowerQuery.Contains("ehs")) return "Safety";
            if (lowerQuery.Contains("quality") || lowerQuery.Contains("qms")) return "Quality";
            if (lowerQuery.Contains("environment")) return "Environment";
            if (lowerQuery.Contains("security")) return "Security";

            // Content-based detection
            if (lowerQuery.Contains("leave") || lowerQuery.Contains("attendance") ||
                lowerQuery.Contains("payroll") || lowerQuery.Contains("employee"))
                return "HR";

            if (lowerQuery.Contains("information") || lowerQuery.Contains("data") ||
                lowerQuery.Contains("access control") || lowerQuery.Contains("cyber"))
                return "ISMS";

            if (lowerQuery.Contains("accident") || lowerQuery.Contains("hazard") ||
                lowerQuery.Contains("incident") || lowerQuery.Contains("emergency"))
                return "Safety";

            return ""; // Search all types if no specific type detected
        }

        private List<string> GenerateDynamicSectionVariations(string sectionNumber, string documentType, string plant)
        {
            var variations = new List<string>();

            // Basic section variations (universal)
            variations.AddRange(new[]
            {
        $"Section {sectionNumber}",
        $"section {sectionNumber}",
        $"Section{sectionNumber}",
        $"section{sectionNumber}",
        $"{sectionNumber}.",
        $"{sectionNumber} ",
        $"Clause {sectionNumber}",
        $"clause {sectionNumber}",
        $"Part {sectionNumber}",
        $"Chapter {sectionNumber}"
    });

            // Add subsection variations if it's a numbered section
            if (int.TryParse(sectionNumber.Split('.')[0], out int secNum))
            {
                variations.AddRange(new[]
                {
            $"Section {secNum}.1",
            $"Section {secNum}.2",
            $"Section {secNum}.3",
            $"{secNum}.1",
            $"{secNum}.2",
            $"{secNum}.3"
        });
            }

            // Add document-type specific variations
            if (!string.IsNullOrEmpty(documentType))
            {
                variations.AddRange(new[]
                {
            $"{documentType} Section {sectionNumber}",
            $"{documentType} section {sectionNumber}",
            $"{documentType} {sectionNumber}",
            $"{documentType.ToLower()} section {sectionNumber}"
        });
            }

            // Add policy-specific content variations
            var contentTopics = GetDynamicSectionTopics(sectionNumber, documentType);
            variations.AddRange(contentTopics);

            // Add plant-specific variations
            variations.AddRange(new[]
            {
        $"{plant} {documentType} Section {sectionNumber}",
        $"{plant} section {sectionNumber}",
        $"section {sectionNumber} {plant}"
    });

            return variations.Distinct().ToList();
        }

        private List<string> GetDynamicSectionTopics(string sectionNumber, string documentType)
        {
            var topics = new List<string>();

            // Define section mappings per policy type dynamically
            var policySpecificMappings = new Dictionary<string, Dictionary<string, string[]>>
            {
                ["ISMS"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Introduction", "Purpose", "Scope", "Overview" },
                    ["2"] = new[] { "Scope", "Application", "Boundaries" },
                    ["3"] = new[] { "Definitions", "Terms", "Abbreviations" },
                    ["4"] = new[] { "Context", "Organization", "Stakeholders" },
                    ["5"] = new[] { "Leadership", "Management Commitment", "Policy" },
                    ["6"] = new[] { "Physical Security", "Secure Areas", "Equipment Protection" },
                    ["7"] = new[] { "Planning", "Risk Assessment", "Treatment" },
                    ["8"] = new[] { "Operation", "Operational Controls", "Implementation" },
                    ["9"] = new[] { "Performance", "Evaluation", "Monitoring", "Audit" },
                    ["10"] = new[] { "Improvement", "Nonconformity", "Corrective Action" }
                },
                ["HR"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Introduction", "Purpose", "Employee Handbook" },
                    ["2"] = new[] { "Scope", "Applicability", "Coverage" },
                    ["3"] = new[] { "Recruitment", "Selection", "Hiring Process" },
                    ["4"] = new[] { "Leave Policy", "Annual Leave", "Sick Leave", "Casual Leave" },
                    ["5"] = new[] { "Attendance", "Working Hours", "Punctuality" },
                    ["6"] = new[] { "Performance", "Appraisal", "Review Process" },
                    ["7"] = new[] { "Grievance", "Complaint", "Resolution" },
                    ["8"] = new[] { "Disciplinary", "Misconduct", "Actions" },
                    ["9"] = new[] { "Benefits", "Compensation", "Welfare" },
                    ["10"] = new[] { "Termination", "Resignation", "Exit Process" }
                },
                ["Safety"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Introduction", "Safety Policy", "Commitment" },
                    ["2"] = new[] { "Scope", "Applicability", "Coverage" },
                    ["3"] = new[] { "Hazard Identification", "Risk Assessment" },
                    ["4"] = new[] { "Emergency Procedures", "Response", "Evacuation" },
                    ["5"] = new[] { "Incident Reporting", "Investigation", "Analysis" },
                    ["6"] = new[] { "Training", "Competency", "Awareness" },
                    ["7"] = new[] { "PPE Requirements", "Personal Protective Equipment" },
                    ["8"] = new[] { "Contractor Safety", "Vendor Management" },
                    ["9"] = new[] { "Audit", "Inspection", "Monitoring" },
                    ["10"] = new[] { "Review", "Improvement", "Management Review" }
                },
                ["Quality"] = new Dictionary<string, string[]>
                {
                    ["1"] = new[] { "Scope", "Quality Manual", "QMS" },
                    ["2"] = new[] { "References", "Standards", "Documentation" },
                    ["3"] = new[] { "Definitions", "Terms", "Quality Terms" },
                    ["4"] = new[] { "Quality System", "QMS Requirements" },
                    ["5"] = new[] { "Management Responsibility", "Leadership" },
                    ["6"] = new[] { "Resource Management", "Human Resources" },
                    ["7"] = new[] { "Product Realization", "Process Management" },
                    ["8"] = new[] { "Measurement", "Analysis", "Customer Satisfaction" },
                    ["9"] = new[] { "Improvement", "Corrective Action", "Preventive Action" }
                }
            };

            if (policySpecificMappings.ContainsKey(documentType) &&
                policySpecificMappings[documentType].ContainsKey(sectionNumber))
            {
                topics.AddRange(policySpecificMappings[documentType][sectionNumber]);
            }

            // Add generic section topics if no specific mapping found
            if (!topics.Any())
            {
                topics.AddRange(new[]
                {
            $"section {sectionNumber} content",
            $"policy section {sectionNumber}",
            $"{documentType} requirements section {sectionNumber}"
        });
            }

            return topics;
        }


        private async Task<List<RelevantChunk>> SearchForSpecificSection(
     SectionQuery sectionQuery,
     ModelConfiguration embeddingModel,
     int maxResults,
     string plant,
     string collectionId)
        {
            // Create a single comprehensive search query instead of multiple
            var combinedQuery = $"Section {sectionQuery.SectionNumber} {sectionQuery.DocumentType} " +
                               string.Join(" ", GetDynamicSectionTopics(sectionQuery.SectionNumber, sectionQuery.DocumentType));

            // Single search instead of multiple
            var results = await PerformChromaSearch(combinedQuery, embeddingModel, maxResults * 2, plant, collectionId);

            // Filter results after retrieval
            return results
                .Where(r => IsSectionContentDynamic(r.Text, r.Source, sectionQuery))
                .OrderByDescending(r => CalculateDynamicSectionRelevance(r, sectionQuery))
                .Take(maxResults)
                .ToList();
        }

        private double CalculateDynamicSectionRelevance(RelevantChunk chunk, SectionQuery sectionQuery)
        {
            double relevance = chunk.Similarity;
            var lowerText = chunk.Text.ToLowerInvariant();
            var lowerSource = chunk.Source.ToLowerInvariant();

            // Boost for exact section match
            var exactSectionPatterns = new[]
            {
        $"section {sectionQuery.SectionNumber}",
        $"section{sectionQuery.SectionNumber}",
        $"{sectionQuery.SectionNumber}."
    };

            if (exactSectionPatterns.Any(pattern => lowerText.Contains(pattern)))
            {
                relevance += 0.4; // Strong boost for exact section
            }

            // Boost for document type match
            if (!string.IsNullOrEmpty(sectionQuery.DocumentType))
            {
                if (lowerSource.Contains(sectionQuery.DocumentType.ToLowerInvariant()))
                {
                    relevance += 0.3;
                }
            }

            // Boost for expected section content
            var expectedTopics = GetDynamicSectionTopics(sectionQuery.SectionNumber, sectionQuery.DocumentType);
            var topicMatches = expectedTopics.Count(topic => lowerText.Contains(topic.ToLowerInvariant()));
            if (topicMatches > 0)
            {
                relevance += 0.2 * Math.Min(topicMatches, 3); // Up to 0.6 boost
            }

            // Boost for structural indicators
            var structuralKeywords = new[] { "policy", "procedure", "requirement", "shall", "must", "should" };
            var structuralMatches = structuralKeywords.Count(keyword => lowerText.Contains(keyword));
            if (structuralMatches > 0)
            {
                relevance += 0.1 * Math.Min(structuralMatches, 2); // Up to 0.2 boost
            }

            return Math.Min(1.0, relevance);
        }


        private bool IsSectionContentDynamic(string text, string source, SectionQuery sectionQuery)
        {
            var lowerText = text.ToLowerInvariant();
            var sectionNumber = sectionQuery.SectionNumber;
            var documentType = sectionQuery.DocumentType;

            // Check for direct section references
            var directSectionIndicators = new[]
            {
        $"section {sectionNumber}",
        $"section{sectionNumber}",
        $"{sectionNumber}.",
        $"{sectionNumber} ",
        $"clause {sectionNumber}",
        $"part {sectionNumber}"
    };

            if (directSectionIndicators.Any(indicator => lowerText.Contains(indicator)))
                return true;

            // Check document type alignment
            if (!string.IsNullOrEmpty(documentType))
            {
                var sourceFileName = Path.GetFileNameWithoutExtension(source).ToLowerInvariant();
                if (!sourceFileName.Contains(documentType.ToLowerInvariant()) &&
                    !sourceFileName.Contains("general") &&
                    !sourceFileName.Contains("centralized"))
                {
                    // If document type doesn't match and it's not a general document, lower relevance
                    return false;
                }
            }

            // Check for section-specific content based on policy type
            var expectedTopics = GetDynamicSectionTopics(sectionNumber, documentType);
            var hasExpectedContent = expectedTopics.Any(topic =>
                lowerText.Contains(topic.ToLowerInvariant()));

            return hasExpectedContent;
        }

        private List<string> GenerateSectionVariations(string sectionNumber, string documentType)
        {
            var variations = new List<string>();

            // Basic section variations
            variations.AddRange(new[]
            {
        $"Section {sectionNumber}",
        $"section {sectionNumber}",
        $"Section{sectionNumber}",
        $"section{sectionNumber}",
        $"{sectionNumber}.",
        $"{sectionNumber} "
    });

            // Add subsection variations if it's a numbered section
            if (int.TryParse(sectionNumber, out int secNum))
            {
                variations.AddRange(new[]
                {
            $"Section {secNum}.1",
            $"Section {secNum}.2",
            $"{secNum}.1",
            $"{secNum}.2"
        });
            }

            // Add content-specific variations based on common section topics
            var sectionTopics = GetCommonSectionTopics(sectionNumber);
            variations.AddRange(sectionTopics);

            return variations.Distinct().ToList();
        }

        private List<string> GetCommonSectionTopics(string sectionNumber)
        {
            var topics = new List<string>();

            // Map common section numbers to their typical content
            var sectionMappings = new Dictionary<string, string[]>
            {
                ["1"] = new[] { "Introduction", "Purpose", "Scope", "Overview" },
                ["2"] = new[] { "Scope", "Application", "Definitions", "Terms" },
                ["3"] = new[] { "Definitions", "Terms", "Abbreviations", "References" },
                ["4"] = new[] { "Context", "Organization", "Understanding", "Requirements" },
                ["5"] = new[] { "Leadership", "Management", "Policy", "Responsibility" },
                ["6"] = new[] { "Physical Security", "Secure Areas", "Protection", "Access Control" },
                ["7"] = new[] { "Planning", "Risk Assessment", "Objectives", "Changes" },
                ["8"] = new[] { "Operation", "Operational", "Controls", "Implementation" },
                ["9"] = new[] { "Performance", "Evaluation", "Monitoring", "Assessment" },
                ["10"] = new[] { "Improvement", "Continual", "Corrective", "Actions" }
            };

            if (sectionMappings.ContainsKey(sectionNumber))
            {
                topics.AddRange(sectionMappings[sectionNumber]);
            }

            return topics;
        }

        private bool IsSectionContent(string text, string source, string sectionNumber)
        {
            var lowerText = text.ToLowerInvariant();
            var sectionIndicators = new[]
            {
        $"section {sectionNumber}",
        $"section{sectionNumber}",
        $"{sectionNumber}.",
        $"{sectionNumber} "
    };

            return sectionIndicators.Any(indicator => lowerText.Contains(indicator));
        }

        private bool ContainsSection(string text, string sectionNumber)
        {
            var lowerText = text.ToLower();
            var patterns = new[]
            {
        $"section {sectionNumber}",
        $"section{sectionNumber}",
        $"{sectionNumber}.",
        $"{sectionNumber} "
    };

            return patterns.Any(pattern => lowerText.Contains(pattern.ToLower()));
        }

        private bool ContainsDocumentType(string source, string documentType)
        {
            if (string.IsNullOrEmpty(documentType)) return true;

            return source.ToLower().Contains(documentType.ToLower());
        }

        private double CalculateSectionRelevance(RelevantChunk chunk, SectionQuery sectionQuery)
        {
            double relevance = chunk.Similarity;

            // Boost for exact section match
            if (ContainsSection(chunk.Text, sectionQuery.SectionNumber))
            {
                relevance += 0.3;
            }

            // Boost for document type match
            if (!string.IsNullOrEmpty(sectionQuery.DocumentType) &&
                ContainsDocumentType(chunk.Source, sectionQuery.DocumentType))
            {
                relevance += 0.2;
            }

            // Boost for section keywords in text
            var sectionKeywords = new[] { "section", "policy", "procedure", "rule" };
            var textLower = chunk.Text.ToLower();
            foreach (var keyword in sectionKeywords)
            {
                if (textLower.Contains(keyword))
                {
                    relevance += 0.05;
                }
            }

            return Math.Min(1.0, relevance);
        }

        // Supporting class for section queries
        public class SectionQuery
        {
            public string SectionNumber { get; set; } = "";
            public string DocumentType { get; set; } = "";
            public string OriginalQuery { get; set; } = "";
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
            var targetPlantLower = targetPlant.ToLowerInvariant();

            foreach (var chunk in chunks)
            {
                var source = chunk.Source.ToLowerInvariant();


                // 1. Check if it contains target plant (case-insensitive)
                if (source.Contains(targetPlantLower))
                {
                    chunk.PolicyType = $"{targetPlant.ToTitleCase()} Specific Policy";
                    filteredChunks.Add(chunk);
                    continue;
                }

                // 2. Always include context and centralized files
                if (source.Contains("context") || source.Contains("abbreviation") ||
                    source.Contains("centralized") || source.Contains("general"))
                {
                    chunk.PolicyType = "Context Information";
                    filteredChunks.Add(chunk);
                    continue;
                }

                // 3. Check if it's from a different plant and exclude
                var otherPlants = new[] { "manesar", "sanand" };
                var isOtherPlant = otherPlants.Any(plant =>
                    plant != targetPlantLower && source.Contains(plant));

                if (!isOtherPlant)
                {
                    // Include unknown sources as general policies
                    chunk.PolicyType = "General Policy";
                    filteredChunks.Add(chunk);
                }
            }

            _logger.LogInformation($"🎯 Plant filtering: {chunks.Count} → {filteredChunks.Count} chunks for {targetPlant}");
            return filteredChunks;
        }
        private Dictionary<string, object> CreateChunkMetadata(
    string sourceFile,
    DateTime lastModified,
    string modelName,
    string text,
    string plant,
    string sectionId = "",
    string title = "",
    string documentType = "")
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

            // Add section information
            if (!string.IsNullOrEmpty(sectionId))
            {
                metadata["section_id"] = sectionId;
                metadata["section_number"] = ExtractSectionNumber(sectionId);
            }

            if (!string.IsNullOrEmpty(title))
            {
                metadata["section_title"] = title;
            }

            if (!string.IsNullOrEmpty(documentType))
            {
                metadata["document_type"] = documentType;
            }

            // Plant classification (existing logic)
            if (fileName.Contains("abbreviation") || fileName.Contains("context"))
            {
                metadata["plant"] = "context";
                metadata["is_context"] = true;
            }
            else if (folderPath.Contains("centralized") || fileName.Contains("centralized"))
            {
                metadata["plant"] = "centralized";
                metadata["is_centralized"] = true;
            }
            else if (folderPath.Contains(normalizedPlant) || fileName.Contains(normalizedPlant))
            {
                metadata["plant"] = normalizedPlant;
                metadata["is_plant_specific"] = true;
            }
            else
            {
                metadata["plant"] = "general";
                metadata["is_general"] = true;
            }

            return metadata;
        }

        private string ExtractSectionNumber(string sectionId)
        {
            var match = Regex.Match(sectionId, @"(\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : "";
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

        private async Task<List<RelevantChunk>> PerformChromaSearch(
    string query,
    ModelConfiguration embeddingModel,
    int maxResults,
    string plant,
    string collectionId)
        {
            try
            {
                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);
                if (queryEmbedding.Count == 0)
                {
                    _logger.LogError($"❌ Failed to generate embedding for query: {query}");
                    return new List<RelevantChunk>();
                }

                var normalizedPlant = plant.ToLowerInvariant();
                var whereFilter = new Dictionary<string, object>
        {
            { "$or", new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "plant", normalizedPlant } },
                    new Dictionary<string, object> { { "plant", "centralized" } },
                    new Dictionary<string, object> { { "plant", "context" } },
                    new Dictionary<string, object> { { "plant", "general" } }
                }
            }
        };

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = maxResults,
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
                    return new List<RelevantChunk>();
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseContent);
                return ParseSearchResults(doc.RootElement, maxResults, plant);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ PerformChromaSearch failed for query: {query}");
                return new List<RelevantChunk>();
            }
        }
        private async Task<List<RelevantChunk>> SearchGeneral(
    string query,
    ModelConfiguration embeddingModel,
    int maxResults,
    string plant,
    string collectionId)
        {
            try
            {
                // General search without section-specific filtering
                _logger.LogInformation($"🔍 Performing general search for: {query}");

                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);
                if (queryEmbedding.Count == 0)
                {
                    return new List<RelevantChunk>();
                }

                var normalizedPlant = plant.ToLowerInvariant();

                // Broader search criteria for general queries
                var whereFilter = new Dictionary<string, object>
        {
            { "$or", new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "plant", normalizedPlant } },
                    new Dictionary<string, object> { { "plant", "centralized" } },
                    new Dictionary<string, object> { { "plant", "context" } },
                    new Dictionary<string, object> { { "plant", "general" } }
                }
            }
        };

                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = Math.Min(maxResults * 2, 50), // Get more results for general search
                    include = new[] { "documents", "metadatas", "distances" },
                    where = whereFilter
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseContent);
                    var results = ParseSearchResults(doc.RootElement, maxResults, plant);

                    _logger.LogInformation($"🔍 General search found {results.Count} results");
                    return results;
                }

                return new List<RelevantChunk>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ General search failed: {ex.Message}");
                return new List<RelevantChunk>();
            }
        }

        public async Task DeleteModelDataFromChroma(string modelName)
        {
            await _collectionManager.DeleteModelCollectionAsync(modelName);
        }

        private async Task<List<RelevantChunk>> PerformNativeChromaSearch(
    string query,
    ModelConfiguration embeddingModel,
    int maxResults,
    string plant,
    string collectionId)
        {
            try
            {
                // Generate embedding once
                var queryEmbedding = await GetEmbeddingAsync(query, embeddingModel);
                if (queryEmbedding.Count == 0) return new List<RelevantChunk>();

                // Use ChromaDB native query with metadata filtering
                var searchData = new
                {
                    query_embeddings = new List<List<float>> { queryEmbedding },
                    n_results = maxResults,
                    include = new[] { "documents", "metadatas", "distances" },
                    where = new Dictionary<string, object>
            {
                { "$or", new List<Dictionary<string, object>>
                    {
                        new() { { "plant", plant.ToLowerInvariant() } },
                        new() { { "plant", "centralized" } },
                        new() { { "plant", "context" } }
                    }
                }
            }
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                    searchData);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseContent);
                    return ParseSearchResults(doc.RootElement, maxResults, plant);
                }

                return new List<RelevantChunk>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Native ChromaDB search failed");
                return new List<RelevantChunk>();
            }
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                _logger.LogInformation("🚀 Starting parallel RAG system initialization");

                // Parallel model discovery and configuration
                var initTasks = new List<Task>
        {
            Task.Run(async () =>
            {
                var availableModels = await _modelManager.DiscoverAvailableModelsAsync();
                await ConfigureDefaultModelsAsync(availableModels);
                return availableModels;
            }),
            Task.Run(() => EnsureDirectoriesExist()),
            Task.Run(() => EnsureAbbreviationContext()),
            Task.Run(async () => await LoadCorrectionCacheAsync()),
            Task.Run(async () => await LoadHistoricalAppreciatedAnswersAsync())
        };

                await Task.WhenAll(initTasks);

                // Parallel document processing for all plants
                var plantTasks = _plants.Plants.Keys.Select(async plant =>
                {
                    _logger.LogInformation($"Processing documents for plant: {plant}");
                    var models = await _modelManager.DiscoverAvailableModelsAsync();
                    var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();
                    await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);
                });

                await Task.WhenAll(plantTasks);

                _isInitialized = true;
                _logger.LogInformation("✅ Parallel RAG system initialization completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize RAG system");
                throw;
            }
        }

        private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<float>();

            var cacheKey = $"{model.Name}:{text.GetHashCode():X8}";

            // Fast cache lookup with access tracking
            if (_optimizedEmbeddingCache.TryGetValue(cacheKey, out var cached))
            {
                if (DateTime.UtcNow - cached.Cached < TimeSpan.FromHours(24))
                {
                    // Update access count for LFU eviction
                    _optimizedEmbeddingCache.TryUpdate(cacheKey,
                        (cached.Embedding, cached.Cached, cached.AccessCount + 1), cached);
                    return cached.Embedding;
                }
                else
                {
                    _optimizedEmbeddingCache.TryRemove(cacheKey, out _);
                }
            }

            // Reduced timeout for faster failures
            if (!await _globalEmbeddingSemaphore.WaitAsync(TimeSpan.FromMilliseconds(100)))
            {
                _logger.LogWarning("Embedding semaphore timeout, using cached or empty result");
                return cached.Embedding ?? new List<float>();
            }

            try
            {
                var processedText = CleanTextForEmbedding(text, model);
                var request = new
                {
                    model = model.Name,
                    prompt = processedText,
                    options = new { num_ctx = 1024, temperature = 0 }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Reduced timeout
                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

                if (!response.IsSuccessStatusCode) return new List<float>();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                    return new List<float>();

                var embedding = embeddingProperty.EnumerateArray().Select(x => x.GetSingle()).ToList();

                // Cache with LFU tracking
                if (_optimizedEmbeddingCache.Count < 3000) // Reduced cache size
                {
                    _optimizedEmbeddingCache.TryAdd(cacheKey, (embedding, DateTime.UtcNow, 1));
                }

                return embedding;
            }
            finally
            {
                _globalEmbeddingSemaphore.Release();
            }
        }

        private void CleanupEmbeddingCache(object state)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-12);
                var itemsToRemove = _optimizedEmbeddingCache
                    .Where(kvp => kvp.Value.Cached < cutoff || kvp.Value.AccessCount < 2) // LFU + time-based
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in itemsToRemove)
                {
                    _optimizedEmbeddingCache.TryRemove(key, out _);
                }

                _logger.LogDebug($"🧹 Cleaned {itemsToRemove.Count} cache entries");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup failed");
            }
        }
        private async Task<List<RelevantChunk>> GetRelevantChunksOptimized(
    string query,
    ModelConfiguration embeddingModel,
    string plant)
        {
            const int MAX_CHUNKS = 7; // Reduced from 15
            const double MIN_SIMILARITY = 0.2; // Higher threshold
            var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);
            try
            {
                var chunks = await PerformNativeChromaSearch(query, embeddingModel, MAX_CHUNKS * 2, plant, collectionId);

                // Client-side filtering and ranking
                var filteredChunks = chunks
                    .Where(c => c.Similarity >= MIN_SIMILARITY)
                    .OrderByDescending(c => c.Similarity)
                    .Take(MAX_CHUNKS)
                    .ToList();

                _logger.LogInformation($"🎯 Optimized retrieval: {chunks.Count} → {filteredChunks.Count} chunks");
                return filteredChunks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Optimized chunk retrieval failed");
                return new List<RelevantChunk>();
            }
        }
    }
}
