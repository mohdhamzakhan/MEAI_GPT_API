// Services/DynamicRagService.cs
using DocumentFormat.OpenXml.InkML;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
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
        private readonly Conversation _conversation;
        private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();
        private readonly ConcurrentBag<CorrectionEntry> _correctionsCache = new();
        private readonly object _lockObject = new();
        private readonly string _currentUser = "system";
        private bool _isInitialized = false;
        private readonly ConcurrentBag<(string Question, string Answer, List<RelevantChunk> Chunks)> _appreciatedTurns = new();
        private readonly PlantSettings _plants;
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
            _conversation = conversation;
            _plants = plants.Value;

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
                foreach (var plant in _plants.Plants.Keys)
                {
                    _logger.LogInformation($"🌱 Processing documents for plant: {plant}");
                    await ProcessDocumentsForAllModelsAsync(embeddingModels, plant);
                }

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

            if (Directory.Exists(_chromaOptions.PolicyFolder))
            {
                policyFiles.AddRange(Directory.GetFiles(Path.Combine(_chromaOptions.PolicyFolder,plant), "*.*", SearchOption.AllDirectories)
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

        private async Task ProcessChunkBatchForModelAsync(
            List<(string Text, string SourceFile)> chunks,
            ModelConfiguration model,
            string collectionId,
            DateTime lastModified,
            string plant)
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

                        var metadata = CreateChunkMetadata(sourceFile, lastModified, model.Name, cleanedText, plant);
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
            var safeOptions = new Dictionary<string, object>();
            foreach (var kvp in model.ModelOptions)
            {
                object value = kvp.Value;

                if (kvp.Key == "num_ctx" && value is String d)
                    value = int.Parse(d);
                else if (kvp.Key == "top_p" && value is string e)
                    value = float.Parse(e);
                else if (kvp.Key == "temperature" && value is string f)
                    value = float.Parse(f);

                safeOptions[kvp.Key] = value;
            }

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var request = new
                    {
                        model = model.Name, // Dynamic model name!
                        prompt = text,
                        options = safeOptions // Dynamic model options!
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
                if (embModel.EmbeddingDimension == 0)
                    embModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                //sessionId = "69e7386a-d108-4304-be2a-c43402dbbb94";
                var context = _conversation.GetOrCreateConversationContext(sessionId);
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
                    await UpdateConversationHistory(context, question, correction.Answer, new List<RelevantChunk>());

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

                if (IsTopicChanged(question, context))
                {
                    _logger.LogInformation($"Topic changed for session {context.SessionId}, clearing context");
                    ClearContext(context);
                }

                var contextualQuery = BuildContextualQuery(question, context.History);

                var similarAppreciated = _appreciatedTurns
    .FirstOrDefault(a => CalculateTextSimilarity(a.Question, question) > 0.75);

                if (similarAppreciated != default)
                {
                    _logger.LogInformation("💡 Reusing appreciated answer for similar question.");
                    await UpdateConversationHistory(context, question, similarAppreciated.Answer, similarAppreciated.Chunks);

                    return new QueryResponse
                    {
                        Answer = similarAppreciated.Answer,
                        IsFromCorrection = false,
                        Sources = similarAppreciated.Chunks.Select(c => c.Source).Distinct().ToList(),
                        Confidence = 1.0,
                        ProcessingTimeMs = 0,
                        RelevantChunks = similarAppreciated.Chunks,
                        SessionId = context.SessionId,
                        ModelsUsed = new Dictionary<string, string>
                        {
                            ["generation"] = generationModel,
                            ["embedding"] = embeddingModel
                        }
                    };
                }


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
                    context,
                    ismeai: meaiInfo
                );

                var answerEmbedding = await GetEmbeddingAsync(answer, embModel);

                // Rank chunks by similarity to answer
                var scoredChunks = new List<(RelevantChunk Chunk, double Similarity)>();
                foreach (var chunk in relevantChunks.Take(10))
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
        public Task MarkAppreciatedAsync(string sessionId, string question)
        {
            if (_sessionContexts.TryGetValue(sessionId, out var context))
            {
                var turn = context.History.LastOrDefault(t => t.Question.Equals(question, StringComparison.OrdinalIgnoreCase));
                if (turn != null)
                {
                    var chunks = ConvertToRelevantChunks(context.RelevantChunks ?? new List<EmbeddingData>());
                    _appreciatedTurns.Add((turn.Question, turn.Answer, new List<RelevantChunk>(chunks)));
                    _logger.LogInformation($"⭐ Appreciated turn saved: {question}");
                }
            }
            return Task.CompletedTask;
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
        private async Task<List<RelevantChunk>> SearchChromaDBAsync(string query, ModelConfiguration embeddingModel, int maxResults)
        {
            try
            {
                var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);
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

        private bool IsTopicChanged(string question, ConversationContext context)
        {
            if (string.IsNullOrWhiteSpace(question)) return true;

            var lowerQuestion = question.ToLowerInvariant();

            // Broader follow-up phrase detection (not only at start)
            string[] contextualPhrases = new[]
{
    // English follow-up cues
    "what about",
    "how about",
    "what is his",
    "what is her",
    "how is she",
    "how is he",
    "what will",
    "what if",
    "what else",
    "can she",
    "can he",
    "do they",
    "did she",
    "did he",
    "does it",
    "does he",
    "does she",
    "is that true",
    "tell me more",
    "more info",
    "additional info",
    "continue",
    "next",
    "go on",
    "expand on that",
    "in addition",
    "related to",
    "following that",
    "based on that",
    "regarding that",
    "same as",
    "just like",
    "similar to",
    "and then",
    "after that",
    "he",
    "she",
    "it",
    "them",
    "they",
    "their",
    "that",
    "his",
    "her",
    "this person",
    "that person",
    "the individual",
    "the person you mentioned",
    "okay",
    "got it",
    "alright",
    "yes and",
    "oh",
    "hmm",
    "also",
    "too",
    "by the way",
    "btw",
    "on that topic",
    "as you said",
    "like you said",
    "you mentioned",
    "back to that",
    "about that",
    "is that still true",
    "with that in mind",

    // Hinglish / Hindi-English expressions
    "phir kya",
    "fir kya",
    "uske baad",
    "aur batao",
    "aur kya",
    "baki kya",
    "aur info do",
    "kya usne",
    "kya uska",
    "kya uski",
    "toh kya",
    "fir kya hua",
    "ab kya",
    "ab uska",
    "wo kya karega",
    "wo kya karegi",
    "usse kya",
    "usse related",
    "iski info",
    "uski info",
    "wo bhi",
    "iske alawa",
    "yeh kya hai",
    "matlab kya",
    "next batao",
    "aur details",
    "ye kisne bola",
    "phir kya hua",
    "us topic pe",
    "wahi wala",
    "uske bare me",
    "pehle jo bola tha",
    "jaise aapne bola",
    "bol rahe the",
    "jis baat ki",
    "fir batao",
    "agar aisa ho",      
    "agar main",          
    "agar mein",          
    "agar usne",          
    "agar uska",          
    "agar uski",          
    "agar mujhe",         
    "agar kisi ne",       
    "agar me",            
    "agar hum",           
    "agar vo",            
    "agar mai",           
    "agar kuch",          
    "agar kal",           
    "agar mujhe chhutti", 
    "agar leave chahiye", 
    "agar me chala jau",  
    "agar combine karu", 
    "agar CL lo",         
    "agar sick leave",
    "agar SL lo",
    "agar allowed hai",   
};



            // 1. If it contains follow-up terms, assume same topic
            // ✅ Check fuzzy match on contextual phrases
            if (IsFollowUpPhraseFuzzy(question, contextualPhrases))
            {
                return false;
            }

            // 2. Use anchor if present and valid
            var anchor = context.LastTopicAnchor ?? "";
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                double anchorSim = CalculateTextSimilarity(question, anchor);
                if (anchorSim >= 0.4) return false;
            }

            // 3. Fallback: check similarity with last question
            if (context.History.Count > 0)
            {
                var lastQuestion = context.History.Last().Question;
                double lastSim = CalculateTextSimilarity(question, lastQuestion);
                if (lastSim >= 0.4) return false;
            }

            // 4. Default to "changed"
            return true;
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

                // If it's a new topic, update anchor
                if (IsTopicChanged(question, context))
                {
                    context.LastTopicAnchor = question; // 🆕 update anchor
                }

                context.LastAccessed = DateTime.Now;
                
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


        private async Task<CorrectionEntry?> CheckCorrectionsAsync(string question)
        {
            try
            {
                var embeddingModel = await _modelManager.GetModelAsync(_config.DefaultEmbeddingModel!);
                var inputEmbedding = await GetEmbeddingAsync(question, embeddingModel);

                lock (_lockObject)
                {
                    return _correctionsCache
                        .Select(c => new
                        {
                            Entry = c,
                            Similarity = CosineSimilarity(inputEmbedding, c.Embedding)
                        })
                        .OrderByDescending(x => x.Similarity)
                        .FirstOrDefault(x => x.Similarity >= 0.8)  // You can tune this threshold
                        ?.Entry;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check semantic correction match");
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

        public Task<QueryResponse> ProcessQueryAsync(string question, string generationModel, int maxResults = 10, bool meaiInfo = true, string? sessionId = null, bool useReRanking = true, string? embeddingModel = null)
        {
            throw new NotImplementedException();
        }
    }
}
