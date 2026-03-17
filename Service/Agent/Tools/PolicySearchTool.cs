using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace MEAI_GPT_API.Services.Agent.Tools
{
    public class PolicySearchTool : IAgentTool
    {
        public string Name => "PolicySearchTool";
        public string Description => "Searches company policy documents using hybrid retrieval";
        public int Priority => 10;

        private readonly IModelManager _modelManager;
        private readonly DynamicCollectionManager _collectionManager;
        private readonly AbbreviationExpansionService _abbreviationService;
        private readonly ILogger<PolicySearchTool> _logger;
        private readonly HttpClient _chromaClient;
        private readonly ChromaDbOptions _chromaOptions;

        public PolicySearchTool(
            IModelManager modelManager,
            DynamicCollectionManager collectionManager,
            AbbreviationExpansionService abbreviationService,
            IHttpClientFactory httpClientFactory,
            IOptions<ChromaDbOptions> chromaOptions,
            ILogger<PolicySearchTool> logger)
        {
            _modelManager = modelManager;
            _collectionManager = collectionManager;
            _abbreviationService = abbreviationService;
            _chromaClient = httpClientFactory.CreateClient("ChromaDB");
            _chromaOptions = chromaOptions.Value;
            _logger = logger;
        }

        public async Task<bool> CanHandleAsync(string query, AgentContext context)
        {
            // This tool handles policy-related queries for specific plants
            return !string.IsNullOrEmpty(context.Plant) &&
                   context.Plant != "General";
        }

        public async Task<double> EstimateConfidenceAsync(string query, AgentContext context)
        {
            // Check if query contains policy-related keywords
            var policyKeywords = new[] { "policy", "leave", "hr", "procedure", "section", "rule" };
            var queryLower = query.ToLowerInvariant();

            var matches = policyKeywords.Count(k => queryLower.Contains(k));
            return Math.Min(1.0, matches * 0.3);
        }

        public async Task<ToolResult> ExecuteAsync(ToolRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ToolResult { ToolName = Name };

            try
            {
                var strategy = request.Parameters.GetValueOrDefault("strategy", "Broad").ToString();
                var maxResults = Convert.ToInt32(request.Parameters.GetValueOrDefault("maxResults", 10));
                var plant = request.Context.Plant;

                _logger.LogInformation($"🔍 PolicySearchTool: {strategy} search for plant {plant}");

                // Get embedding model
                var embeddingModel = await _modelManager.GetModelAsync("nomic-embed-text:v1.5");
                if (embeddingModel == null)
                {
                    throw new ToolExecutionException(Name, "Embedding model not available");
                }

                // Expand query with abbreviations
                var expandedQuery = _abbreviationService.ExpandQuery(request.Query);

                // Perform search
                var chunks = await SearchPolicyDocuments(
                    expandedQuery,
                    embeddingModel,
                    maxResults,
                    plant
                );

                result.Success = true;
                result.Data = chunks;
                result.Confidence = chunks.Any() ? chunks.Average(c => c.Similarity) : 0.0;
                result.Sources = chunks.Select(c => c.Source).Distinct().ToList();
                result.Metadata["retrievalStrategy"] = strategy;
                result.Metadata["totalChunks"] = chunks.Count;

                stopwatch.Stop();
                result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PolicySearchTool execution failed");

                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;

                return result;
            }
        }

        private async Task<List<RelevantChunk>> SearchPolicyDocuments(
            string query,
            ModelConfiguration embeddingModel,
            int maxResults,
            string plant)
        {
            // Implementation using your existing search logic
            // This is a simplified version - adapt from your SearchChromaDBAsync method

            var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embeddingModel);

            // Generate query embedding
            var queryEmbedding = await GenerateEmbedding(query, embeddingModel);

            // Search ChromaDB
            var searchData = new
            {
                query_embeddings = new List<List<float>> { queryEmbedding },
                n_results = maxResults,
                include = new[] { "documents", "metadatas", "distances" },
                where = new Dictionary<string, object>
                {
                    { "plant", plant }
                }
            };

            var response = await _chromaClient.PostAsJsonAsync(
                $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/query",
                searchData
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new ToolExecutionException(Name, "ChromaDB query failed");
            }

            var content = await response.Content.ReadAsStringAsync();
            // Parse and return chunks (adapt from your existing code)

            return new List<RelevantChunk>(); // Placeholder
        }

        private async Task<List<float>> GenerateEmbedding(string text, ModelConfiguration model)
        {
            // Implement embedding generation (copy from your existing code)
            return new List<float>(); // Placeholder
        }
    }
}