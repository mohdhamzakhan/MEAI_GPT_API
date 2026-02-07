using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using Microsoft.AspNetCore.Mvc;

namespace MEAI_GPT_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RerankController : ControllerBase
    {
        private readonly RerankerService _rerankerService;
        private readonly ILogger<RerankController> _logger;

        public RerankController(
            RerankerService rerankerService,
            ILogger<RerankController> logger)
        {
            _rerankerService = rerankerService;
            _logger = logger;
        }

        [HttpPost]
        public async Task<ActionResult<RerankResponse>> Rerank(
            [FromBody] RerankRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return BadRequest("Query is required.");

            if (request.Chunks == null || request.Chunks.Count == 0)
                return Ok(new RerankResponse { Chunks = request.Chunks });

            try
            {
                var result = request.Chunks;

                if (request.UseReranking && request.Chunks.Count > 3)
                {
                    result = await _rerankerService.RerankAsync(
                        request.Query,
                        request.Chunks,
                        request.Model,
                        request.TopK);
                }

                return Ok(new RerankResponse
                {
                    Chunks = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reranking failed");
                return StatusCode(500, "Reranking failed");
            }
        }
    }
}
