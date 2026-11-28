// Evaluation/EvalRunner.cs
//
// Runs a fixed question set through the REAL streaming pipeline
// (DynamicRagService.ProcessQueryStreamAsync), so a retrieval/prompt/verifier change is
// measured the same way a user would hit it. Every case gets its own new session, so one
// failed answer can never pollute the next case's history.
//
// Each case can check:
//   - expectedSources : at least one returned source file name contains one of these
//   - mustContain     : every phrase appears in the answer
//   - mustNotContain  : none of these phrases appear in the answer
//   - expectRefusal   : the system must decline instead of answering (adversarial cases)
//
// A report is written to logs/eval-reports/ and compared with the previous run, so a case
// that used to pass and now fails is flagged as a REGRESSION.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Services;

namespace MEAI_GPT_API.Evaluation
{
    public class EvalCase
    {
        public string Id { get; set; } = "";
        public string Question { get; set; } = "";
        public string? Plant { get; set; }
        public string? UserId { get; set; }
        public List<string> ExpectedSources { get; set; } = new();
        public List<string> MustContain { get; set; } = new();
        public List<string> MustNotContain { get; set; } = new();
        public bool ExpectRefusal { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    public class EvalCaseResult
    {
        public string Id { get; set; } = "";
        public string Question { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public bool Passed { get; set; }
        public bool SourceOk { get; set; }
        public bool ContainsOk { get; set; }
        public bool NotContainsOk { get; set; }
        public bool RefusalOk { get; set; }
        public bool Refused { get; set; }
        public List<string> ReturnedSources { get; set; } = new();
        public List<string> FailureReasons { get; set; } = new();
        public string AnswerPreview { get; set; } = "";
        public string? AnswerQualityJson { get; set; }
        public string? Error { get; set; }
        public long ElapsedMs { get; set; }
    }

    public class EvalReport
    {
        public DateTime StartedAt { get; set; }
        public int Total { get; set; }
        public int Passed { get; set; }
        public double PassRate { get; set; }
        public double SourceHitRate { get; set; }      // among cases that declare expectedSources
        public long AverageMs { get; set; }
        public List<string> Regressions { get; set; } = new();
        public List<string> Fixed { get; set; } = new();
        public string? PreviousReport { get; set; }
        public string? ReportFile { get; set; }
        public List<EvalCaseResult> Results { get; set; } = new();
    }

    public class EvalRunner
    {
        private static readonly Regex RefusalPattern = new(
            @"contact (your )?(supervisor|hr)|does not contain|do(es)? not (have|include)|don't have sufficient|couldn't|could not|not (specified|covered|mentioned) in|not confident",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOut = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IRAGService _rag;
        private readonly ILogger<EvalRunner> _logger;

        public EvalRunner(IRAGService rag, ILogger<EvalRunner> logger)
        {
            _rag = rag;
            _logger = logger;
        }

        public async Task<EvalReport> RunAsync(
            List<EvalCase> cases, string defaultPlant, string defaultUserId, CancellationToken ct)
        {
            if (_rag is not DynamicRagService rag)
                throw new InvalidOperationException("IRAGService is not DynamicRagService; the evaluation needs the streaming pipeline.");

            var report = new EvalReport { StartedAt = DateTime.Now };

            foreach (var c in cases)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogInformation("🧪 Eval case {Id}: {Question}", c.Id, c.Question);
                report.Results.Add(await RunCaseAsync(rag, c, c.Plant ?? defaultPlant, c.UserId ?? defaultUserId, ct));
            }

            Summarize(report);
            CompareWithPrevious(report);
            Save(report);
            return report;
        }

        private static async Task<EvalCaseResult> RunCaseAsync(
            DynamicRagService rag, EvalCase c, string plant, string userId, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var result = new EvalCaseResult { Id = c.Id, Question = c.Question, Tags = c.Tags };
            var answer = new StringBuilder();
            var sources = new List<string>();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(4));

            try
            {
                await foreach (var chunk in rag.ProcessQueryStreamAsync(
                    c.Question, plant,
                    sessionId: Guid.NewGuid().ToString("N"),
                    userId: userId,
                    cancellationToken: cts.Token))
                {
                    switch (chunk.Type)
                    {
                        case "sources":
                            if (chunk.Sources != null) sources.AddRange(chunk.Sources);
                            break;
                        case "response":
                            answer.Append(chunk.Content);
                            break;
                        case "answer_quality":
                            result.AnswerQualityJson = chunk.Content;
                            break;
                        case "requires_designation":
                            result.Error = $"requires_designation: '{userId}' has no designation on file";
                            break;
                        case "error":
                            result.Error = chunk.Content;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { result.Error ??= "timeout (4 min)"; }
            catch (Exception ex) { result.Error = ex.Message; }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.ReturnedSources = sources.Distinct().ToList();

            var text = answer.ToString();
            result.AnswerPreview = text.Length > 400 ? text[..400] + "…" : text;
            result.Refused = RefusalPattern.IsMatch(text);

            static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "");

            result.SourceOk = c.ExpectedSources.Count == 0 ||
                c.ExpectedSources.Any(e => result.ReturnedSources.Any(s => Norm(s).Contains(Norm(e))));

            var missing = c.MustContain
                .Where(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0).ToList();
            result.ContainsOk = missing.Count == 0;

            var forbidden = c.MustNotContain
                .Where(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            result.NotContainsOk = forbidden.Count == 0;

            result.RefusalOk = c.ExpectRefusal ? result.Refused : !result.Refused;

            if (result.Error != null) result.FailureReasons.Add($"error: {result.Error}");
            if (!result.SourceOk) result.FailureReasons.Add(
                $"expected source [{string.Join(" | ", c.ExpectedSources)}] not in [{string.Join(", ", result.ReturnedSources)}]");
            if (!result.ContainsOk) result.FailureReasons.Add($"answer missing: {string.Join(" | ", missing)}");
            if (!result.NotContainsOk) result.FailureReasons.Add($"answer contains forbidden: {string.Join(" | ", forbidden)}");
            if (!result.RefusalOk) result.FailureReasons.Add(
                c.ExpectRefusal ? "should have refused but answered" : "refused or hedged but should have answered");

            result.Passed = result.FailureReasons.Count == 0;
            return result;
        }

        private static void Summarize(EvalReport r)
        {
            r.Total = r.Results.Count;
            r.Passed = r.Results.Count(x => x.Passed);
            r.PassRate = r.Total == 0 ? 0 : Math.Round(100.0 * r.Passed / r.Total, 1);
            r.AverageMs = r.Total == 0 ? 0 : (long)r.Results.Average(x => x.ElapsedMs);

            var withSources = r.Results.Where(x => x.SourceOk || x.FailureReasons.Any(f => f.StartsWith("expected source"))).ToList();
            r.SourceHitRate = withSources.Count == 0
                ? 0
                : Math.Round(100.0 * withSources.Count(x => x.SourceOk) / withSources.Count, 1);
        }

        private static string ReportDir => Path.Combine(AppContext.BaseDirectory, "logs", "eval-reports");

        private void CompareWithPrevious(EvalReport current)
        {
            try
            {
                if (!Directory.Exists(ReportDir)) return;
                var previousFile = Directory.GetFiles(ReportDir, "eval-*.json")
                    .OrderByDescending(f => f).FirstOrDefault();
                if (previousFile == null) return;

                var previous = JsonSerializer.Deserialize<EvalReport>(
                    File.ReadAllText(previousFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (previous == null) return;

                var before = previous.Results.ToDictionary(x => x.Id, x => x.Passed);
                current.PreviousReport = Path.GetFileName(previousFile);

                foreach (var r in current.Results)
                {
                    if (!before.TryGetValue(r.Id, out var wasPassing)) continue;
                    if (wasPassing && !r.Passed) current.Regressions.Add(r.Id);
                    if (!wasPassing && r.Passed) current.Fixed.Add(r.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not compare with the previous evaluation report");
            }
        }

        private void Save(EvalReport report)
        {
            try
            {
                Directory.CreateDirectory(ReportDir);
                var file = Path.Combine(ReportDir, $"eval-{report.StartedAt:yyyyMMdd-HHmmss}.json");
                report.ReportFile = file;
                File.WriteAllText(file, JsonSerializer.Serialize(report, JsonOut));
                _logger.LogInformation(
                    "🧪 Eval done: {Passed}/{Total} passed ({Rate}%), source hit {Hit}%, regressions: {Reg}. Report: {File}",
                    report.Passed, report.Total, report.PassRate, report.SourceHitRate,
                    report.Regressions.Count == 0 ? "none" : string.Join(", ", report.Regressions), file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save the evaluation report");
            }
        }
    }
}