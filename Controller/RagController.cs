using MEAI_GPT_API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class RagController : ControllerBase
    {
        private readonly RagService _ragService;

        public RagController(RagService ragService)
        {
            _ragService = ragService;
        }



        [HttpPost("query")]
        public async Task<ActionResult<QueryResponse>> Query([FromBody] QueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty");

            try
            {
                var response = await _ragService.ProcessQueryAsync(request.Question, request.model, request.MaxResults, request.meai_info, request.sessionId,true);
                return Ok(response);
            }
            catch (Exception ex)
            {
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
               await _ragService.SaveCorrectionAsync(request.Question, request.CorrectAnswer, request.model);
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
            var status = _ragService.GetSystemStatus();
            return Ok(status);
            //return Ok();
        }

        [HttpPost("refresh-embeddings")]
        public async Task<ActionResult> RefreshEmbeddings([FromBody] string model)
        {
            try
            {
                await _ragService.RefreshEmbeddingsAsync(model);
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
            var corrections = _ragService.GetRecentCorrections(limit);
            return Ok(corrections);
            //return Ok();
        }

        [HttpDelete("corrections/{id}")]
        public async Task<ActionResult> DeleteCorrection(string id)
        {
            try
            {
                var success = await _ragService.DeleteCorrectionAsync(id);
                if (!success)
                    return NotFound("Correction not found");

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
    }
}
