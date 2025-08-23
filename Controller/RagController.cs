using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class RagController : ControllerBase
    {
        private readonly IRAGService _ragService;
        private readonly ILogger<RagController> _logger;
        private readonly DynamicCodingAssistanceService _codingService; // Add this
        private readonly CodingDetectionService _codingDetection;


        [ActivatorUtilitiesConstructor]
        public RagController(
        IRAGService ragService,
        DynamicCodingAssistanceService codingService, // Inject coding service
        CodingDetectionService codingDetection,
        ILogger<RagController> logger)
        {
            _ragService = ragService;
            _codingService = codingService;
            _logger = logger;
            _codingDetection = codingDetection;
        }



        //[HttpPost("query")]
        //public async Task<ActionResult<QueryResponse>> Query([FromBody] QueryRequest request)
        //{
        //    if (string.IsNullOrWhiteSpace(request.Question))
        //        return BadRequest("Question cannot be empty");

        //    try
        //    {
        //        // ✅ FIXED: Updated call to match DynamicRagService signature
        //        var response = await _ragService.ProcessQueryAsync(
        //            question: request.Question,
        //            request.Plant,
        //            generationModel: request.GenerationModel, // New parameter
        //            embeddingModel: request.EmbeddingModel,   // New parameter
        //            maxResults: request.MaxResults,
        //            meaiInfo: request.meai_info,
        //            sessionId: request.sessionId,
        //            useReRanking: true

        //        );

        //        return Ok(response);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Query processing failed for question: {Question}", request.Question);
        //        return StatusCode(500, new { error = ex.Message, details = ex.StackTrace });
        //    }
        //}

        [HttpPost("query")]
        public async Task<ActionResult<QueryResponse>> Query([FromBody] QueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty");

            try
            {
                // 🔍 DETECT IF IT'S A CODING QUERY
                var codingDetection = _codingDetection.DetectCodingQuery(request.Question);

                if (codingDetection.IsCodingRelated && codingDetection.Confidence > 0.5)
                {
                    _logger.LogInformation($"🖥️ Detected coding query with {codingDetection.Confidence:P0} confidence. Language: {codingDetection.DetectedLanguage}");

                    // Route to coding assistance service
                    var codingResponse = await _codingService.ProcessCodingQueryAsync(
                        codingQuestion: request.Question,
                        codeContext: null, // You can extract this from the request if needed
                        language: codingDetection.DetectedLanguage != "general" ? codingDetection.DetectedLanguage : null,
                        sessionId: request.sessionId,
                        includeExamples: true,
                        difficulty: "intermediate" // You can make this configurable
                    );

                    // Convert coding response to standard query response format
                    return Ok(new QueryResponse
                    {
                        Answer = codingResponse.Solution,
                        Sources = new List<string> { $"{codingResponse.Language} Coding Assistant" },
                        SessionId = codingResponse.SessionId,
                        ProcessingTimeMs = codingResponse.ProcessingTimeMs,
                        IsFromCache = codingResponse.IsFromCache,
                        Confidence = codingResponse.Confidence,
                        Metadata = new Dictionary<string, object>
                        {
                            ["IsCodingResponse"] = true,
                            ["Language"] = codingResponse.Language,
                            ["TechnicalLevel"] = codingResponse.TechnicalLevel,
                            ["SolutionComplexity"] = codingResponse.SolutionComplexity,
                            ["CodeExamples"] = codingResponse.CodeExamples,
                            ["RecommendedNextSteps"] = codingResponse.RecommendedNextSteps,
                            ["RelatedTopics"] = codingResponse.RelatedTopics
                        }
                    });
                }

                // 📚 ROUTE TO REGULAR RAG SERVICE
                _logger.LogInformation("📚 Processing as regular RAG query");
                var response = await _ragService.ProcessQueryAsync(
                    question: request.Question,
                    request.Plant,
                    generationModel: request.GenerationModel,
                    embeddingModel: request.EmbeddingModel,
                    maxResults: request.MaxResults,
                    meaiInfo: request.meai_info,
                    sessionId: request.sessionId,
                    useReRanking: true
                );

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Query processing failed for question: {Question}", request.Question);
                return StatusCode(500, new { error = ex.Message, details = ex.StackTrace });
            }
        }

        // Add a dedicated coding endpoint for explicit coding queries
        [HttpPost("coding-query")]
        public async Task<ActionResult<CodingAssistanceResponse>> CodingQuery([FromBody] CodingQueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty");

            try
            {
                var response = await _codingService.ProcessCodingQueryAsync(
                    codingQuestion: request.Question,
                    codeContext: request.CodeContext,
                    language: request.Language,
                    sessionId: request.SessionId,
                    includeExamples: request.IncludeExamples,
                    difficulty: request.Difficulty ?? "intermediate"
                );

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coding query processing failed: {Question}", request.Question);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("models")]
        public async Task<ActionResult<List<ModelConfiguration>>> GetAvailableModels()
        {
            try
            {
                var models = await _ragService.GetAvailableModelsAsync();
                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available models");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("feedback")]
        public async Task<ActionResult> SubmitFeedback([FromBody] FeedbackRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.CorrectAnswer))
                return BadRequest("Question and correct answer are required");

            try
            {
                await _ragService.ApplyCorrectionAsync(request.sessionId, request.Question, request.CorrectAnswer, request.model);
                return Ok(new { message = "Feedback saved successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("status")]
        public ActionResult<SystemStatus> GetStatus()
        {
            //var status = _ragService.GetSystemStatusAsync();
            //return Ok(status);
            return Ok();
        }

        [HttpPost("refresh-embeddings")]
        public async Task<ActionResult> RefreshEmbeddings()
        {
            try
            {
                await _ragService.InitializeAsync();
                return Ok(new { message = "Embeddings refreshed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("corrections")]
        public ActionResult<List<CorrectionEntry>> GetCorrections([FromQuery] int limit = 50)
        {
            //var corrections = _ragService.GetRecentCorrections(limit);
            //return Ok(corrections);
            return Ok();
        }

        [HttpDelete("corrections/{id}")]
        public async Task<ActionResult> DeleteCorrection(string id)
        {
            try
            {
                //var success = await _ragService.DeleteCorrectionAsync(id);
                //if (!success)
                //    return NotFound("Correction not found");

                return Ok(new { message = "Correction deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("upload-policy")]
        public async Task<ActionResult> UploadPolicy(IFormFile file, string model)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            try
            {
                //await _ragService.ProcessUploadedPolicyAsync(file, model);
                return Ok(new { message = "Policy uploaded and processed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        [HttpPost("feedback/like")]
        public async Task<IActionResult> Like([FromBody] FeedbackRequest feedback)
        {
            await _ragService.MarkAppreciatedAsync(feedback.sessionId, feedback.Question);
            return Ok();
        }

        [HttpGet("rag-status")]
        public async Task<IActionResult> GetRagStatus()
        {
            try
            {
                if (_ragService is DynamicRagService dynamicRag)
                {
                    var isInitialized = dynamicRag._isInitialized; // You'll need to make this public
                    var systemStatus = await dynamicRag.GetSystemStatusAsync();

                    return Ok(new
                    {
                        IsInitialized = systemStatus.IsHealthy,
                        IsHealthy = systemStatus.IsHealthy,
                        Status = isInitialized ? "Ready" : "Initializing",
                        Message = isInitialized ? "RAG system is ready" : "RAG system is still initializing..."
                    });
                }

                return Ok(new { Status = "Unknown", Message = "Unable to determine RAG status" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = "Error", Message = ex.Message });
            }
        }

        [HttpGet("delete-model")]
        public async Task<IActionResult> DeleteModelFromChroma(string model)
        {
            await _ragService.DeleteModelDataFromChroma(model);
            return Ok(new { message = $"Model {model} data deleted from ChromaDB" });
        }
    }
}
public class CodingQueryRequest
{
    public string Question { get; set; } = string.Empty;
    public string? CodeContext { get; set; }
    public string? Language { get; set; }
    public string? SessionId { get; set; }
    public bool IncludeExamples { get; set; } = true;
    public string? Difficulty { get; set; } = "intermediate";
}
