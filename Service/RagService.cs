using MEAI_GPT_API.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using static MEAI_GPT_API.Models.Conversation;

public class RagService
{
    private static readonly HttpClient httpClient = new()
    {
        BaseAddress = new Uri("http://192.168.129.203:11434"),
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static readonly HttpClient chromaClient = new()
    {
        BaseAddress = new Uri("http://192.168.129.203:7654"), // ChromaDB default port
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly string POLICIES_FOLDER = "D:\\Code\\MEAIGPT\\MEAIRAG\\policies";
    private static readonly string[] SUPPORTED_EXTENSIONS = { ".txt", ".md", ".pdf", ".docx" };
    private static readonly string CHROMA_COLLECTION_NAME = "meai_policies"; // For creation
    private string CHROMA_COLLECTION_ID = "123e4567-c89b-12d3-a456-426614174000"; // For queries/add, etc.
    private readonly string tenant = "default_tenant";
    private readonly string database = "default_database";
    private static readonly string CORRECTIONS_COLLECTION_NAME = "meai_corrections";
private static readonly string CORRECTIONS_COLLECTION_ID = "123e4567-e89b-12d3-a456-426614174000"; // Replace with your real UUIDv4

    private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();
    private readonly ConcurrentBag<CorrectionEntry> _correctionsCache = new();
    private readonly object _lockObject = new();
    private int _nextCorrectionId = 1;

    public class ModelConfiguration
    {
        public string EmbeddingModel { get; set; } = "nomic-embed-text:latest";
        public string GenerationModel { get; set; } = "llama3.1:8b";
        public string ReRankModel { get; set; } = "linux6200/bge-reranker-v2-m3";
        public bool UseReRanking { get; set; } = true;
        public bool UseCrossEncoder { get; set; } = false;
    }

    public async Task InitializeAsync()
    {
        Console.WriteLine("Initializing RAG system with ChromaDB...");

        // Initialize ChromaDB
        await InitializeChromaDBAsync();

        // Check available embedding models
        var embeddingModel = GetBestEmbeddingModel();
        var isAvailable = await IsModelAvailable(embeddingModel);

        if (!isAvailable)
        {
            Console.WriteLine($"⚠️ Warning: Preferred embedding model '{embeddingModel}' not available");
            Console.WriteLine("Consider pulling an embedding model: 'ollama pull nomic-embed-text:latest'");
        }

        await LoadOrGenerateEmbeddings(embeddingModel);
        await LoadCorrectionsAsync();

        var count = await GetCollectionCountAsync();
        Console.WriteLine($"Ready! ChromaDB collection has {count} embeddings.");
    }

    private async Task InitializeChromaDBAsync()
    {
        try
        {
            // Check if ChromaDB is running
            var healthResponse = await chromaClient.GetAsync("/api/v2/heartbeat");
            if (!healthResponse.IsSuccessStatusCode)
            {
                throw new Exception("ChromaDB is not running. Start ChromaDB with: chroma run --host localhost --port 8000");
            }

            // Create or get collection
            var collectionData = new
            {
                name = CHROMA_COLLECTION_NAME,
                metadata = new { description = "MEAI HR Policy documents" },
            };

            var response = await chromaClient.PostAsJsonAsync(
     $"/api/v2/tenants/{tenant}/databases/{database}/collections", collectionData);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                CHROMA_COLLECTION_ID =  await GetCollectionIdByNameAsync(CHROMA_COLLECTION_NAME);

                Console.WriteLine($"ChromaDB collection creation response: {error}");
            }

            Console.WriteLine("✅ ChromaDB initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ChromaDB initialization failed: {ex.Message}");
            Console.WriteLine("Make sure ChromaDB is running: pip install chromadb && chroma run --host localhost --port 8000");
            throw;
        }
    }

    public async Task<QueryResponse> ProcessQueryAsync(
       string question,
       string generationModel,
       int maxResults = 10,
       bool meai_info = true,
       string? sessionId = null,
       bool useReRanking = true,
       string? embeddingModel = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var conversationContext = GetOrCreateConversationContext(sessionId);

        if (IsHistoryClearRequest(question))
        {
            conversationContext.History.Clear();
            conversationContext.RelevantChunks.Clear();
            conversationContext.LastAccessed = DateTime.Now;

            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessionContexts.TryRemove(sessionId, out _);
            }

            stopwatch.Stop();
            return new QueryResponse
            {
                Answer = "✅ Conversation history cleared. How can I assist you today?",
                IsFromCorrection = false,
                Sources = new List<string>(),
                Confidence = 1.0,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                RelevantChunks = new List<RelevantChunk>()
            };
        }

        if (string.IsNullOrEmpty(embeddingModel) || !SupportsEmbeddings(embeddingModel))
        {
            embeddingModel = GetBestEmbeddingModel();
        }

        if (IsTopicChanged(question, conversationContext.History))
        {
            Console.WriteLine("⚠️ Topic change detected. Clearing previous context.");
            conversationContext.History.Clear();
            conversationContext.RelevantChunks.Clear();
        }

        var contextualQuery = BuildContextualQuery(question, conversationContext.History);
        List<RelevantChunk> relevantChunks = new();

        if (meai_info)
        {
            try
            {
                var correction = await CheckCorrections(question);
                if (correction != null)
                {
                    stopwatch.Stop();
                    return new QueryResponse
                    {
                        Answer = correction.Answer,
                        IsFromCorrection = true,
                        Sources = new List<string> { "Corrections Database" },
                        Confidence = 1.0,
                        ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                        RelevantChunks = new List<RelevantChunk>()
                    };
                }

                if (conversationContext.RelevantChunks.Count > 0 && IsFollowUpQuestion(question, conversationContext.History))
                {
                    relevantChunks = conversationContext.RelevantChunks.Select(e => new RelevantChunk
                    {
                        Text = e.Text,
                        Source = e.SourceFile,
                        Similarity = e.Similarity
                    }).ToList();
                    Console.WriteLine($"✅ Reusing context for follow-up question in session {sessionId}");
                }
                else
                {
                    relevantChunks = await SearchChromaDBAsync(contextualQuery, embeddingModel, maxResults);
                    conversationContext.RelevantChunks = relevantChunks.Select(r => new EmbeddingData(
    Text: r.Text,
    Vector: new List<float>(),
    SourceFile: r.Source,
    LastModified: DateTime.UtcNow,
    model: "model-name")
                    {
                        Similarity = r.Similarity
                    }).ToList();
                }

                if (useReRanking && relevantChunks.Count > maxResults)
                {
                    relevantChunks = await ReRankChunks(contextualQuery, relevantChunks, maxResults, generationModel);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in search process: {ex.Message}");
                relevantChunks = new List<RelevantChunk>();
            }
        }

        var answer = await GenerateChatResponse(
            question,
            generationModel,
            conversationContext.History,
            relevantChunks,
            temperature: 0.2
        );

        conversationContext.History.Add(new ConversationTurn
        {
            Question = question,
            Answer = answer,
            Timestamp = DateTime.Now,
            Sources = relevantChunks.Select(c => c.Source).Distinct().ToList()
        });

        if (conversationContext.History.Count > 10)
        {
            conversationContext.History = conversationContext.History.TakeLast(5).ToList();
        }

        conversationContext.LastAccessed = DateTime.Now;
        stopwatch.Stop();

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

            var response = await chromaClient.PostAsJsonAsync($"/api/v2/tenants/{tenant}/databases/{database}/collections/{CHROMA_COLLECTION_ID}/query", searchData);
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

    private async Task LoadOrGenerateEmbeddings(string model = "nomic-embed-text:latest")
    {
        var policyFiles = GetAllPolicyFiles();
        var newDocumentsCount = 0;

        Console.WriteLine($"Processing {policyFiles.Count} policy files with model: {model}");

        foreach (var filePath in policyFiles)
        {
            var fileInfo = new FileInfo(filePath);
            var lastModified = fileInfo.LastWriteTime;

            // Check if file already exists in ChromaDB
            if (await IsFileInChromaDBAsync(filePath, lastModified))
            {
                Console.WriteLine($"✅ Skipping {Path.GetFileName(filePath)} - already in ChromaDB");
                continue;
            }

            Console.WriteLine($"🔄 Processing {Path.GetFileName(filePath)}...");

            var content = await ReadFileContent(filePath);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var chunks = ChunkText(content, filePath, maxTokens: 2048);
            Console.WriteLine($"📝 Processing {chunks.Count} chunks from {Path.GetFileName(filePath)}");

            // Process chunks in batches
            var batchSize = 5; // Adjust based on your system capacity
            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                await ProcessChunkBatch(batch, model, filePath, lastModified);
                newDocumentsCount += batch.Count;

                // Small delay between batches
                if (i + batchSize < chunks.Count)
                {
                    await Task.Delay(1000);
                }
            }
        }

        Console.WriteLine($"✅ Embedding processing complete! Added {newDocumentsCount} new chunks to ChromaDB");
    }

    public class ChunkMetadata
    {
        public string SourceFile { get; set; }
        public string LastModified { get; set; }
        public string Model { get; set; }
        public int ChunkSize { get; set; }
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

            var response = await chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{tenant}/databases/{database}/collections/{CHROMA_COLLECTION_ID}/add",
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
    private async Task<bool> IsFileInChromaDBAsync(string filePath, DateTime lastModified)
    {
        try
        {
            var queryData = new
            {
                where = new
                {
                    source_file = filePath,
                    last_modified = lastModified.ToString("O")
                },
                limit = 1
            };

            var response = await chromaClient.PostAsJsonAsync($"/api/v2/collections/{CHROMA_COLLECTION_ID}/get", queryData);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(result);

                if (doc.RootElement.TryGetProperty("ids", out var idsArray))
                {
                    return idsArray.GetArrayLength() > 0;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> GetCollectionIdByNameAsync(string collectionName)
    {
        var response = await chromaClient.GetAsync($"/api/v2/tenants/{tenant}/databases/{database}/collections");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(result);
        foreach (var collection in doc.RootElement.EnumerateArray())
        {
            if (collection.GetProperty("name").GetString() == collectionName)
                return collection.GetProperty("id").GetString();
        }
        return null;
    }

    private async Task<int> GetCollectionCountAsync()
    {
        try
        {
            var response = await chromaClient.GetAsync($"/api/v2/tenants/{tenant}/databases/{database}/collections/{CHROMA_COLLECTION_ID}/count");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(result);
                return doc.RootElement.GetInt32();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get collection count: {ex.Message}");
        }
        return 0;
    }

    private string GenerateChunkId(string filePath, string text, DateTime lastModified)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var textHash = text.GetHashCode().ToString("X");
        var timeStamp = lastModified.Ticks.ToString();
        return $"{fileName}_{textHash}_{timeStamp}";
    }

    // Enhanced chat response generation using /api/chat
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
                content = @"You are MEAI HR Policy Assistant, an expert advisor with deep knowledge of company policies and procedures. 

RESPONSE GUIDELINES:
• Provide accurate, helpful answers based on the policy context provided
• Use **bold** for important deadlines, amounts, or key requirements
• Use *italics* for emphasis on critical points
• If information is not available in the context, clearly state so
• Always end with: 'For additional clarification, please contact HR.'
• Be concise but thorough
• Reference specific policy sections when available"
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
            var response = await httpClient.PostAsJsonAsync("/api/chat", requestData);
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

    // Existing helper methods (simplified versions)...

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
                    model = model,
                    prompt = text,
                    options = new
                    {
                        num_ctx = 2048,
                        temperature = 0.0
                    }
                };

                var response = await httpClient.PostAsJsonAsync("/api/embeddings", request);
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

    // Add remaining helper methods...
    private bool IsTopicChanged(string question, List<ConversationTurn> history) =>
        history.Count > 0 && CalculateTextSimilarity(question, history.Last().Question) < 0.3;

    private double CalculateTextSimilarity(string text1, string text2)
    {
        var words1 = ExtractKeywords(text1).Split(' ').ToHashSet();
        var words2 = ExtractKeywords(text2).Split(' ').ToHashSet();
        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
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

    private ConversationContext GetOrCreateConversationContext(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return new ConversationContext();

        return _sessionContexts.GetOrAdd(sessionId, _ => new ConversationContext());
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

    private async Task<bool> IsModelAvailable(string modelName)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/tags");
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking model availability: {ex.Message}");
        }
        return false;
    }

    private string GetBestEmbeddingModel()
    {
        var preferredModels = new[]
        {
            "nomic-embed-text:latest",
            "mxbai-embed-large",
            "bge-large-en",
            "bge-base-en"
        };

        return preferredModels.FirstOrDefault(SupportsEmbeddings) ?? "nomic-embed-text:latest";
    }

    private bool SupportsEmbeddings(string model)
    {
        var embeddingModels = new[]
        {
            "nomic-embed-text", "mxbai-embed-large", "bge-large-en", "bge-base-en"
        };

        return embeddingModels.Any(em => model.ToLower().Contains(em));
    }

    private List<string> GetAllPolicyFiles() =>
        Directory.GetFiles(POLICIES_FOLDER, "*.*", SearchOption.AllDirectories)
            .Where(f => SUPPORTED_EXTENSIONS.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

    private async Task<string> ReadFileContent(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".txt" or ".md" => await File.ReadAllTextAsync(filePath),
            ".pdf" => ExtractTextFromPdf(filePath),
            ".docx" => ExtractTextFromDocx(filePath),
            _ => ""
        };
    }

    private string ExtractTextFromPdf(string filePath)
    {
        // Implement PDF text extraction (you'll need a PDF library like iTextSharp)
        // For now, return empty string
        Console.WriteLine($"PDF extraction not implemented for {filePath}");
        return "";
    }

    private string ExtractTextFromDocx(string filePath)
    {
        // Implement DOCX text extraction (you'll need a library like DocumentFormat.OpenXml)
        // For now, return empty string
        Console.WriteLine($"DOCX extraction not implemented for {filePath}");
        return "";
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
    private async Task LoadCorrectionsAsync()
    {
        try
        {
            // Get correction count from ChromaDB
            var response = await chromaClient.GetAsync($"/api/v2/tenants/{tenant}/databases/{database}/collections/{CORRECTIONS_COLLECTION_NAME}/count");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var collection = JsonSerializer.Deserialize<ChromaCollection>(content);
                Console.WriteLine($"📋 Loaded corrections collection with {collection?.Count ?? 0} entries");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load corrections info: {ex.Message}");
        }
    }
    public class ChromaCollection
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public Dictionary<string, object> Metadata { get; set; } = new();
        public int Count { get; set; }
    }

    public class CorrectionEntry
    {
        public int Id { get; set; }
        public string Question { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    private int EstimateTokenCount(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? 0 : text.Length / 4; // Approximate: 1 token ~ 4 chars
    }

    // === Add Missing Method: CheckCorrections ===
    private async Task<CorrectionEntry?> CheckCorrections(string question)
    {
        return await Task.Run(() =>
        {
            lock (_lockObject)
            {
                return _correctionsCache.FirstOrDefault(c =>
                    c.Question.Equals(question, StringComparison.OrdinalIgnoreCase));
            }
        });
    }
    private async Task<List<RelevantChunk>> ReRankChunks(
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

            var response = await httpClient.PostAsJsonAsync("/api/generate", requestData);
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

}