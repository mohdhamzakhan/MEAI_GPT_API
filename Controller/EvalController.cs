// Controller/EvalController.cs
//
//   GET  /api/eval/cases                      -> the loaded question set
//   POST /api/eval/run?plant=Sanand&userId=hamza.khan&tag=centralized
//                                             -> runs all cases (or only those with the tag)
//
// Cases come from ./context/eval-cases.json (override with config key Eval:CasesFile).
// userId must exist in employee-directory.json, otherwise the pipeline asks for a designation.

using System.Text.Json;
using MEAI_GPT_API.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MEAI_GPT_API.Controller
{
    [ApiController]
    [Route("api/eval")]
    [Authorize]
    public class EvalController : ControllerBase
    {
        private readonly EvalRunner _runner;
        private readonly IConfiguration _config;
        private readonly ILogger<EvalController> _logger;

        public EvalController(EvalRunner runner, IConfiguration config, ILogger<EvalController> logger)
        {
            _runner = runner;
            _config = config;
            _logger = logger;
        }

        private string CasesPath => _config["Eval:CasesFile"] ?? "./context/eval-cases.json";

        private List<EvalCase>? LoadCases()
        {
            if (!System.IO.File.Exists(CasesPath)) return null;
            return JsonSerializer.Deserialize<List<EvalCase>>(
                System.IO.File.ReadAllText(CasesPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        [HttpGet("cases")]
        public IActionResult GetCases()
        {
            var cases = LoadCases();
            return cases == null
                ? NotFound(new { error = $"Cases file not found at {CasesPath}" })
                : Ok(new { count = cases.Count, cases });
        }

        [HttpPost("run")]
        public async Task<IActionResult> Run(
            [FromQuery] string plant = "Sanand",
            [FromQuery] string userId = "hamza.khan",
            [FromQuery] string? tag = null,
            CancellationToken ct = default)
        {
            var cases = LoadCases();
            if (cases == null)
                return NotFound(new { error = $"Cases file not found at {CasesPath}" });

            if (!string.IsNullOrWhiteSpace(tag))
                cases = cases.Where(c => c.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))).ToList();

            if (cases.Count == 0)
                return BadRequest(new { error = "No cases matched." });

            _logger.LogInformation("Starting evaluation: {Count} case(s), plant {Plant}, user {User}", cases.Count, plant, userId);

            var report = await _runner.RunAsync(cases, plant, userId, ct);

            // Summary first, details below; failures are listed so they're easy to find.
            return Ok(new
            {
                report.Total,
                report.Passed,
                report.PassRate,
                report.SourceHitRate,
                report.AverageMs,
                report.Regressions,
                report.Fixed,
                report.PreviousReport,
                report.ReportFile,
                failures = report.Results.Where(r => !r.Passed)
                    .Select(r => new { r.Id, r.Question, r.FailureReasons, r.ReturnedSources, r.AnswerPreview }),
                results = report.Results.Select(r => new { r.Id, r.Passed, r.ElapsedMs })
            });
        }
    }
}