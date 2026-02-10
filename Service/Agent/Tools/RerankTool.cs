using System.Diagnostics;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;

namespace MEAI_GPT_API.Services.Agent.Tools
{
    public class RerankTool : IAgentTool
    {
        public string Name => "RerankTool";
        public string Description => "Reranks search results using cross-encoder model";
        public int Priority => 5;

        private readonly RerankerService _rerankerService;
        private readonly ILogger<RerankTool> _logger;

        public RerankTool(
            RerankerService rerankerService,
            ILogger<RerankTool> logger)
        {
            _rerankerService = rerankerService;
            _logger = logger;
        }

        public Task<bool> CanHandleAsync(string query, AgentContext context)
        {
            // Reranking is always beneficial if we have search results
            return Task.FromResult(context.State.ContainsKey("PolicyRetrieval"));
        }

        public Task<double> EstimateConfidenceAsync(string query, AgentContext context)
        {
            return Task.FromResult(0.9); // Reranking is highly reliable
        }

        public async Task<ToolResult> ExecuteAsync(ToolRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ToolResult { ToolName = Name };

            try
            {
                // Get chunks from previous step
                if (!request.Context.State.TryGetValue("PolicyRetrieval", out var retrievalData))
                {
                    throw new ToolExecutionException(Name, "No search results to rerank");
                }

                var chunks = retrievalData as List<RelevantChunk> ?? new List<RelevantChunk>();
                var topK = Convert.ToInt32(request.Parameters.GetValueOrDefault("topK", 5));

                _logger.LogInformation($"🎯 RerankTool: Reranking {chunks.Count} chunks");

                var reranked = await _rerankerService.RerankAsync(
                    request.Query,
                    chunks,
                    "qllama/bge-reranker-v2-m3:f16",
                    topK
                );

                result.Success = true;
                result.Data = reranked;
                result.Confidence = 0.95;
                result.Metadata["originalCount"] = chunks.Count;
                result.Metadata["rerankedCount"] = reranked.Count;

                stopwatch.Stop();
                result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RerankTool execution failed");

                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;

                return result;
            }
        }
    }
}