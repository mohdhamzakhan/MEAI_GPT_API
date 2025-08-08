using MEAI_GPT_API.Models;
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

        public RagController(IRAGService ragService, ILogger<RagController> logger)
        {
            _ragService = ragService;
            _logger = logger;
        }



        [HttpPost("query")]
        public async Task<ActionResult<QueryResponse>> Query([FromBody] QueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty");

            try
            {
                // ✅ FIXED: Updated call to match DynamicRagService signature
                var response = await _ragService.ProcessQueryAsync(
                    question: request.Question,
                    request.Plant,
                    generationModel: request.GenerationModel, // New parameter
                    embeddingModel: request.EmbeddingModel,   // New parameter
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
            await _ragService.MarkAppreciatedAsync(feedback.sessionId, feedback.Question, feedback.Plant);
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
