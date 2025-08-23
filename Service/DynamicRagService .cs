// Services/DynamicRagService.cs
using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Service.Models;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
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

        //new code by Hamza
        private readonly StringProcessingService _stringProcessor;
        private readonly PolicyAnalysisService _policyAnalysis;
        private readonly TextChunkingService _textChunking;
        private readonly ConversationAnalysisService _conversationAnalysis;
        private readonly EntityExtractionService _entityExtraction;
        private readonly SystemPromptBuilder _systemPromptBuilder;

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

        private readonly string _metricsFile = Path.Combine(AppContext.BaseDirectory, "Logs", "rag-metrics.log");

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
            AbbreviationExpansionService abbreviationService,
            //new code by Hamza
            StringProcessingService stringProcessor,
            PolicyAnalysisService policyAnalysis,
            TextChunkingService textChunking,
            ConversationAnalysisService conversationAnalysis,
            EntityExtractionService entityExtraction,
            SystemPromptBuilder systemPromptBuilder)
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

            _stringProcessor = stringProcessor;
            _policyAnalysis = policyAnalysis;
            _textChunking = textChunking;
            _conversationAnalysis = conversationAnalysis;
            _entityExtraction = entityExtraction;
            _systemPromptBuilder = systemPromptBuilder;

            InitializeSessionCleanup();
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

        private void LogMetric(string metric)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_metricsFile)!);
            File.AppendAllText(_metricsFile, $"{DateTime.Now:O} | {metric}{Environment.NewLine}");
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


                var chunks = _textChunking.ChunkText(content, filePath);
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
            int maxResults = 10,
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

                var _perRequestEmbeddings = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<List<float>>>(StringComparer.Ordinal);

                async Task<List<float>> GetPerRequestEmbeddingAsync(string text)
                {
                    if (string.IsNullOrWhiteSpace(text))
                        return new List<float>();

                    var task = _perRequestEmbeddings.GetOrAdd(text, _ => GetEmbeddingAsync(text, embModel));
                    try
                    {
                        var emb = await task;
                        return emb ?? new List<float>();
                    }
                    catch
                    {
                        _perRequestEmbeddings.TryRemove(text, out _);
                        throw;
                    }
                }


                if (IsHistoryClearRequest(question))
                {
                    return await HandleHistoryClearRequest(context, dbSession.SessionId);
                }

                // 🆕 Check for similar conversations in database first (ONLY for MEAI queries)
                //var questionEmbedding = await GetEmbeddingAsync(question, embModel);

                var questionEmbedding = await GetPerRequestEmbeddingAsync(question);

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
                if (_conversationAnalysis.IsTopicChanged(question, context))
                {
                    _logger.LogInformation($"Topic changed for session {context.SessionId}, clearing context");
                    ClearContext(context);
                }

                var contextualQuery = _conversationAnalysis.BuildContextualQuery(question, context.History);

                // Get relevant chunks - ONLY for MEAI queries
                var relevantChunks = await GetRelevantChunksWithExpansionAsync(
                                    contextualQuery, embModel, maxResults, meaiInfo, context, useReRanking, genModel, plant);

                var sectionQuery = await _policyAnalysis.DetectAndParseSection(question); // Make it async
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

                    if (lastConversationId.HasValue && _conversationAnalysis.IsFollowUpQuestion(question, context))
                    {
                        parentId = lastConversationId.Value;
                    }
                }



                // 1️⃣ Get answer embedding
                var answer = await GenerateChatResponseAsync(
                    question, genModel, context.History, relevantChunks, context, ismeai: meaiInfo, plant: plant);
                //var answerEmbedding = await GetEmbeddingAsync(answer, embModel);


                var answerEmbedding = await GetPerRequestEmbeddingAsync(answer);
                // 2️⃣ Process only top 3 relevant chunks by initial similarity
                var scoredChunks = await Task.WhenAll(
                    relevantChunks
                        .Where(x => x.Similarity > 0.4) // initial threshold
                        .OrderByDescending(x => x.Similarity)
                        .Take(3) // fewer chunks
                        .Select(async chunk =>
                        {
                            var chunkEmbedding = await GetPerRequestEmbeddingAsync(chunk.Text);
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

                //var questionEmbeddingTask = GetEmbeddingAsync(question, embModel);
                //var answerEmbeddingTask = GetEmbeddingAsync(answer, embModel);

                var questionEmbeddingTask = GetPerRequestEmbeddingAsync(question);
                var answerEmbeddingTask = GetPerRequestEmbeddingAsync(answer);

                var entitiesTask = _entityExtraction.ExtractEntitiesAsync(answer);

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

                var hasSufficientCoverage = _policyAnalysis.CheckPolicyCoverage(relevantChunks, question);

                _metrics.RecordQueryProcessing(stopwatch.ElapsedMilliseconds, relevantChunks.Count, true);
                LogMetric($"QueryTimeMs={stopwatch.ElapsedMilliseconds} | Chunks={relevantChunks.Count} | Confidence={confidence:F2}");

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

        // Add this to your class
        private readonly ConcurrentDictionary<string, (List<float> Embedding, DateTime Cached)> _sessionEmbeddingCache = new();


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
                var namedEntities = await _entityExtraction.ExtractEntitiesAsync(answer);

                // Determine topic tag (simple keyword-based approach)
                var topicTag = _conversationAnalysis.DetermineTopicTag(question, answer);

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
                var topicTag = _conversationAnalysis.DetermineTopicTag(question, answer);

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

                var currentTopics = _conversationAnalysis.ExtractKeyTopics(question);
                if (currentTopics.Any())
                {
                    var isMainTopic = !_conversationAnalysis.IsQuestionPatternContinuation(question, context)
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
            var hasSufficientCoverage = _policyAnalysis.CheckPolicyCoverage(chunks, question);

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
                    await _systemPromptBuilder.BuildMeaiSystemPrompt(plant, chunks, question) : // Add await
                    _systemPromptBuilder.BuildGeneralSystemPrompt()
            });


            // OPTIMIZED: Limited conversation history (reduce token usage)
            foreach (var turn in history.TakeLast(4)) // Reduced from 6
            {
                messages.Add(new { role = "user", content = turn.Question });
                messages.Add(new { role = "assistant", content = turn.Answer });
            }

            // OPTIMIZED: Build context only when needed and more efficiently
            if (chunks.Any() && ismeai && hasSufficientCoverage)
            {
                var contextContent = BuildOptimizedContext(chunks, plant);
                messages.Add(new { role = "system", content = contextContent });
            }

            // Add current question
            question = _conversationAnalysis.ResolvePronouns(question, context);
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
                            { "num_ctx", 4000 }, // Increased context window
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
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
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
        private async Task<(string Answer, List<RelevantChunk> Chunks)?> CheckAppreciatedAnswerAsync(string question)
        {
            try
            {
                var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                var inputEmbedding = await GetEmbeddingAsync(question, embeddingModel); // ✅ Use embedding model

                if (inputEmbedding == null || inputEmbedding.Count == 0)
                    return null;

                var matches = new List<(string Answer, List<RelevantChunk> Chunks, double Similarity)>();

                foreach (var entry in _appreciatedTurns)
                {
                    var entryEmbedding = await GetEmbeddingAsync(entry.Question, embeddingModel); // ✅ Async call
                    var similarity = CosineSimilarity(inputEmbedding, entryEmbedding);

                    if (similarity >= 0.8)
                    {
                        matches.Add((entry.Answer, entry.Chunks, similarity));
                    }
                }

                if (matches.Any())
                {
                    var best = matches.OrderByDescending(x => x.Similarity).First();
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
                if (_conversationAnalysis.IsTopicChangedLightweight(question, context))
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
                            similarity = _stringProcessor.CalculateTextSimilarity(question.ToLowerInvariant(), correction.Question.ToLowerInvariant());
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
                        Text = _stringProcessor.CleanText(chunk.Text),
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
                    string policyType = _policyAnalysis.DeterminePolicyType(metadata, sourceFile, currentPlant);

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
                diagnostic.HasSufficientCoverage = _policyAnalysis.CheckPolicyCoverage(chunks, question);

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
                var sectionQuery = await _policyAnalysis.DetectAndParseSection(query);

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
        public async Task LearnFromSuccessfulQuery(string query, string sectionNumber, string documentType, List<RelevantChunk> chunks)
        {
            try
            {
                // Extract successful keywords from the query
                var queryKeywords = _entityExtraction.ExtractTopicsFromQuery(query.ToLowerInvariant());

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
                            var docType = _entityExtraction.ExtractDocumentTypeFromMetadata(metadata);
                            var sectionId = _entityExtraction.ExtractSectionIdFromMetadata(metadata);
                            var sectionTitle = _entityExtraction.ExtractSectionTitleFromMetadata(metadata);

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
                    .Where(w => w.Length > 2 && !TextUtils.IsCommonWord(w));
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
                    if (term.Length > 2 && !TextUtils.IsCommonWord(term))
                    {
                        terms.Add(term);
                    }
                }
            }

            // Extract frequently mentioned nouns
            var words = lowerContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3 && !TextUtils.IsCommonWord(w))
                .GroupBy(w => w)
                .Where(g => g.Count() > 2) // Mentioned at least 3 times
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key);

            terms.AddRange(words);

            return terms.Distinct().Take(15).ToList();
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
                               string.Join(" ", _policyAnalysis.GetDynamicSectionTopics(sectionQuery.SectionNumber, sectionQuery.DocumentType));

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
            var expectedTopics = _policyAnalysis.GetDynamicSectionTopics(sectionQuery.SectionNumber, sectionQuery.DocumentType);
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
            var expectedTopics = _policyAnalysis.GetDynamicSectionTopics(sectionNumber, documentType);
            var hasExpectedContent = expectedTopics.Any(topic =>
                lowerText.Contains(topic.ToLowerInvariant()));

            return hasExpectedContent;
        }
        // Supporting class for section queries
        public class SectionQuery
        {
            public string SectionNumber { get; set; } = "";
            public string DocumentType { get; set; } = "";
            public string OriginalQuery { get; set; } = "";
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
                var expandedQuery = _abbreviationService.ExpandQueryList(query);
                foreach (var q in expandedQuery.Skip(1))
                {
                    var chunks = await SearchChromaDBAsync(q, embeddingModel, 3, plant);
                    allChunks.AddRange(chunks.Take(1)); // keep only best hit per variant
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
            if (string.IsNullOrWhiteSpace(text))
                return new List<float>();

            var cacheKey = $"{model.Name}:{text.GetHashCode():X}";

            // Check cache first
            if (_optimizedEmbeddingCache.TryGetValue(cacheKey, out var cached))
            {
                if (DateTime.Now - cached.Cached < TimeSpan.FromHours(24))
                {
                    cached.AccessCount++;
                    return cached.Embedding;
                }
            }

            // Use semaphore to limit concurrent embedding requests
            await _globalEmbeddingSemaphore.WaitAsync();
            try
            {
                var processedText = _stringProcessor.CleanTextForEmbedding(text, model);

                var request = new
                {
                    model = model.Name, // ✅ Use the embedding model specifically
                    prompt = processedText,
                    options = new
                    {
                        num_ctx = 1024,
                        temperature = 0
                    }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ Embedding request failed: {response.StatusCode} - {errorContent}");
                    return new List<float>();
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                {
                    _logger.LogError($"❌ No embedding property in response for model {model.Name}");
                    return new List<float>();
                }

                var embedding = embeddingProperty.EnumerateArray()
                    .Select(x => x.GetSingle())
                    .ToList();

                // Cache the result
                _optimizedEmbeddingCache.TryAdd(cacheKey, (embedding, DateTime.UtcNow, 1));

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
        // Add this field to your DynamicRagService class
        private readonly EmbeddingServiceCircuitBreaker _circuitBreaker = new();
        public class EmbeddingServiceCircuitBreaker
        {
            private int _failureCount = 0;
            private DateTime _lastFailureTime = DateTime.MinValue;
            private readonly int _failureThreshold = 3; // Reduced from 5
            private readonly TimeSpan _timeout = TimeSpan.FromMinutes(1); // Reduced from 2
            private readonly object _lock = new object();

            public bool CanExecute()
            {
                lock (_lock)
                {
                    if (_failureCount < _failureThreshold)
                        return true;

                    if (DateTime.Now - _lastFailureTime > _timeout)
                    {
                        _failureCount = 0;
                        return true;
                    }
                    return false;
                }
            }

            public void RecordSuccess() { lock (_lock) { _failureCount = 0; } }
            public void RecordFailure() { lock (_lock) { _failureCount++; _lastFailureTime = DateTime.Now; } }
        }

    }
}
