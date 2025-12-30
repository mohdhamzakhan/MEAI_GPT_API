// Services/DynamicRagService.cs
using DocumentFormat.OpenXml.Office2013.Excel;
using DocumentFormat.OpenXml.Spreadsheet;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Service.Models;
using MEAIGPTAPI.Services;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static MEAI_GPT_API.Models.Conversation;
using static MEAI_GPT_API.Services.DynamicRagService;

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
        private readonly HelperMethods _helperMethods;

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
        private readonly OllamaHttpClient _ollamaClient;
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
            SystemPromptBuilder systemPromptBuilder,
            HelperMethods helperMethods,
            OllamaHttpClient ollamaClient)
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
            _ollamaClient = ollamaClient;

            InitializeSessionCleanup();
            _helperMethods = helperMethods;
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

        private void EnsurePlantSpecificOrganizationContext()
        {
            foreach (var plant in _plants.Plants.Keys)
            {
                var plantOrgPath = Path.Combine(
                    _chromaOptions.ContextFolder,
                    $"organization-{plant.ToLower()}.txt"
                );

                if (!File.Exists(plantOrgPath))
                {
                    var plantOrgContent = $@"MEAI {plant} Plant - Organization Details

These are the fixed organizational details for {plant} plant.";

                    File.WriteAllText(plantOrgPath, plantOrgContent);
                    _logger.LogInformation($"Created organization context file for {plant}");
                }
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

            // ✅ ADD: Include context files (abbreviations, etc.)
            var contextFiles = GetContextFiles();
            var allFiles = policyFiles.Concat(contextFiles).ToList();

            _logger.LogInformation($"📄 Processing {policyFiles.Count} policy files + {contextFiles.Count} context files for {embeddingModels.Count} embedding models");

            var tasks = embeddingModels.Select(async model =>
            {
                _logger.LogInformation($"🔄 Processing documents for model: {model.Name}");

                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);

                foreach (var filePath in allFiles)
                {
                    await ProcessFileForModelAsync(filePath, model, collectionId, plant);
                }

                _logger.LogInformation($"✅ Completed document processing for model: {model.Name}");
            });

            await Task.WhenAll(tasks);
        }

        // ✅ NEW METHOD: Get context files
        private List<string> GetContextFiles()
        {
            var contextFiles = new List<string>();

            if (Directory.Exists(_chromaOptions.ContextFolder))
            {
                // Get all context files (abbreviations, organization, etc.)
                contextFiles.AddRange(Directory.GetFiles(_chromaOptions.ContextFolder, "*.txt"));

                _logger.LogInformation($"Found {contextFiles.Count} context files:");
                foreach (var file in contextFiles)
                {
                    _logger.LogInformation($"  - {Path.GetFileName(file)}");
                }
            }

            return contextFiles;
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


            _logger.LogInformation($"📋 Total files for {plant}: {policyFiles.Count}");
            return policyFiles;
        }


        // This cache tracks processed files with their last write time to skip unchanged files
        private readonly ConcurrentDictionary<string, DateTime> processedFilesCache = new();
        // In your ProcessFileForModelAsync method, ensure you're using proper document processing


        /// <summary>
        /// Check if embeddings already exist for this file in ChromaDB
        /// </summary>
        private async Task<bool> FileEmbeddingsExistAsync(string filePath, string modelName, string collectionId)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);

                // Query ChromaDB for any embeddings with this source file
                var queryData = new
                {
                    where = new Dictionary<string, object>
            {
                { "source_file", new Dictionary<string, object>
                    {
                        { "$eq", filePath }
                    }
                }
            },
                    limit = 1 // Just check if any exist
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/get",
                    queryData);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);

                    if (doc.RootElement.TryGetProperty("ids", out var idsArray))
                    {
                        var hasEmbeddings = idsArray.GetArrayLength() > 0;

                        if (hasEmbeddings)
                        {
                            _logger.LogDebug($"✓ Embeddings already exist for {fileName} in collection {collectionId}");
                        }

                        return hasEmbeddings;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to check existing embeddings for {filePath}");
                return false; // Assume doesn't exist if check fails
            }
        }
        private async Task ProcessFileForModelAsync(string filePath, ModelConfiguration model, string collectionId, string plant)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var lastWriteTime = fileInfo.LastWriteTime;
                var cacheKey = $"{filePath}:{model.Name}";

                // ✅ CHECK 1: In-memory cache
                if (processedFilesCache.TryGetValue(cacheKey, out var cachedWriteTime))
                {
                    if (cachedWriteTime >= lastWriteTime)
                    {
                        _logger.LogDebug($"✓ [CACHE HIT] Skipping '{Path.GetFileName(filePath)}' for model '{model.Name}'");
                        return; // ← EARLY EXIT
                    }
                }

                // ✅ CHECK 2: ChromaDB existence (in case cache was cleared but embeddings exist)
                var embeddingsExist = await FileEmbeddingsExistAsync(filePath, model.Name, collectionId);

                if (embeddingsExist && cachedWriteTime >= lastWriteTime)
                {
                    _logger.LogInformation($"✓ [DB HIT] Embeddings exist for '{Path.GetFileName(filePath)}', skipping reprocessing");

                    // Update cache
                    processedFilesCache[cacheKey] = lastWriteTime;
                    return; // ← EARLY EXIT
                }

                _logger.LogInformation($"📄 Processing NEW/MODIFIED file: {Path.GetFileName(filePath)} for model: {model.Name}");

                // Only delete if we're reprocessing a changed file
                if (embeddingsExist)
                {
                    _logger.LogInformation($"🗑️ Deleting old embeddings for modified file: {Path.GetFileName(filePath)}");
                    await DeleteFileEmbeddingsAsync(filePath, model.Name, collectionId);
                }

                _logger.LogInformation($"📄 Extracting text from {Path.GetFileName(filePath)}");
                var content = await _documentProcessor.ExtractTextAsync(filePath);

                // ✅ VALIDATE EXTRACTED TEXT
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning($"❌ No content extracted from {Path.GetFileName(filePath)}");
                    return;
                }

                // ✅ CHECK FOR BINARY/CORRUPTED DATA
                var replacementCharCount = content.Count(c => c == '\uFFFD');
                if (replacementCharCount > 10) // Allow some tolerance
                {
                    _logger.LogError($"❌ File appears corrupted: {Path.GetFileName(filePath)} " +
                                  $"contains {replacementCharCount} replacement characters");
                    return;
                }

                // ✅ CHECK TEXT QUALITY
                var alphanumericRatio = content.Count(char.IsLetterOrDigit) / (double)content.Length;
                if (alphanumericRatio < 0.5) // Less than 50% readable chars
                {
                    _logger.LogWarning($"⚠️ Low quality text in {Path.GetFileName(filePath)}: " +
                                    $"{alphanumericRatio:P0} alphanumeric");
                }

                _logger.LogInformation($"✅ Extracted {content.Length} characters from {Path.GetFileName(filePath)}");

                var chunks = _textChunking.ChunkText(content, filePath);

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning($"⚠️ No content extracted from {Path.GetFileName(filePath)}");
                    return;
                }


                if (!chunks.Any())
                {
                    _logger.LogWarning($"⚠️ No chunks created from {Path.GetFileName(filePath)}");
                    return;
                }

                var successCount = await ProcessChunkBatchForModelAsync(chunks, model, collectionId, fileInfo.LastWriteTime, plant);

                if (successCount > 0)
                {
                    processedFilesCache[cacheKey] = lastWriteTime;
                    _logger.LogInformation($"✅ Successfully processed & CACHED {successCount}/{chunks.Count} chunks from {Path.GetFileName(filePath)}");
                }
                else
                {
                    _logger.LogError($"❌ Failed to process - NOT caching");
                    processedFilesCache.TryRemove(cacheKey, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to process file {filePath}");
                processedFilesCache.TryRemove($"{filePath}:{model.Name}", out _);
            }
        }

        /// <summary>
        /// Delete all embeddings for a specific file from ChromaDB
        /// </summary>
        private async Task DeleteFileEmbeddingsAsync(string filePath, string modelName, string collectionId)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                _logger.LogInformation($"🗑️ Deleting old embeddings for: {fileName} (model: {modelName})");

                // ChromaDB delete by metadata filter
                var deleteRequest = new
                {
                    where = new Dictionary<string, object>
                    {
                        { "source_file", new Dictionary<string, object>
                            {
                                { "$eq", filePath }
                            }
                        }
                    }
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/delete",
                    deleteRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"✅ Deleted old embeddings for {fileName}");
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"⚠️ Failed to delete old embeddings: {response.StatusCode} - {error}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to delete old embeddings for {filePath} - continuing anyway");
                // Don't throw - allow reprocessing to continue even if deletion fails
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

            // Validate input
            if (string.IsNullOrWhiteSpace(question))
                throw new ArgumentException("Question cannot be empty");

            try
            {
                // Select models (use defaults for MEAI queries)
                if (string.IsNullOrEmpty(generationModel))
                    generationModel = _config.DefaultGenerationModel
                        ?? throw new InvalidOperationException("Default generation model not configured.");

                if (string.IsNullOrEmpty(embeddingModel))
                    embeddingModel = _config.DefaultEmbeddingModel
                        ?? throw new InvalidOperationException("Default embedding model not configured.");

                // Create or load conversation context
                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(),
                    _currentUser);

                var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);

                // 1️⃣ Early return: appreciated or corrected answers
                var appreciated = await CheckAppreciatedAnswerAsync(question);
                if (appreciated != null)
                {
                    _logger.LogInformation("⚡ Early return: Using appreciated answer");
                    return CreateSuccessResponse(appreciated.Value.Answer, "Appreciated Answer",
                        stopwatch.ElapsedMilliseconds, 0.95);
                }

                var correction = await CheckCorrectionsAsync(question);
                if (correction != null)
                {
                    _logger.LogInformation("⚡ Early return: Using correction");
                    var rephrased = await _helperMethods.RephraseWithLLMAsync(
                        correction.Answer, generationModel);

                    return CreateSuccessResponse(rephrased, "User Correction",
                        stopwatch.ElapsedMilliseconds, 1.0, isFromCorrection: true);
                }

                // 2️⃣ Clear history if requested
                if (IsHistoryClearRequest(question))
                    return await HandleHistoryClearRequest(context, sessionId);

                // 3️⃣ Fast path: non-MEAI queries skip embeddings entirely
                if (!meaiInfo)
                    return await ProcessNonMeaiQueryFast(question, sessionId, generationModel, stopwatch);

                // Load models
                var genModel = await _modelManager.GetModelAsync(generationModel!);
                var embModel = await _modelManager.GetModelAsync(embeddingModel!);
                if (genModel == null) throw new ArgumentException($"Generation model {generationModel} not available");
                if (embModel == null) throw new ArgumentException($"Embedding model {embeddingModel} not available");
                if (embModel.EmbeddingDimension == 0)
                    embModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);

                _logger.LogInformation($"Processing MEAI query with models - Gen: {generationModel}, Emb: {embeddingModel}");

                // Embedding cache for this query
                var _perRequestEmbeddings = new ConcurrentDictionary<string, Task<List<float>>>(StringComparer.Ordinal);

                async Task<List<float>> GetPerRequestEmbeddingAsync(string text)
                {
                    if (string.IsNullOrWhiteSpace(text)) return new List<float>();
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

                // 🧠 Semantic similarity reuse from DB (fast lookup)
                var questionEmbedding = await GetPerRequestEmbeddingAsync(question);
                var similarConversations = await _conversationStorage.SearchSimilarConversationsAsync(
                    questionEmbedding, plant, threshold: 0.85, limit: 2);

                if (similarConversations.Any())
                {
                    var best = similarConversations.First();
                    if (best.Entry.WasAppreciated)
                    {
                        _logger.LogInformation($"💡 Reusing appreciated answer from DB (ID: {best.Entry.Id})");

                        await SaveConversationToDatabase(
                            dbSession.SessionId, question, best.Entry.Answer,
                            new List<RelevantChunk>(), genModel, embModel,
                            best.Similarity, stopwatch.ElapsedMilliseconds,
                            false, null, plant);

                        return new QueryResponse
                        {
                            Answer = best.Entry.Answer,
                            Confidence = best.Similarity,
                            ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                            SessionId = dbSession.SessionId,
                            Sources = best.Entry.Sources,
                            IsFromCorrection = false,
                            RelevantChunks = new List<RelevantChunk>(),
                            ModelsUsed = new Dictionary<string, string>
                            {
                                ["generation"] = generationModel,
                                ["embedding"] = embeddingModel
                            }
                        };
                    }
                }

                // 🧩 Topic & context
                if (_conversationAnalysis.IsTopicChanged(question, context))
                {
                    _logger.LogInformation($"🔄 Topic changed for session {context.SessionId}; resetting context");
                    ClearContext(context);
                }

                // Section detection before retrieval
                var sectionQuery = await _policyAnalysis.DetectAndParseSection(question);
                List<RelevantChunk> relevantChunks = new();

                if (sectionQuery != null)
                {
                    _logger.LogInformation($"🎯 Detected section query: {sectionQuery.DocumentType} - Section {sectionQuery.SectionNumber}");
                    relevantChunks = await SearchForSpecificSection(
                        sectionQuery, embModel, maxResults, plant,
                        await _collectionManager.GetOrCreateCollectionAsync(embModel));
                }
                else
                {
                    var contextualQuery = _conversationAnalysis.BuildContextualQuery(question, context.History);
                    relevantChunks = await GetRelevantChunksWithExpansionAsync(
                        contextualQuery, embModel, maxResults,
                        meaiInfo, context, useReRanking, genModel, plant);
                }

                // Generate final answer
                var parentId = context.History.Any() &&
                               dbSession.Metadata.TryGetValue("lastConversationId", out var lastId) &&
                               _conversationAnalysis.IsFollowUpQuestion(question, context)
                               ? Convert.ToInt32(lastId)
                               : (int?)null;

                var answer = await GenerateChatResponseAsync(
                    question, genModel, context.History, relevantChunks, context, meaiInfo, plant);

                var answerEmbedding = await GetPerRequestEmbeddingAsync(answer);

                // 🔍 Rank top chunks
                var scored = await Task.WhenAll(
                    relevantChunks.OrderByDescending(x => x.Similarity)
                        .Take(5)
                        .Select(async chunk =>
                        {
                            var emb = await GetPerRequestEmbeddingAsync(chunk.Text);
                            var sim = CosineSimilarity(answerEmbedding, emb);
                            chunk.Similarity = sim;
                            return (chunk, sim);
                        }));

                var dynamicThreshold = scored.Any(s => s.sim > 0.6) ? 0.5 : 0.3;
                var topChunks = scored.Where(s => s.sim > dynamicThreshold)
                                      .OrderByDescending(s => s.sim)
                                      .Take(5)
                                      .Select(s => s.chunk)
                                      .ToList();

                // Compute confidence
                var confidence = topChunks.Any() ? topChunks.Average(c => c.Similarity) : 0;

                // Entity extraction and persistence
                var entities = await _entityExtraction.ExtractEntitiesAsync(answer);
                var conversationId = await SaveConversationToDatabaseFast(
                    dbSession.SessionId, question, answer, topChunks,
                    genModel, embModel, confidence, stopwatch.ElapsedMilliseconds,
                    false, parentId, plant, questionEmbedding, answerEmbedding, entities);

                dbSession.Metadata["lastConversationId"] = conversationId;
                await _conversationStorage.UpdateSessionAsync(dbSession);

                await UpdateConversationHistoryFast(context, question, answer, topChunks, entities);

                stopwatch.Stop();
                _metrics.RecordQueryProcessing(stopwatch.ElapsedMilliseconds, topChunks.Count, true);

                var coverage = _policyAnalysis.CheckPolicyCoverage(relevantChunks, question);
                LogMetric($"QueryTimeMs={stopwatch.ElapsedMilliseconds} | TopChunks={topChunks.Count} | Confidence={confidence:F2}");

                return new QueryResponse
                {
                    Answer = answer,
                    Confidence = confidence,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    IsFromCorrection = false,
                    Sources = topChunks.Select(c => c.Source).Distinct().ToList(),
                    RelevantChunks = topChunks,
                    SessionId = dbSession.SessionId,
                    ModelsUsed = new Dictionary<string, string>
                    {
                        ["generation"] = generationModel,
                        ["embedding"] = embeddingModel
                    },
                    Plant = plant,
                    HasSufficientPolicyCoverage = coverage
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Query processing failed for session {SessionId}, plant {Plant}", sessionId, plant);
                _metrics.RecordQueryProcessing(stopwatch.ElapsedMilliseconds, 0, false);
                throw new RAGServiceException($"Failed to process query for plant {plant}", ex);
            }
        }


        // Add this to your class
        private readonly ConcurrentDictionary<string, (List<float> Embedding, DateTime Cached)> _sessionEmbeddingCache = new();
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
                    CreatedAt = DateTime.Now,
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
                    CreatedAt = DateTime.Now,
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
                var response = await _ollamaClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);

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
                LastUpdated = DateTime.Now,
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
                var ollamaHealth = await _ollamaClient.GetAsync("/api/tags");
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
                if (!ids.Any())
                {
                    _logger.LogWarning("AddToChromaDBAsync called with empty ids list");
                    return false;
                }

                _logger.LogInformation($"💾 Adding {ids.Count} documents to ChromaDB collection: {collectionId}");

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

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ ChromaDB add failed: {response.StatusCode}");
                    _logger.LogError($"Error details: {errorContent}");
                    _logger.LogError($"Collection: {collectionId}, Documents: {ids.Count}");
                    return false;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"✅ ChromaDB response: {responseContent}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to add to ChromaDB collection: {collectionId}");
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
                Date = DateTime.Now
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
                        finalAnswer = await _helperMethods.RephraseWithLLMAsync(correction.Answer, generationModel ?? _config.DefaultGenerationModel);
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
                stream = true,
                options = new Dictionary<string, object>
        {
            { "num_ctx", 4096 }, // Reduced context for faster processing
            { "top_p", 0.9 }
        }
            };

            try
            {
                var response = await _ollamaClient.PostAsJsonAsync("/api/chat", requestData);
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
        private async Task UpdateConversationHistoryLightweight(ConversationContext context, string question, string answer)
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
        private async Task<int> SaveNonMeaiConversationToDatabase(string sessionId, string question, string answer, ModelConfiguration generationModel, double confidence, long processingTimeMs, bool isFromCorrection, string plant)
        {
            try
            {
                // For non-MEAI queries, we skip embedding generation but still save conversation
                var entry = new ConversationEntry
                {
                    SessionId = sessionId,
                    Question = question,
                    Answer = answer,
                    CreatedAt = DateTime.Now,
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

        private async Task<int> ProcessChunkBatchForModelAsync(
    List<(string Text, string SourceFile, string SectionId, string Title)> chunks,
    ModelConfiguration model,
    string collectionId,
    DateTime lastModified,
    string plant)
        {
            try
            {
                _logger.LogInformation($"🔄 Processing batch of {chunks.Count} chunks for model {model.Name}");

                // Filter and prepare chunks
                var validChunks = chunks
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                    .Select(chunk => new
                    {
                        Text = _stringProcessor.CleanText(chunk.Text),
                        SourceFile = chunk.SourceFile,
                        ChunkId = GenerateChunkId(chunk.SourceFile, chunk.Text, lastModified, model.Name),
                        SectionId = chunk.SectionId,
                        Title = chunk.Title
                    })
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text) && chunk.Text.Length >= 20)
                    .ToList();

                if (!validChunks.Any())
                {
                    _logger.LogWarning($"⚠️ No valid chunks after filtering");
                    return 0;
                }

                _logger.LogInformation($"✓ {validChunks.Count} valid chunks after filtering");

                // Check existing chunks
                var existingChunks = await CheckExistingChunksAsync(collectionId, validChunks.Select(c => c.ChunkId).ToList());
                var newChunks = validChunks.Where(c => !existingChunks.Contains(c.ChunkId)).ToList();

                if (!newChunks.Any())
                {
                    _logger.LogInformation($"✓ All {validChunks.Count} chunks already exist in collection");
                    return validChunks.Count; // Return success
                }

                _logger.LogInformation($"📝 Generating embeddings for {newChunks.Count} new chunks");

                // Process chunks with detailed error tracking
                var successfulChunks = new List<(string Text, string ChunkId, List<float> Embedding, string SourceFile, string SectionId, string Title)>();
                var failedChunks = new List<(string ChunkId, string Error)>();

                for (int i = 0; i < newChunks.Count; i++)
                {
                    var chunk = newChunks[i];
                    try
                    {
                        _logger.LogDebug($"🔤 [{i + 1}/{newChunks.Count}] Generating embedding for chunk: {chunk.ChunkId}");

                        var embedding = await GetEmbeddingAsync(chunk.Text, model);

                        if (embedding == null || embedding.Count == 0)
                        {
                            var error = $"Empty embedding returned";
                            _logger.LogWarning($"⚠️ {error} for chunk: {chunk.ChunkId}");
                            _logger.LogWarning($"Text preview: {chunk.Text.Substring(0, Math.Min(200, chunk.Text.Length))}");
                            failedChunks.Add((chunk.ChunkId, error));
                            continue;
                        }

                        if (embedding.Count != model.EmbeddingDimension)
                        {
                            var error = $"Embedding dimension mismatch: got {embedding.Count}, expected {model.EmbeddingDimension}";
                            _logger.LogError($"❌ {error}");
                            failedChunks.Add((chunk.ChunkId, error));
                            continue;
                        }

                        successfulChunks.Add((chunk.Text, chunk.ChunkId, embedding, chunk.SourceFile, chunk.SectionId, chunk.Title));
                        _logger.LogDebug($"✓ Successfully generated embedding {i + 1}/{newChunks.Count}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Failed to generate embedding for chunk {i + 1}/{newChunks.Count}: {chunk.ChunkId}");
                        _logger.LogError($"Problematic text: {chunk.Text.Substring(0, Math.Min(200, chunk.Text.Length))}");
                        failedChunks.Add((chunk.ChunkId, ex.Message));
                    }

                    // Add small delay to avoid overwhelming the API
                    if (i < newChunks.Count - 1)
                    {
                        await Task.Delay(50);
                    }
                }

                // Log summary
                _logger.LogInformation($"📊 Embedding generation complete: {successfulChunks.Count} succeeded, {failedChunks.Count} failed");

                if (failedChunks.Any())
                {
                    _logger.LogWarning($"⚠️ Failed chunks: {string.Join(", ", failedChunks.Select(f => f.ChunkId))}");
                }

                if (!successfulChunks.Any())
                {
                    _logger.LogError($"❌ No embeddings generated successfully - cannot save to ChromaDB");
                    return 0;
                }

                // Prepare data for ChromaDB
                var documents = successfulChunks.Select(c => c.Text).ToList();
                var ids = successfulChunks.Select(c => c.ChunkId).ToList();
                var embeddings = successfulChunks.Select(c => c.Embedding).ToList();
                var metadatas = successfulChunks.Select(c =>
                    CreateChunkMetadata(c.SourceFile, lastModified, model.Name, c.Text, plant, c.SectionId, c.Title)).ToList();

                _logger.LogInformation($"💾 Saving {successfulChunks.Count} chunks to ChromaDB collection: {collectionId}");

                // Save to ChromaDB with error handling
                var saveSuccess = await AddToChromaDBAsync(collectionId, ids, embeddings, documents, metadatas);

                if (saveSuccess)
                {
                    _logger.LogInformation($"✅ Successfully saved {successfulChunks.Count} chunks to ChromaDB");
                    return successfulChunks.Count;
                }
                else
                {
                    _logger.LogError($"❌ Failed to save chunks to ChromaDB");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to process chunk batch for model {model.Name}");
                return 0;
            }
        }


        //private async Task ProcessChunkBatchForModelAsync(List<(string Text, string SourceFile, string SectionId, string Title)> chunks, ModelConfiguration model, string collectionId, DateTime lastModified, string plant)
        //{
        //    try
        //    {
        //        // Filter and prepare chunks in parallel
        //        var validChunks = chunks
        //            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
        //            .Select(chunk => new
        //            {
        //                Text = _stringProcessor.CleanText(chunk.Text),
        //                SourceFile = chunk.SourceFile,
        //                ChunkId = GenerateChunkId(chunk.SourceFile, chunk.Text, lastModified, model.Name)
        //            })
        //            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
        //            .ToList();

        //        if (!validChunks.Any()) return;

        //        // 🔧 DEBUG: Log problematic chunks
        //        _logger.LogInformation($"🔄 Processing {validChunks.Count} chunks for model {model.Name}");

        //        // Check which chunks already exist (batch check)
        //        var existingChunks = await CheckExistingChunksAsync(collectionId, validChunks.Select(c => c.ChunkId).ToList());
        //        var newChunks = validChunks.Where(c => !existingChunks.Contains(c.ChunkId)).ToList();

        //        if (!newChunks.Any())
        //        {
        //            _logger.LogDebug($"All chunks already exist for {model.Name}");
        //            return;
        //        }

        //        _logger.LogInformation($"📝 Generating embeddings for {newChunks.Count} new chunks");

        //        // 🔧 Process chunks individually to identify problematic ones
        //        var successfulChunks = new List<(string Text, string ChunkId, List<float> Embedding)>();

        //        for (int i = 0; i < newChunks.Count; i++)
        //        {
        //            try
        //            {
        //                _logger.LogDebug($"🔤 Processing chunk {i + 1}/{newChunks.Count}: {newChunks[i].ChunkId}");
        //                var embedding = await GetEmbeddingAsync(newChunks[i].Text, model);

        //                if (embedding.Count > 0)
        //                {
        //                    successfulChunks.Add((newChunks[i].Text, newChunks[i].ChunkId, embedding));
        //                }
        //                else
        //                {
        //                    _logger.LogWarning($"⚠️ Failed to generate embedding for chunk: {newChunks[i].ChunkId}");
        //                    _logger.LogWarning($"⚠️ Problematic text preview: {newChunks[i].Text.Substring(0, Math.Min(100, newChunks[i].Text.Length))}");
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                _logger.LogError(ex, $"❌ Failed to process individual chunk: {newChunks[i].ChunkId}");
        //                _logger.LogError($"❌ Problematic text: {newChunks[i].Text.Substring(0, Math.Min(200, newChunks[i].Text.Length))}");
        //            }
        //        }

        //        if (successfulChunks.Any())
        //        {
        //            // Prepare data for ChromaDB
        //            var documents = successfulChunks.Select(c => c.Text).ToList();
        //            var ids = successfulChunks.Select(c => c.ChunkId).ToList();
        //            var embeddings = successfulChunks.Select(c => c.Embedding).ToList();
        //            var metadatas = successfulChunks.Select(c =>
        //                CreateChunkMetadata(newChunks.First(nc => nc.ChunkId == c.ChunkId).SourceFile,
        //                                  lastModified, model.Name, c.Text, plant)).ToList();

        //            await AddToChromaDBAsync(collectionId, ids, embeddings, documents, metadatas);
        //            _logger.LogInformation($"✅ Added {successfulChunks.Count} new chunks for model {model.Name}");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Failed to process chunk batch for model {model.Name}");
        //    }
        //}
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
                Timestamp = DateTime.Now
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
        private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, ModelConfiguration embeddingModel, int maxResults, string plant)
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
                                var keywords = _textChunking.ExtractKeywordsFromSectionContent(document, sectionTitle);
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
        private async Task<List<RelevantChunk>> SearchForSpecificSection(SectionQuery sectionQuery, ModelConfiguration embeddingModel, int maxResults, string plant, string collectionId)
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

        private Dictionary<string, object> CreateChunkMetadata(string sourceFile, DateTime lastModified, string modelName, string text, string plant, string sectionId = "", string title = "", string documentType = "")
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
        { "processed_at", DateTime.Now.ToString("O") },
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
        private async Task<List<RelevantChunk>> GetRelevantChunksWithExpansionAsync(string query, ModelConfiguration embeddingModel, int maxResults, bool meaiInfo, ConversationContext context, bool useReRanking, ModelConfiguration generationModel, string plant)
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
        private async Task<List<RelevantChunk>> PerformChromaSearch(string query, ModelConfiguration embeddingModel, int maxResults, string plant, string collectionId)
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
        private async Task<List<RelevantChunk>> SearchGeneral(string query, ModelConfiguration embeddingModel, int maxResults, string plant, string collectionId)
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
        //public async Task InitializeAsync()
        //{
        //    if (_isInitialized) return;

        //    try
        //    {
        //        _logger.LogInformation("🚀 Starting parallel RAG system initialization");

        //        // Parallel model discovery and configuration
        //        var initTasks = new List<Task>
        //{
        //    Task.Run(async () =>
        //    {
        //        var availableModels = await _modelManager.DiscoverAvailableModelsAsync();
        //        await ConfigureDefaultModelsAsync(availableModels);
        //        return availableModels;
        //    }),
        //    Task.Run(() => EnsureDirectoriesExist()),
        //    Task.Run(() => EnsureAbbreviationContext()),
        //    Task.Run(async () => await LoadCorrectionCacheAsync()),
        //    Task.Run(async () => await LoadHistoricalAppreciatedAnswersAsync())
        //};

        //        await Task.WhenAll(initTasks);

        //        // Parallel document processing for all plants
        //        var plantTasks = _plants.Plants.Keys.Select(async plant =>
        //        {
        //            _logger.LogInformation($"Processing documents for plant: {plant}");
        //            var models = await _modelManager.DiscoverAvailableModelsAsync();
        //            var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();
        //            await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);
        //        });

        //        await Task.WhenAll(plantTasks);

        //        _isInitialized = true;
        //        _logger.LogInformation("✅ Parallel RAG system initialization completed");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "❌ Failed to initialize RAG system");
        //        throw;
        //    }
        //}

        private bool isInitialized = false;
        private readonly object initLock = new object();
        // Add these fields to your class
        private readonly ConcurrentDictionary<string, bool> _plantInitialized = new();
        private readonly ConcurrentDictionary<string, DateTime> _plantLastScan = new();
        private bool _systemInitialized = false;
        private readonly object _systemInitLock = new();
        public async Task InitializeAsync()
        {
            // ✅ Check if system is already initialized
            lock (_systemInitLock)
            {
                if (_systemInitialized)
                {
                    _logger.LogInformation("✓ System already initialized, skipping full initialization");
                    return;
                }
            }

            try
            {
                _logger.LogInformation("🚀 Starting RAG system initialization");

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
            Task.Run(() => EnsurePlantSpecificOrganizationContext()),
            Task.Run(async () => await LoadCorrectionCacheAsync()),
            Task.Run(async () => await LoadHistoricalAppreciatedAnswersAsync())
        };

                await Task.WhenAll(initTasks);

                // ✅ ONLY process plants that haven't been initialized
                var models = await _modelManager.DiscoverAvailableModelsAsync();
                var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();

                var plantTasks = _plants.Plants.Keys.Select(async plant =>
                {
                    // Check if this plant was already initialized
                    if (_plantInitialized.TryGetValue(plant, out var initialized) && initialized)
                    {
                        _logger.LogInformation($"✓ Plant '{plant}' already initialized, checking for changes only");
                        await CheckForChangedFilesAsync(plant, embeddingModels);
                        return;
                    }

                    _logger.LogInformation($"📄 Processing documents for NEW plant: {plant}");
                    await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);
                    _plantInitialized[plant] = true;
                    _plantLastScan[plant] = DateTime.Now;
                });

                await Task.WhenAll(plantTasks);

                lock (_systemInitLock)
                {
                    _systemInitialized = true;
                }

                _logger.LogInformation("✅ RAG system initialization completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize RAG system");
                throw;
            }
        }

        /// ✅ NEW: Only check and process files that have changed
        /// </summary>
        private async Task CheckForChangedFilesAsync(string plant, List<ModelConfiguration> embeddingModels)
        {
            try
            {
                var policyFiles = GetPolicyFiles(plant);
                var changedFiles = new List<string>();

                // Check which files have changed since last scan
                foreach (var filePath in policyFiles)
                {
                    var fileInfo = new FileInfo(filePath);
                    var lastModified = fileInfo.LastWriteTime;

                    // Check if file was modified after last scan
                    if (_plantLastScan.TryGetValue(plant, out var lastScan))
                    {
                        if (lastModified > lastScan)
                        {
                            changedFiles.Add(filePath);
                            _logger.LogInformation($"🔄 Detected change: {Path.GetFileName(filePath)}");
                        }
                    }
                    else
                    {
                        // No previous scan, add file
                        changedFiles.Add(filePath);
                    }
                }

                if (!changedFiles.Any())
                {
                    _logger.LogInformation($"✓ No changes detected for plant '{plant}'");
                    return;
                }

                _logger.LogInformation($"📝 Processing {changedFiles.Count} changed files for plant '{plant}'");

                // Only process changed files
                foreach (var model in embeddingModels)
                {
                    var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);

                    foreach (var filePath in changedFiles)
                    {
                        await ProcessFileForModelAsync(filePath, model, collectionId, plant);
                    }
                }

                // Update last scan time
                _plantLastScan[plant] = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to check for changed files in plant: {plant}");
            }
        }

        public async Task ReinitializePlantAsync(string plant)
        {
            try
            {
                _logger.LogInformation($"🔄 Force reinitializing plant: {plant}");

                // Clear plant initialization flag
                _plantInitialized.TryRemove(plant, out _);
                _plantLastScan.TryRemove(plant, out _);

                // Clear all file caches for this plant
                var policyFiles = GetPolicyFiles(plant);
                foreach (var file in policyFiles)
                {
                    await ClearFileCacheAsync(file);
                }

                // Reprocess
                var models = await _modelManager.DiscoverAvailableModelsAsync();
                var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();

                await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);

                _plantInitialized[plant] = true;
                _plantLastScan[plant] = DateTime.Now;

                _logger.LogInformation($"✅ Plant reinitialized: {plant}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reinitialize plant: {plant}");
                throw;
            }
        }

        public async Task LoadCachesOnlyAsync()
        {
            try
            {
                _logger.LogInformation("📂 Loading caches without reprocessing files");

                // Load essential caches
                await Task.WhenAll(
                    LoadCorrectionCacheAsync(),
                    LoadHistoricalAppreciatedAnswersAsync()
                );

                // Mark system as initialized (prevents full init)
                lock (_systemInitLock)
                {
                    _systemInitialized = true;
                }

                // Mark all configured plants as initialized
                foreach (var plant in _plants.Plants.Keys)
                {
                    _plantInitialized[plant] = true;
                }

                _logger.LogInformation($"✅ Caches loaded - {processedFilesCache.Count} files in cache");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load caches");
                throw;
            }
        }

        public async Task ReprocessFileAsync(string filePath, string plant)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"File not found: {filePath}");
                }

                _logger.LogInformation($"🔄 Force reprocessing file: {filePath}");

                // Clear cache for this file
                await ClearFileCacheAsync(filePath);

                // Process with all embedding models
                var models = await _modelManager.DiscoverAvailableModelsAsync();
                var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();

                foreach (var model in embeddingModels)
                {
                    var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);
                    await ProcessFileForModelAsync(filePath, model, collectionId, plant);
                }

                _logger.LogInformation($"✅ File reprocessed successfully: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reprocess file: {filePath}");
                throw;
            }
        }
        private async Task<List<float>> GetEmbeddingAsync(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("GetEmbeddingAsync called with empty text");
                return new List<float>();
            }

            if (model == null)
            {
                _logger.LogError("GetEmbeddingAsync called with null model");
                return new List<float>();
            }

            var cacheKey = $"{model.Name}:{text.GetHashCode():X}";

            // Check cache first
            if (_optimizedEmbeddingCache.TryGetValue(cacheKey, out var cached))
            {
                if (DateTime.Now - cached.Cached < TimeSpan.FromHours(24))
                {
                    _logger.LogDebug($"✓ Cache hit for embedding");
                    _optimizedEmbeddingCache[cacheKey] = (cached.Embedding, cached.Cached, cached.AccessCount + 1);
                    return cached.Embedding;
                }
            }

            await _globalEmbeddingSemaphore.WaitAsync();
            try
            {
                // ✅ CRITICAL FIX: Aggressively clean ALL model control tokens
                var processedText = StripAllModelTokens(text);

                if (string.IsNullOrWhiteSpace(processedText) || processedText.Length < 3)
                {
                    _logger.LogError($"❌ Text too short after cleaning: '{processedText}'");
                    return new List<float>();
                }

                // ✅ Limit text length (important for preventing token issues)
                if (processedText.Length > 2000)
                {
                    processedText = processedText.Substring(0, 2000).Trim();
                    _logger.LogDebug($"⚠️ Text truncated to 2000 chars");
                }

                _logger.LogDebug($"🔄 Generating embedding for text ({processedText.Length} chars)");

                // ✅ Use the SIMPLEST request format possible
                var request = new
                {
                    model = model.Name,
                    prompt = processedText,  // Keep "prompt" - it's standard across Ollama versions
                    options = new Dictionary<string, object>() // Empty options to avoid conflicts
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var response = await _ollamaClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"❌ Embedding request failed: {response.StatusCode}");
                    _logger.LogError($"Error: {errorContent}");
                    _logger.LogError($"Model: {model.Name}, Text preview: {processedText.Substring(0, Math.Min(100, processedText.Length))}");
                    return new List<float>();
                }

                var json = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogError($"❌ Empty response from embedding API");
                    return new List<float>();
                }

                // ✅ Parse response
                using var doc = JsonDocument.Parse(json);

                // Try standard format
                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                {
                    var embedding = embeddingProperty.EnumerateArray()
                        .Select(x => x.GetSingle())
                        .ToList();

                    if (embedding.Count > 0)
                    {
                        // Verify dimension matches
                        if (model.EmbeddingDimension > 0 && embedding.Count != model.EmbeddingDimension)
                        {
                            _logger.LogWarning($"⚠️ Dimension mismatch: got {embedding.Count}, expected {model.EmbeddingDimension}");
                        }

                        _optimizedEmbeddingCache.TryAdd(cacheKey, (embedding, DateTime.Now, 1));
                        _logger.LogDebug($"✓ Embedding generated: {embedding.Count}D");
                        return embedding;
                    }
                }

                _logger.LogError($"❌ No embedding in response. JSON: {json.Substring(0, Math.Min(200, json.Length))}");
                return new List<float>();
            }
            catch (OperationCanceledException)
            {
                _logger.LogError($"❌ Embedding timeout for model: {model.Name}");
                return new List<float>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Embedding failed for model: {model.Name}");
                return new List<float>();
            }
            finally
            {
                _globalEmbeddingSemaphore.Release();
            }
        }

        private string StripAllModelTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var cleaned = text;

            // ✅ 1. Remove ALL special token patterns (exhaustive list)
            var specialTokenPatterns = new[]
            {
        // LLaMA/Mistral tokens
        @"<\|im_start\|>", @"<\|im_end\|>", @"<\|im_sep\|>",
        @"<\|endoftext\|>", @"<\|startoftext\|>",
        
        // Instruction format tokens
        @"\[INST\]", @"\[/INST\]",
        @"<<SYS>>", @"<</SYS>>",
        
        // Generic model tokens
        @"<s>", @"</s>",
        @"<\|user\|>", @"<\|assistant\|>", @"<\|system\|>",
        @"<assistant>", @"</assistant>",
        @"<user>", @"</user>",
        @"<system>", @"</system>",
        
        // Chat-ML tokens
        @"<\|begin_of_text\|>", @"<\|end_of_text\|>",
        @"<\|start_header_id\|>", @"<\|end_header_id\|>",
        
        // Other potential tokens
        @"<\|pad\|>", @"<\|eos\|>", @"<\|bos\|>",
        
        // Remove any remaining angle bracket tokens
        @"<\|[^>]+\|>"
    };

            foreach (var pattern in specialTokenPatterns)
            {
                cleaned = Regex.Replace(cleaned, pattern, " ", RegexOptions.IgnoreCase);
            }

            // ✅ 2. Remove markdown code blocks (can contain tokens)
            cleaned = Regex.Replace(cleaned, @"```[\w]*\n?", "", RegexOptions.Multiline);
            cleaned = cleaned.Replace("```", "");

            // ✅ 3. Remove common instruction prefixes that might trigger token detection
            var instructionPrefixes = new[]
            {
        "### Instruction:", "### Response:", "### System:",
        "Human:", "Assistant:", "AI:",
        "Question:", "Answer:"
    };

            foreach (var prefix in instructionPrefixes)
            {
                cleaned = cleaned.Replace(prefix, "", StringComparison.OrdinalIgnoreCase);
            }

            // ✅ 4. Normalize whitespace
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            cleaned = Regex.Replace(cleaned, @"\n\s*\n", "\n");

            // ✅ 5. Remove control characters (except newline and tab)
            cleaned = Regex.Replace(cleaned, @"[\x00-\x08\x0B-\x0C\x0E-\x1F\x7F]", "");

            // ✅ 6. Remove any remaining suspicious patterns
            // Remove HTML-like tags that might be interpreted as tokens
            cleaned = Regex.Replace(cleaned, @"</?[a-zA-Z][^>]*>", " ");

            // ✅ 7. Final cleanup
            cleaned = cleaned.Trim();

            // ✅ 8. Ensure text is plain and safe
            if (cleaned.Contains("<|") || cleaned.Contains("|>") ||
                cleaned.Contains("[INST]") || cleaned.Contains("<<"))
            {
                _logger.LogWarning($"⚠️ Text still contains suspicious tokens after cleaning!");
                _logger.LogWarning($"Text preview: {cleaned.Substring(0, Math.Min(200, cleaned.Length))}");
            }

            return cleaned;
        }

        private string StripAllModelTokensAggressive(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // ✅ NUCLEAR OPTION: Only keep safe characters
            var result = new StringBuilder(text.Length);

            foreach (char c in text)
            {
                // Keep alphanumeric, whitespace, and common punctuation
                if (char.IsLetterOrDigit(c) ||
                    char.IsWhiteSpace(c) ||
                    c == '.' || c == ',' || c == '!' || c == '?' ||
                    c == ';' || c == ':' || c == '-' || c == '(' ||
                    c == ')' || c == '\'' || c == '"' || c == '/')
                {
                    result.Append(c);
                }
                else
                {
                    result.Append(' '); // Replace unsafe chars with space
                }
            }

            var cleaned = result.ToString();

            // Remove excessive whitespace
            cleaned = Regex.Replace(cleaned, @"\s+", " ");

            return cleaned.Trim();
        }

        /// Fallback method using old /api/embeddings endpoint
        /// </summary>

        private void CleanupEmbeddingCache(object state)
        {
            try
            {
                var cutoff = DateTime.Now.AddHours(-12);
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




        public async IAsyncEnumerable<StreamChunk> ProcessQueryStreamAsync(
    string question,
    string plant,
    string? generationModel = null,
    string? embeddingModel = null,
    int maxResults = 10,
    bool meaiInfo = true,
    string? sessionId = null,
    bool useReRanking = true,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamChunk? errorChunk = null;
            var stopwatch = Stopwatch.StartNew();
            yield return new StreamChunk { Type = "status", Content = "Processing query..." };

            var sessionResult = await SafeInitializeSession(sessionId);
            if (sessionResult.HasError)
            {
                yield return new StreamChunk { Type = "error", Content = sessionResult.ErrorMessage };
                yield break;
            }

            var context = sessionResult.Context!;

            QuestionType questionType = QuestionType.NewTopic;

            if (context.History.Any())
            {
                questionType = ClassifyQuestionType(question, context);

                // Provide feedback to user
                switch (questionType)
                {
                    case QuestionType.FollowUp:
                        yield return new StreamChunk
                        {
                            Type = "status",
                            Content = "Continuing previous conversation..."
                        };
                        _logger.LogInformation($"Follow-up detected: '{question}'");
                        break;

                    case QuestionType.RelatedTopic:
                        yield return new StreamChunk
                        {
                            Type = "status",
                            Content = "Related question detected, adjusting context..."
                        };
                        _logger.LogInformation($"Related topic: '{question}'");
                        break;

                    case QuestionType.NewTopic:
                        yield return new StreamChunk
                        {
                            Type = "status",
                            Content = "New topic detected, starting fresh..."
                        };
                        _logger.LogInformation($"New topic: '{question}'");
                        ClearContext(context); // ✅ Clear old context
                        break;
                }
            }

            // Early validation
            if (string.IsNullOrWhiteSpace(question))
            {
                yield return new StreamChunk { Type = "error", Content = "Question cannot be empty" };
                yield break;
            }

            if (cancellationToken.IsCancellationRequested)
                yield break;

            // ✅ NEW: Expand abbreviations early in the pipeline
            yield return new StreamChunk { Type = "status", Content = "Processing query..." };

            var originalQuestion = question;
            var expandedQuestion = _abbreviationService.ExpandQuery(question);

            if (expandedQuestion != originalQuestion)
            {
                _logger.LogInformation($"🔄 Query expanded: '{originalQuestion}' → '{expandedQuestion}'");
                yield return new StreamChunk
                {
                    Type = "info",
                    Content = $"Interpreting abbreviations in your query..."
                };

                // Use expanded question for the rest of processing
                question = expandedQuestion;
            }

            // Set defaults
            generationModel ??= _config.DefaultGenerationModel;
            embeddingModel ??= _config.DefaultEmbeddingModel;

            yield return new StreamChunk { Type = "status", Content = "Initializing session..." };

            if (sessionResult.HasError)
            {
                yield return new StreamChunk { Type = "error", Content = sessionResult.ErrorMessage };
                yield break;
            }

            if (cancellationToken.IsCancellationRequested) yield break;

            // Check appreciated answers first (fastest path)
            yield return new StreamChunk { Type = "status", Content = "Checking knowledge cache..." };

            var appreciatedResult = await SafeCheckAppreciatedAnswer(question);
            if (appreciatedResult.Answer.Any())
            {
                yield return new StreamChunk { Type = "status", Content = "Found cached answer!" };
                await foreach (var chunk in StreamTextResponse(appreciatedResult.Answer, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) yield break;
                    yield return chunk;
                }
                yield return new StreamChunk { Type = "complete", Content = "", ProcessingTimeMs = stopwatch.ElapsedMilliseconds };
                yield break;
            }

            // Check corrections
            var correctionResult = await SafeCheckCorrections(question);
            if (correctionResult.Answer.Any())
            {
                yield return new StreamChunk { Type = "status", Content = "Applying user correction..." };

                var rephrasedAnswer = await SafeRephraseWithLLM(correctionResult.Answer, generationModel);
                var finalAnswer = rephrasedAnswer ?? correctionResult.Answer;

                await foreach (var chunk in StreamTextResponse(finalAnswer, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) yield break;
                    yield return chunk;
                }
                yield return new StreamChunk { Type = "complete", Content = "", ProcessingTimeMs = stopwatch.ElapsedMilliseconds };
                yield break;
            }

            // Handle history clear
            if (IsHistoryClearRequest(question))
            {
                yield return new StreamChunk { Type = "status", Content = "Clearing conversation history..." };
                ClearContext(context);
                yield return new StreamChunk { Type = "response", Content = "Conversation history cleared. How can I help you?" };
                yield return new StreamChunk { Type = "complete", Content = "", ProcessingTimeMs = stopwatch.ElapsedMilliseconds };
                yield break;
            }

            // Fast path for non-MEAI queries
            if (!meaiInfo)
            {
                yield return new StreamChunk { Type = "status", Content = "Processing general query..." };
                await foreach (var chunk in ProcessNonMeaiQueryStream(question, sessionId, generationModel, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) yield break;
                    yield return chunk;
                }
                yield return new StreamChunk { Type = "complete", Content = "", ProcessingTimeMs = stopwatch.ElapsedMilliseconds };
                yield break;
            }

            // Full MEAI processing
            yield return new StreamChunk { Type = "status", Content = "Loading AI models..." };

            // Validate models - handle safely
            var modelResult = await SafeValidateModels(generationModel, embeddingModel);
            if (modelResult.HasError)
            {
                yield return new StreamChunk { Type = "error", Content = "Required AI models are not available" };
                yield break;
            }

            var genModel = modelResult.GenerationModel;
            var embModel = modelResult.EmbeddingModel;

            if (cancellationToken.IsCancellationRequested) yield break;

            yield return new StreamChunk { Type = "status", Content = "Processing your question..." };

            // Check for topic changes
            if (_conversationAnalysis.IsTopicChanged(question, context))
            {
                yield return new StreamChunk { Type = "status", Content = "New topic detected, refreshing context..." };
                ClearContext(context);
            }

            var contextualQuery = _conversationAnalysis.BuildContextualQuery(question, context.History);

            if (cancellationToken.IsCancellationRequested) yield break;

            // Search for relevant information
            yield return new StreamChunk { Type = "status", Content = "Searching knowledge base..." };

            var searchResult = await SafeSearchKnowledgeBaseWithExpansion(
        originalQuestion,      // Keep original for context
        expandedQuestion,      // Use expanded for better retrieval
        contextualQuery,
        embModel,
        maxResults,
        plant,
        useReRanking
    );
            if (searchResult.HasError)
            {
                yield return new StreamChunk { Type = "error", Content = "Failed to search knowledge base" };
                yield break;
            }

            var relevantChunks = searchResult.Chunks;

            if (cancellationToken.IsCancellationRequested) yield break;

            // Show found sources - THIS MATCHES HTML EXPECTATIONS
            if (relevantChunks.Any())
            {
                yield return new StreamChunk
                {
                    Type = "sources",
                    Content = $"Found {relevantChunks.Count} relevant sources",
                    Sources = relevantChunks.Select(c => c.Source).Distinct().ToList()
                };

                // Stream preview of top chunks - MATCHING HTML FORMAT
                for (int i = 0; i < Math.Min(10, relevantChunks.Count); i++)
                {
                    var chunk = relevantChunks[i];
                    var preview = chunk.Text;
                    yield return new StreamChunk
                    {
                        Type = "chunk",
                        Content = preview,
                        Source = chunk.Source,
                        Similarity = chunk.Similarity,
                        TextPreview = preview  // HTML expects this property
                    };
                }
            }

            yield return new StreamChunk { Type = "status", Content = "Generating response..." };

            List<float> questionEmbedding = new List<float>();
            try
            {
                questionEmbedding = await GetEmbeddingAsync(question, embModel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate question embedding");
            }

            // Generate the actual response with streaming
            var fullResponse = new StringBuilder();
            var hasError = false;
            string? errorMessage = null;

            // ✅ Generate response without try-catch around yield
            await foreach (var token in GenerateStreamingResponseSafe(
                question,
                genModel.Name!,
                context,
                relevantChunks,
                meaiInfo,
                plant,
                questionType,
                cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                // Check if this is an error token
                if (token.StartsWith("__ERROR__:"))
                {
                    hasError = true;
                    errorMessage = token.Substring(10);
                    break;
                }

                fullResponse.Append(token);

                yield return new StreamChunk
                {
                    Type = "response",
                    Content = token
                };
            }

            if (!hasError && fullResponse.Length > 0)
            {
                try
                {
                    _logger.LogInformation($"💾 Saving conversation to database...");

                    // ✅ Extract entities from the answer
                    var entities = await _entityExtraction.ExtractEntitiesAsync(fullResponse.ToString());

                    // ✅ Optionally generate answer embedding (can be null if you want to skip)
                    List<float>? answerEmbedding = null;
                    try
                    {
                        answerEmbedding = await GetEmbeddingAsync(fullResponse.ToString(), embModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate answer embedding, continuing without it");
                    }

                    var conversationId = await SaveConversationToDatabaseFast(
                        sessionResult.Session!.SessionId,
                        question,
                        fullResponse.ToString(),
                        relevantChunks,
                        genModel!,
                        embModel!,
                        relevantChunks.Any() ? relevantChunks.Average(c => c.Similarity) : 0.8, // confidence
                        stopwatch.ElapsedMilliseconds,
                        false, // isFromCorrection
                        null, // parentId
                        plant,
                        questionEmbedding, // ✅ Now defined
                        answerEmbedding, // ✅ Now defined (can be null)
                        entities); // ✅ Now defined

                    _logger.LogInformation($"✅ Saved conversation with ID: {conversationId}");

                    if (conversationId > 0)
                    {
                        // ✅ Update conversation history in memory
                        await UpdateConversationHistoryFast(context, question, fullResponse.ToString(), relevantChunks, entities);

                        var verification = await _conversationStorage.GetSessionConversationsAsync(sessionResult.Session!.SessionId);
                        _logger.LogInformation($"🔍 Verification: Database now has {verification.Count} conversations for this session");
                    }
                    else
                    {
                        _logger.LogError($"❌ Failed to save conversation - returned ID is {conversationId}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to save conversation to database");
                }
            }
            stopwatch.Stop();

            yield return new StreamChunk
            {
                Type = "complete",
                Content = "Done",
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds
            };

            // Handle errors after yield loop
            if (hasError)
            {
                yield return new StreamChunk
                {
                    Type = "error",
                    Content = errorMessage ?? "Unknown error occurred"
                };
                yield break;
            }

            // Update context (outside yield loop, so we can use try-catch)
            try
            {
                if (fullResponse.Length > 0)
                {

                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update conversation context (non-critical)");
            }

            yield return new StreamChunk
            {
                Type = "complete",
                Content = "Done",
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds
            };
        }

        private QuestionType ClassifyQuestionType(string question, ConversationContext context)
        {
            if (!context.History.Any())
                return QuestionType.NewTopic;

            var lastTurn = context.History.Last();

            // ✅ METHOD 1: Fast pattern detection using your existing service
            var isFollowUpPattern = _conversationAnalysis.IsQuestionPatternContinuation(question, context);

            if (isFollowUpPattern)
            {
                _logger.LogInformation($"Pattern match: follow-up question");
                return QuestionType.FollowUp;
            }

            // ✅ METHOD 2: Topic extraction using your existing service
            var currentTopics = _conversationAnalysis.ExtractKeyTopics(question);
            var previousTopics = _conversationAnalysis.ExtractKeyTopics(lastTurn.Question);

            if (currentTopics.Any() && previousTopics.Any())
            {
                var overlap = currentTopics.Intersect(previousTopics, StringComparer.OrdinalIgnoreCase).Count();
                var totalUnique = currentTopics.Union(previousTopics).Count();

                if (totalUnique > 0)
                {
                    var similarityRatio = (double)overlap / totalUnique;

                    if (similarityRatio >= 0.4) // 40% overlap = follow-up
                    {
                        _logger.LogInformation($"Topic overlap: {similarityRatio:P0} - follow-up");
                        return QuestionType.FollowUp;
                    }
                    else if (similarityRatio >= 0.2) // 20-40% = related
                    {
                        _logger.LogInformation($"Topic overlap: {similarityRatio:P0} - related");
                        return QuestionType.RelatedTopic;
                    }
                }
            }

            // ✅ METHOD 3: Named entity overlap
            if (context.NamedEntities.Any())
            {
                var questionLower = question.ToLower();
                var matchedEntities = context.NamedEntities
                    .Where(entity => questionLower.Contains(entity.ToLower()))
                    .ToList();

                if (matchedEntities.Count >= 2)
                {
                    _logger.LogInformation($"Entity match: {string.Join(", ", matchedEntities)} - related");
                    return QuestionType.RelatedTopic;
                }
                else if (matchedEntities.Count == 1)
                {
                    return QuestionType.LooselyRelated;
                }
            }

            // ✅ Default: New topic
            _logger.LogInformation("No relationship detected - new topic");
            return QuestionType.NewTopic;
        }


        private async Task<(bool HasError, string ErrorMessage, List<RelevantChunk> Chunks)> SafeSearchKnowledgeBaseWithExpansion(
    string originalQuery,
    string expandedQuery,
    string contextualQuery,
    ModelConfiguration embModel,
    int maxResults,
    string plant,
    bool useReRanking)
        {
            try
            {
                var allChunks = new List<RelevantChunk>();

                // 1. Search with contextual query (conversation-aware)
                var contextChunks = await SearchChromaDBAsync(contextualQuery, embModel, maxResults, plant);
                allChunks.AddRange(contextChunks);

                // 2. ✅ Search with expanded query (abbreviations expanded)
                if (expandedQuery != originalQuery)
                {
                    _logger.LogInformation($"🔍 Searching with expanded query: {expandedQuery}");
                    var expandedChunks = await SearchChromaDBAsync(expandedQuery, embModel, maxResults / 2, plant);
                    allChunks.AddRange(expandedChunks);
                }

                // 3. ✅ BOOST abbreviation context chunks
                foreach (var chunk in allChunks)
                {
                    if (chunk.Source.Contains("abbreviations.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        chunk.Similarity += 0.35; // Strong boost
                        _logger.LogInformation($"⬆️ Boosted abbreviation chunk: {chunk.Text.Substring(0, Math.Min(80, chunk.Text.Length))}...");
                    }
                }

                // 4. Deduplicate and re-rank
                var uniqueChunks = allChunks
                    .GroupBy(c => c.Text.Trim())
                    .Select(g => g.OrderByDescending(c => c.Similarity).First())
                    .OrderByDescending(c => c.Similarity)
                    .Take(maxResults)
                    .ToList();

                // 5. Optional: Apply re-ranking if enabled
                if (useReRanking && uniqueChunks.Count > 0)
                {
                    _logger.LogInformation("🎯 Applying semantic re-ranking...");
                    // Your existing re-ranking logic here
                }

                _logger.LogInformation($"📊 Retrieved {uniqueChunks.Count} unique chunks (from {allChunks.Count} total)");

                return (false, string.Empty, uniqueChunks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search knowledge base with expansion");
                return (true, "Knowledge base search failed", new List<RelevantChunk>());
            }
        }


        private async IAsyncEnumerable<string> GenerateStreamingResponseSafe(
    string question,
    string model,
    ConversationContext context,
    List<RelevantChunk> relevantChunks,
    bool meaiInfo,
    string plant,
    QuestionType questionType = QuestionType.NewTopic,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var prompt = meaiInfo ? await _systemPromptBuilder.BuildMeaiSystemPrompt(plant, relevantChunks, question) :
                BuildEnhancedPrompt(question, relevantChunks, context, meaiInfo, plant, questionType);



            _logger.LogInformation("Streaming response from model: {Model}", model);

            IAsyncEnumerable<string>? stream = null;

            try
            {
                stream = StreamGenerateAsync(model, prompt, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start streaming: {Message}", ex.Message);
                yield break;
            }

            if (stream == null)
            {
                yield return "__ERROR__:Stream was null";
                yield break;
            }

            // Now stream tokens - errors here will propagate up naturally
            await foreach (var token in stream.WithCancellation(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                yield return token;
            }
        }

        private async IAsyncEnumerable<string> GenerateStreamingResponseFromLLM(
    string question,
    string model,
    ConversationContext context,
    List<RelevantChunk> relevantChunks,
    bool meaiInfo,
    string plant,
    QuestionType questionType = QuestionType.NewTopic,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Build the prompt with context
            var prompt = BuildEnhancedPrompt(question, relevantChunks, context, meaiInfo, plant, questionType);

            _logger.LogInformation("Streaming response from model: {Model}", model);

            // Stream from Ollama
            await foreach (var token in StreamGenerateAsync(model, prompt, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                yield return token;
            }
        }



        public async IAsyncEnumerable<string> StreamGenerateAsync(
        string model,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var requestBody = new
            {
                model = model,
                prompt = prompt,
                stream = true,  // ← CRITICAL: Enable streaming
                options = new
                {
                    temperature = 0.7,
                    num_predict = 2000,
                    top_k = 40,
                    top_p = 0.9
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
            {
                Content = JsonContent.Create(requestBody)
            };

            _logger.LogInformation("Starting streaming generation with model: {Model}", model);

            using var response = await _ollamaClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,  // ← Don't buffer the entire response
                cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            int tokenCount = 0;
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(line))
                    continue;


                var streamResponse = JsonSerializer.Deserialize<OllamaStreamResponse>(line);

                if (streamResponse?.Response != null && !string.IsNullOrEmpty(streamResponse.Response))
                {
                    tokenCount++;
                    yield return streamResponse.Response;
                }

                // Check if streaming is complete
                if (streamResponse?.Done == true)
                {
                    _logger.LogInformation("Streaming complete. Total tokens: {Count}", tokenCount);
                    break;
                }

                _logger.LogWarning("Failed to parse Ollama stream response");
                continue;
            }
        }

        // Response model for Ollama streaming
        private class OllamaStreamResponse
        {
            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("response")]
            public string? Response { get; set; }
            [JsonPropertyName("message")]
            public MessageContent? Message { get; set; }  // ✅ For /chat endpoint

            [JsonPropertyName("done")]
            public bool Done { get; set; }

            [JsonPropertyName("context")]
            public int[]? Context { get; set; }
        }
        private class MessageContent
        {
            [JsonPropertyName("role")]
            public string? Role { get; set; }

            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
        private async Task<SessionResult> SafeInitializeSession(string sessionId)
        {
            try
            {
                var dbSession = await _conversationStorage.GetOrCreateSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(),
                    _currentUser);

                var context = _conversation.GetOrCreateConversationContext(dbSession.SessionId);

                // ✅ CRITICAL FIX: Load conversation history from database
                if (context.History.Count == 0)
                {
                    await LoadConversationHistoryFromDatabaseAsync(dbSession.SessionId, context);
                }

                return new SessionResult
                {
                    HasError = false,
                    Session = dbSession,
                    Context = context
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize session {SessionId}", sessionId);
                return new SessionResult
                {
                    HasError = true,
                    ErrorMessage = "Failed to initialize session"
                };
            }
        }
        private async Task LoadConversationHistoryFromDatabaseAsync(string sessionId, ConversationContext context)
        {
            try
            {
                _logger.LogInformation($"Loading conversation history for session {sessionId}");

                // ✅ Get conversations from database
                var conversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);

                _logger.LogInformation($"Database returned {conversations.Count} conversations for session {sessionId}");

                if (!conversations.Any())
                {
                    _logger.LogInformation($"No conversation history found in database for session {sessionId}");
                    return;
                }

                // ✅ Convert to ConversationTurn and add to context
                var turns = conversations
                    .OrderBy(c => c.CreatedAt)
                    .TakeLast(10) // Load last 10 conversations
                    .Select(c => new ConversationTurn
                    {
                        Question = c.Question,
                        Answer = c.Answer,
                        Timestamp = c.CreatedAt,
                        Sources = c.Sources ?? new List<string>()
                    })
                    .ToList();

                // ✅ Add turns to context history
                foreach (var turn in turns)
                {
                    context.History.Add(turn);
                }

                _logger.LogInformation($"✅ Loaded {turns.Count} conversation turns into memory for session {sessionId}");

                // ✅ Load named entities from historical conversations
                var allEntities = conversations
                    .SelectMany(c => c.NamedEntities ?? new List<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var entity in allEntities)
                {
                    if (!context.NamedEntities.Contains(entity, StringComparer.OrdinalIgnoreCase))
                    {
                        context.NamedEntities.Add(entity);
                    }
                }

                _logger.LogInformation($"✅ Loaded {allEntities.Count} named entities for session {sessionId}");

                // ✅ Set last topic anchor from most recent conversation
                var lastConversation = conversations
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefault();

                if (lastConversation != null && !string.IsNullOrEmpty(lastConversation.Question))
                {
                    context.LastTopicAnchor = lastConversation.Question;
                    _logger.LogInformation($"✅ Set topic anchor: {lastConversation.Question}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load conversation history for session {sessionId}");
                // Don't throw - continue with empty history
            }
        }


        private async Task<List<ConversationTurn>> LoadHistoryForSession(string sessionId)
        {
            var conversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);

            return conversations
                .OrderBy(c => c.CreatedAt)
                .TakeLast(10)
                .Select(c => new ConversationTurn
                {
                    Question = c.Question,
                    Answer = c.Answer,
                    Timestamp = c.CreatedAt,
                    Sources = c.Sources ?? new List<string>()
                })
                .ToList();
        }

        private async Task LoadConversationHistoryAsync(string sessionId, ConversationContext context)
        {
            try
            {
                _logger.LogInformation($"Loading conversation history for session {sessionId}");

                var conversations = await _conversationStorage.GetSessionConversationsAsync(sessionId);

                _logger.LogInformation($"Database returned {conversations.Count} conversations");

                if (!conversations.Any())
                {
                    _logger.LogInformation($"No conversation history found for session {sessionId}");
                    return;
                }

                // Convert database entries to conversation turns
                var turns = conversations
                    .OrderBy(c => c.CreatedAt)
                    .TakeLast(10) // Load last 10 conversations
                    .Select(c => new ConversationTurn
                    {
                        Question = c.Question,
                        Answer = c.Answer,
                        Timestamp = c.CreatedAt,
                        Sources = c.Sources ?? new List<string>()
                    })
                    .ToList();

                // Populate context with loaded history
                context.History.AddRange(turns);

                // ✅ LOAD NAMED ENTITIES from historical conversations
                var allEntities = conversations
                    .SelectMany(c => c.NamedEntities ?? new List<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var entity in allEntities)
                {
                    if (!context.NamedEntities.Contains(entity, StringComparer.OrdinalIgnoreCase))
                    {
                        context.NamedEntities.Add(entity);
                    }
                }

                // ✅ SET LAST TOPIC ANCHOR from most recent conversation
                var lastConversation = conversations.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
                if (lastConversation != null && !string.IsNullOrEmpty(lastConversation.Question))
                {
                    context.LastTopicAnchor = lastConversation.Question;
                }

                _logger.LogInformation($"Loaded {turns.Count} conversation turns for session {sessionId}");
                _logger.LogInformation($"Loaded {allEntities.Count} named entities for session {sessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load conversation history for session {sessionId}");
                // Don't throw - continue with empty history
            }
        }


        private async Task<AnswerResult> SafeCheckAppreciatedAnswer(string question)
        {
            try
            {
                var result = await CheckAppreciatedAnswerAsync(question);
                return new AnswerResult
                {
                    HasAnswer = result.HasValue,
                    Answer = result?.Answer ?? ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check appreciated answers");
                return new AnswerResult { HasAnswer = false };
            }
        }

        private async Task<AnswerResult> SafeCheckCorrections(string question)
        {
            try
            {
                var result = await CheckCorrectionsAsync(question);
                return new AnswerResult
                {
                    HasAnswer = result.Answer.Any(),
                    Answer = result?.Answer ?? ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check corrections");
                return new AnswerResult { HasAnswer = false };
            }
        }

        private async Task<string?> SafeRephraseWithLLM(string text, string model)
        {
            try
            {
                return await _helperMethods.RephraseWithLLMAsync(text, model);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rephrase with LLM");
                return null;
            }
        }

        private async Task<ModelValidationResult> SafeValidateModels(string generationModel, string embeddingModel)
        {
            try
            {
                var genModel = await _modelManager.GetModelAsync(generationModel);
                var embModel = await _modelManager.GetModelAsync(embeddingModel);

                if (embModel?.EmbeddingDimension == 0)
                    embModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);

                return new ModelValidationResult
                {
                    HasError = genModel == null || embModel == null,
                    GenerationModel = genModel,
                    EmbeddingModel = embModel
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate models");
                return new ModelValidationResult { HasError = true };
            }
        }

        private async Task<(bool HasError, string ErrorMessage, List<RelevantChunk> Chunks)> SafeSearchKnowledgeBase(
      string query,
      ModelConfiguration embModel,
      int maxResults,
      string plant,
      string originalQuestion)
        {
            try
            {
                // Expand query with abbreviations
                var expandedQuery = _abbreviationService.ExpandQuery(query);

                var searchQueries = new List<string> { query };
                if (expandedQuery != query)
                {
                    searchQueries.Add(expandedQuery);
                }

                var allChunks = new List<RelevantChunk>();

                // Search with all query variants
                foreach (var q in searchQueries)
                {
                    var chunks = await SearchChromaDBAsync(q, embModel, maxResults, plant);
                    allChunks.AddRange(chunks);
                }

                // ✅ Boost context chunks (abbreviations AND organization)
                foreach (var chunk in allChunks)
                {
                    var isContextFile = chunk.Source.Contains("abbreviations.txt", StringComparison.OrdinalIgnoreCase) ||
                                       chunk.Source.Contains("organization", StringComparison.OrdinalIgnoreCase);

                    if (isContextFile)
                    {
                        // Check if query is asking about people/roles
                        var isPeopleQuery = query.Contains("MD", StringComparison.OrdinalIgnoreCase) ||
                                           query.Contains("manager", StringComparison.OrdinalIgnoreCase) ||
                                           query.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                           query.Contains("CEO", StringComparison.OrdinalIgnoreCase) ||
                                           query.Contains("who is", StringComparison.OrdinalIgnoreCase) ||
                                           query.Contains("contact", StringComparison.OrdinalIgnoreCase);

                        if (isPeopleQuery && chunk.Source.Contains("organization"))
                        {
                            chunk.Similarity += 0.50; // Very strong boost for people queries
                            _logger.LogInformation($"⬆️ STRONG BOOST (organization): {chunk.Text.Substring(0, Math.Min(80, chunk.Text.Length))}...");
                        }
                        else
                        {
                            chunk.Similarity += 0.35; // Standard context boost
                            _logger.LogInformation($"⬆️ Boosted context: {chunk.Text.Substring(0, Math.Min(80, chunk.Text.Length))}...");
                        }
                    }
                }

                // Deduplicate and take top results
                var uniqueChunks = allChunks
                    .GroupBy(c => c.Text.Trim())
                    .Select(g => g.OrderByDescending(c => c.Similarity).First())
                    .OrderByDescending(c => c.Similarity)
                    .Take(maxResults)
                    .ToList();

                return (false, string.Empty, uniqueChunks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed in SafeSearchKnowledgeBase");
                return (true, "Search failed", new List<RelevantChunk>());
            }
        }

        public class SessionResult
        {
            public bool HasError { get; set; }
            public string ErrorMessage { get; set; } = "";
            public ConversationSession? Session { get; set; }
            public ConversationContext? Context { get; set; }
        }

        public class AnswerResult
        {
            public bool? HasAnswer { get; set; }
            public string Answer { get; set; } = "";
        }

        public class ModelValidationResult
        {
            public bool HasError { get; set; }
            public ModelConfiguration? GenerationModel { get; set; }
            public ModelConfiguration? EmbeddingModel { get; set; }
        }

        public class SearchResult
        {
            public bool HasError { get; set; }
            public string ErrorMessage { get; set; } = "";
            public List<RelevantChunk> Chunks { get; set; } = new();
        }

        // UPDATED STREAM CHUNK CLASS - matches HTML expectations
        public class StreamChunk
        {
            public string Type { get; set; } = "";
            public string? Content { get; set; }
            public string? Source { get; set; }
            public double? Similarity { get; set; }
            public List<string>? Sources { get; set; }
            public long? ProcessingTimeMs { get; set; }
            public string? TextPreview { get; set; }  // Added for HTML compatibility
        }

        public class ResponseGenerationResult
        {
            public bool HasError { get; set; }
            public string ErrorMessage { get; set; } = "";
            public IAsyncEnumerable<string>? TokenStream { get; set; }
        }

        public class SimpleResponseResult
        {
            public bool HasError { get; set; }
            public string ErrorMessage { get; set; } = "";
            public string Response { get; set; } = "";
        }

        private async IAsyncEnumerable<StreamChunk> StreamTextResponse(
    string text,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            var sentences = text.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var sentence in sentences)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                var words = sentence.Split(' ');
                var currentChunk = "";

                for (int i = 0; i < words.Length; i++)
                {
                    currentChunk += words[i] + " ";

                    // Send chunk every 6-8 words
                    if ((i + 1) % 7 == 0 || i == words.Length - 1)
                    {
                        yield return new StreamChunk { Type = "response", Content = currentChunk };
                        currentChunk = "";

                        if (i < words.Length - 1)
                            await Task.Delay(Random.Shared.Next(50, 100), cancellationToken);
                    }
                }

                if (!sentence.EndsWith('.') && !sentence.EndsWith('!') && !sentence.EndsWith('?'))
                {
                    yield return new StreamChunk { Type = "response", Content = ". " };
                }

                await Task.Delay(Random.Shared.Next(80, 150), cancellationToken);
            }
        }





        private async IAsyncEnumerable<StreamChunk> ProcessNonMeaiQueryStream(
    string question,
    string? sessionId,
    string generationModel,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamChunk { Type = "status", Content = "Processing general query..." };

            // Get session
            var sessionResult = await SafeInitializeSession(sessionId);
            if (sessionResult.HasError)
            {
                yield return new StreamChunk { Type = "error", Content = sessionResult.ErrorMessage };
                yield break;
            }

            var context = sessionResult.Context!;

            // Get model
            var genModel = await _modelManager.GetModelAsync(generationModel ?? _config.DefaultGenerationModel);
            if (genModel == null)
            {
                yield return new StreamChunk { Type = "error", Content = "Generation model not available" };
                yield break;
            }

            // ✅ Stream directly from Ollama - NO intermediate buffering
            var fullResponse = new StringBuilder();

            await foreach (var token in GenerateNonMeaiStreamAsync(
                question,
                genModel.Name!,
                context.History,
                cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                fullResponse.Append(token);

                // ✅ Yield each token immediately
                yield return new StreamChunk
                {
                    Type = "response",  // ✅ This is what your HTML expects
                    Content = token
                };
            }

            // Save to database (optional - after streaming completes)
            try
            {
                if (fullResponse.Length > 0)
                {
                    await SaveNonMeaiConversationToDatabase(
                        sessionResult.Session!.SessionId,
                        question,
                        fullResponse.ToString(),
                        genModel,
                        0.7,
                        0,
                        false,
                        "");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save non-MEAI conversation");
            }

            yield return new StreamChunk { Type = "complete", Content = "", ProcessingTimeMs = 0 };
        }

        private async IAsyncEnumerable<string> GenerateNonMeaiStreamAsync(
    string question,
    string modelName,
    List<ConversationTurn> history,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Build messages
            var messages = new List<object>
    {
        new
        {
            role = "system",
            content = @"You are an intelligent and helpful AI assistant with expertise across multiple domains.

**Core Principles:**
- Provide accurate, well-researched, and detailed responses
- Be conversational, professional, and friendly
- Use clear structure with appropriate formatting
- Cite reasoning when making recommendations
- Admit uncertainty when appropriate rather than guessing

**Response Guidelines:**
1. For factual questions: Provide direct, accurate answers with relevant context
2. For how-to questions: Give step-by-step instructions with clear explanations
3. For recommendations: Explain pros/cons and provide multiple options when relevant
4. For complex topics: Break down concepts into digestible parts
5. For opinion-based questions: Present balanced perspectives

**Formatting:**
- Use clear paragraphs for readability
- Add bullet points for lists or multiple items
- Include examples when they clarify concepts
- Keep responses concise but complete

Be helpful, informative, and engaging in your communication."
        }
    };

            // Add recent history
            foreach (var turn in history.TakeLast(6))
            {
                messages.Add(new { role = "user", content = turn.Question });
                messages.Add(new { role = "assistant", content = turn.Answer });
            }

            // Add current question
            messages.Add(new { role = "user", content = question });

            var requestData = new
            {
                model = modelName,
                messages,
                temperature = 0.7,
                stream = true,  // ✅ CRITICAL: Enable streaming
                options = new Dictionary<string, object>
                {
                    ["num_ctx"] = 4096,
                    ["top_p"] = 0.9
                }
            };

            _logger.LogInformation($"Streaming non-MEAI response with model: {modelName}");

            // Create streaming request
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
            {
                Content = JsonContent.Create(requestData)
            };

            HttpResponseMessage? response = null;

            response = await _ollamaClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Ollama request failed: {response.StatusCode}");
                yield return "I apologize, but I'm having trouble generating a response right now.";
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;


                var streamResponse = JsonSerializer.Deserialize<OllamaStreamResponse>(line);

                // ✅ Handle chat format response
                if (streamResponse?.Message?.Content != null &&
                    !string.IsNullOrEmpty(streamResponse.Message.Content))
                {
                    yield return streamResponse.Message.Content;
                }

                if (streamResponse?.Done == true)
                {
                    _logger.LogInformation("Non-MEAI streaming complete");
                    break;
                }
            }
        }

        private string BuildEnhancedPrompt(
     string question,
     List<RelevantChunk> relevantChunks,
     ConversationContext context,
     bool meaiInfo,
     string plant,
     QuestionType questionType = QuestionType.NewTopic) // ✅ Add parameter
        {
            var promptBuilder = new StringBuilder();

            // ✅ ADD CONTEXT-AWARE INSTRUCTIONS based on question type
            if (questionType == QuestionType.FollowUp && context.History.Any())
            {
                promptBuilder.AppendLine("=== FOLLOW-UP CONVERSATION ===");
                promptBuilder.AppendLine("This is a follow-up question. Reference and build upon the previous discussion.");
                promptBuilder.AppendLine();

                // More history for follow-ups
                promptBuilder.AppendLine("Previous conversation:");
                foreach (var entry in context.History.TakeLast(5))
                {
                    promptBuilder.AppendLine($"Q: {entry.Question}");
                    promptBuilder.AppendLine($"A: {entry.Answer}");
                }
                promptBuilder.AppendLine();

                // Add topic anchor
                if (!string.IsNullOrEmpty(context.LastTopicAnchor))
                {
                    promptBuilder.AppendLine($"Main topic: {context.LastTopicAnchor}");
                    promptBuilder.AppendLine();
                }

                // Include named entities
                if (context.NamedEntities.Any())
                {
                    promptBuilder.AppendLine($"Key entities: {string.Join(", ", context.NamedEntities.Take(10))}");
                    promptBuilder.AppendLine();
                }
            }
            else if (questionType == QuestionType.RelatedTopic && context.History.Any())
            {
                promptBuilder.AppendLine("=== RELATED CONVERSATION ===");
                promptBuilder.AppendLine("This question is related to our previous discussion but explores a different angle.");
                promptBuilder.AppendLine();

                // Limited history for related topics
                promptBuilder.AppendLine("Recent conversation:");
                foreach (var entry in context.History.TakeLast(2))
                {
                    promptBuilder.AppendLine($"Q: {entry.Question}");
                    promptBuilder.AppendLine($"A: {entry.Answer}");
                }
                promptBuilder.AppendLine();
            }
            else if (questionType == QuestionType.NewTopic)
            {
                promptBuilder.AppendLine("=== NEW CONVERSATION ===");
                promptBuilder.AppendLine("This is a new topic. Answer independently.");
                promptBuilder.AppendLine();
                // NO history for new topics
            }
            else if (context.History.Any())
            {
                // Default: show limited history
                promptBuilder.AppendLine("Previous conversation:");
                foreach (var entry in context.History.TakeLast(3))
                {
                    promptBuilder.AppendLine($"Q: {entry.Question}");
                    promptBuilder.AppendLine($"A: {entry.Answer}");
                }
                promptBuilder.AppendLine();
            }

            // Add retrieved context (unchanged)
            if (relevantChunks.Any())
            {
                promptBuilder.AppendLine("Relevant information from company documents:");
                foreach (var chunk in relevantChunks.Take(5))
                {
                    promptBuilder.AppendLine($"Source: {chunk.Source}");
                    promptBuilder.AppendLine(chunk.Text);
                    promptBuilder.AppendLine();
                }
            }

            // Add the actual question
            if (meaiInfo)
            {
                promptBuilder.AppendLine($"Based on the above information from {plant} plant, please answer:");
            }
            else
            {
                promptBuilder.AppendLine("Please answer the following question:");
            }

            promptBuilder.AppendLine(question);

            // Enhanced instructions based on question type
            if (questionType == QuestionType.FollowUp)
            {
                promptBuilder.AppendLine("\nProvide a clear answer that builds on our previous discussion. Reference relevant parts when appropriate.");
            }
            else if (questionType == QuestionType.RelatedTopic)
            {
                promptBuilder.AppendLine("\nProvide a clear answer considering the related previous discussion but treat this as a distinct aspect.");
            }
            else
            {
                promptBuilder.AppendLine("\nProvide a clear, concise answer based on the provided context.");
            }

            return promptBuilder.ToString();
        }





        // Helper method to create the streaming request (handles exceptions)
        private async Task<(bool Success, HttpResponseMessage? Response, string? ErrorMessage)> CreateStreamingRequest(
            string prompt,
            ModelConfiguration model,
            CancellationToken cancellationToken)
        {
            try
            {
                var messages = new List<object>
        {
            new { role = "system", content = "You are a helpful AI assistant." },
            new { role = "user", content = prompt }
        };

                var requestData = new
                {
                    model = model.Name,
                    messages,
                    stream = true,
                    temperature = 0.1,
                    options = new Dictionary<string, object>
            {
                { "num_ctx", 4000 },
                { "num_predict", 2000 },
                { "top_p", 0.9 }
            }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
                {
                    Content = JsonContent.Create(requestData)
                };

                var response = await _ollamaClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Streaming request failed: {response.StatusCode}");
                    return (false, null, "Failed to connect to AI service.");
                }

                return (true, response, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create streaming request");
                return (false, null, "Failed to create streaming request.");
            }
        }

        // Helper method to process streaming response (no try-catch around yields)
        private async IAsyncEnumerable<string> ProcessStreamingResponse(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using (response)
            using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var reader = new StreamReader(stream))
            {
                var currentToken = new StringBuilder();

                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Handle streaming format
                    if (line.StartsWith("data: "))
                    {
                        var jsonData = line.Substring(6);
                        if (jsonData == "[DONE]") break;

                        var tokenResult = ParseStreamToken(jsonData);
                        if (!string.IsNullOrEmpty(tokenResult))
                        {
                            currentToken.Append(tokenResult);

                            // Check if we have complete words
                            var currentText = currentToken.ToString();
                            var lastSpaceIndex = currentText.LastIndexOf(' ');

                            if (lastSpaceIndex > 0)
                            {
                                var wordsToYield = currentText.Substring(0, lastSpaceIndex);
                                yield return wordsToYield;

                                currentToken.Clear();
                                currentToken.Append(currentText.Substring(lastSpaceIndex + 1));
                            }
                        }
                    }
                }

                // Yield any remaining content
                if (currentToken.Length > 0)
                {
                    yield return currentToken.ToString();
                }
            }
        }

        // Helper method to parse individual tokens (handles JSON parsing safely)
        private string ParseStreamToken(string jsonData)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                if (doc.RootElement.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? "";
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse streaming JSON");
            }

            return "";
        }

        public async Task<PolicyProcessingDiagnostics> DiagnosePolicyProcessingAsync(string plant)
        {
            var diagnostics = new PolicyProcessingDiagnostics
            {
                Plant = plant,
                Timestamp = DateTime.Now
            };

            try
            {
                // Check policy files
                var policyFiles = GetPolicyFiles(plant);
                diagnostics.TotalPolicyFiles = policyFiles.Count;
                diagnostics.PolicyFiles = policyFiles.Select(f => new PolicyFileInfo
                {
                    FileName = Path.GetFileName(f),
                    FilePath = f,
                    FileSize = new FileInfo(f).Length,
                    LastModified = new FileInfo(f).LastWriteTime
                }).ToList();

                // Check models
                var models = await _modelManager.DiscoverAvailableModelsAsync();
                var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();
                diagnostics.EmbeddingModels = embeddingModels.Select(m => m.Name).ToList();

                // Check each model's collection
                foreach (var model in embeddingModels)
                {
                    try
                    {
                        var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);
                        var count = await GetCollectionCountAsync(collectionId);

                        diagnostics.ModelCollections.Add(new ModelCollectionInfo
                        {
                            ModelName = model.Name,
                            CollectionId = collectionId,
                            EmbeddingCount = count
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to check collection for model: {model.Name}");
                    }
                }

                // Check cache status
                diagnostics.CachedFiles = processedFilesCache.Count;
                diagnostics.CachedEmbeddings = _optimizedEmbeddingCache.Count;

                _logger.LogInformation($"📊 Policy processing diagnostics completed for {plant}");
            }
            catch (Exception ex)
            {
                diagnostics.Error = ex.Message;
                _logger.LogError(ex, "Failed to run diagnostics");
            }

            return diagnostics;
        }

        // Add these methods to DynamicRagService class

        // Clear cache for a specific file
        public Task ClearFileCacheAsync(string filePath)
        {
            try
            {
                var keysToRemove = processedFilesCache.Keys
                    .Where(k => k.StartsWith(filePath))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    processedFilesCache.TryRemove(key, out _);
                    _logger.LogInformation($"Removed cache entry: {key}");
                }

                _logger.LogInformation($"Cleared {keysToRemove.Count} cache entries for file: {filePath}");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to clear cache for file: {filePath}");
                throw;
            }
        }

        // Process a single file (useful for debugging/reprocessing)
        public async Task ProcessSingleFileAsync(string filePath, string plant)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"File not found: {filePath}");
                }

                _logger.LogInformation($"🔄 Processing single file: {filePath} for plant: {plant}");

                var models = await _modelManager.DiscoverAvailableModelsAsync();
                var embeddingModels = models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();

                if (!embeddingModels.Any())
                {
                    throw new InvalidOperationException("No embedding models available");
                }

                foreach (var model in embeddingModels)
                {
                    _logger.LogInformation($"Processing with model: {model.Name}");

                    var collectionId = await _collectionManager.GetOrCreateCollectionAsync(model);
                    await ProcessFileForModelAsync(filePath, model, collectionId, plant);
                }

                _logger.LogInformation($"✅ Completed processing file: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process single file: {filePath}");
                throw;
            }
        }

        // Clear all caches (nuclear option for debugging)
        public Task ClearAllCachesAsync()
        {
            try
            {
                var filesCacheCount = processedFilesCache.Count;
                var embeddingsCacheCount = _optimizedEmbeddingCache.Count;

                processedFilesCache.Clear();
                _optimizedEmbeddingCache.Clear();

                _logger.LogInformation($"🧹 Cleared all caches: {filesCacheCount} files, {embeddingsCacheCount} embeddings");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear caches");
                throw;
            }
        }

        // Verify embedding model is working
        public async Task<EmbeddingTestResult> TestEmbeddingModelAsync(string modelName)
        {
            var result = new EmbeddingTestResult
            {
                ModelName = modelName,
                Timestamp = DateTime.Now
            };

            try
            {
                var model = await _modelManager.GetModelAsync(modelName);

                if (model == null)
                {
                    result.Success = false;
                    result.Error = $"Model '{modelName}' not found";
                    return result;
                }

                result.ModelFound = true;
                result.ExpectedDimension = model.EmbeddingDimension;

                // Test with sample text
                var testTexts = new[]
                {
            "This is a test sentence for embedding generation.",
            "HR policy regarding leave management.",
            "Section 1: Introduction and Purpose"
        };

                var embeddings = new List<int>();

                foreach (var text in testTexts)
                {
                    var embedding = await GetEmbeddingAsync(text, model);

                    if (embedding == null || embedding.Count == 0)
                    {
                        result.Success = false;
                        result.Error = $"Failed to generate embedding for text: '{text.Substring(0, Math.Min(50, text.Length))}'";
                        return result;
                    }

                    embeddings.Add(embedding.Count);
                }

                result.Success = true;
                result.ActualDimensions = embeddings;
                result.Message = $"Successfully generated {embeddings.Count} embeddings with dimensions: {string.Join(", ", embeddings)}";

                _logger.LogInformation($"✅ Embedding model test passed: {modelName}");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                _logger.LogError(ex, $"Embedding model test failed: {modelName}");
            }

            return result;
        }

        // Supporting class
        public class EmbeddingTestResult
        {
            public string ModelName { get; set; } = "";
            public DateTime Timestamp { get; set; }
            public bool Success { get; set; }
            public bool ModelFound { get; set; }
            public int ExpectedDimension { get; set; }
            public List<int> ActualDimensions { get; set; } = new();
            public string? Error { get; set; }
            public string? Message { get; set; }
        }

        public class PolicyProcessingDiagnostics
        {
            public string Plant { get; set; } = "";
            public DateTime Timestamp { get; set; }
            public int TotalPolicyFiles { get; set; }
            public List<PolicyFileInfo> PolicyFiles { get; set; } = new();
            public List<string> EmbeddingModels { get; set; } = new();
            public List<ModelCollectionInfo> ModelCollections { get; set; } = new();
            public int CachedFiles { get; set; }
            public int CachedEmbeddings { get; set; }
            public string? Error { get; set; }
        }

        public class PolicyFileInfo
        {
            public string FileName { get; set; } = "";
            public string FilePath { get; set; } = "";
            public long FileSize { get; set; }
            public DateTime LastModified { get; set; }
        }

        public class ModelCollectionInfo
        {
            public string ModelName { get; set; } = "";
            public string CollectionId { get; set; } = "";
            public int EmbeddingCount { get; set; }
        }
        public enum QuestionType
        {
            NewTopic,        // Completely new conversation
            FollowUp,        // Direct follow-up (e.g., "what about...", "tell me more")
            RelatedTopic,     // Related but exploring different angle
            LooselyRelated
        }


    }

}
