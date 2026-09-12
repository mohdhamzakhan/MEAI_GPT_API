using MEAI_GPT_API.Service.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class DiagnosticsController : ControllerBase
    {
        private readonly GradeHierarchyService _gradeHierarchy;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DiagnosticsController> _logger;
        private readonly IRAGService _ragService;

        public DiagnosticsController(
            GradeHierarchyService gradeHierarchy,
            IConfiguration configuration,
            ILogger<DiagnosticsController> logger,
            IRAGService rAGService)
        {
            _gradeHierarchy = gradeHierarchy;
            _configuration = configuration;
            _logger = logger;
            _ragService = rAGService;
        }

        // GET /api/diagnostics/ambiguous-titles
        [HttpGet("ambiguous-titles")]
        public IActionResult GetAmbiguousTitlesAudit()
        {
            var audit = _gradeHierarchy.AuditAmbiguousTitles();
            var missing = audit.Where(a => !a.HasExplicitOverride).ToList();

            return Ok(new
            {
                totalAmbiguousTitles = audit.Count,
                missingOverrides = missing.Count,
                details = audit
            });
        }

        // GET /api/diagnostics/eligibility-nulls
        [HttpGet("eligibility-nulls")]
        public IActionResult GetEligibilityNullAudit()
        {
            var cachePath = _configuration["GradeEligibility:CacheFilePath"]
                            ?? "./context/grade-eligibility-cache.json";

            if (!System.IO.File.Exists(cachePath))
                return NotFound(new { error = $"Cache file not found at {cachePath}" });

            var entries = JsonSerializer.Deserialize<List<ChunkEligibility>>(
                System.IO.File.ReadAllText(cachePath)) ?? new();

            var empty = entries
                .Where(e => e.MinGradeBand is null && e.MaxGradeBand is null && e.EmployeeCategory is null)
                .ToList();

            var byPath = empty
                .GroupBy(e => e.ExtractionPath)
                .Select(g => new { path = g.Key, count = g.Count() })
                .ToList();

            return Ok(new
            {
                totalEntries = entries.Count,
                fullyEmptyEligibility = empty.Count,
                breakdownByPath = byPath,
                // the only bucket actually worth eyeballing:
                llmExtractedButEmpty = empty
                    .Where(e => e.ExtractionPath == "LlmExtracted")
                    .Select(e => new { e.SourceFile, e.ChunkKey, e.ChunkPreview })
                    .Take(50)
            });
        }
        public class RepairEligibilityRequest
        {
            public List<string> SourceFiles { get; set; } = new();
        }

        [HttpPost("repair-eligibility")]
        public async Task<IActionResult> RepairEligibility([FromBody] RepairEligibilityRequest request)
        {
            if (request.SourceFiles == null || !request.SourceFiles.Any())
                return BadRequest(new { error = "Provide sourceFiles: the list of files flagged by the timeout log grep." });

            var results = await _ragService.RepairEligibilityForFilesAsync(request.SourceFiles);
            return Ok(results);
        }
    }
}