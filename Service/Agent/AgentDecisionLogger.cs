using System.Text.Json;

namespace MEAI_GPT_API.Services.Agent
{
    public class AgentDecisionLogger
    {
        private static readonly object _lock = new();
        private readonly ILogger<AgentDecisionLogger> _logger;
        private readonly string _logDir;
        private readonly string _logFile;
        private const long MaxFileBytes = 10 * 1024 * 1024;   // rotate at 10 MB
        private const int RetentionDays = 30;

        public AgentDecisionLogger(ILogger<AgentDecisionLogger> logger)
        {
            _logger = logger;
            _logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
            _logFile = Path.Combine(_logDir, "agent-decisions.jsonl");
            Directory.CreateDirectory(_logDir);
            CleanupOldFiles();
        }

        public void LogDecision(AgentDecision decision)
        {
            try
            {
                var json = JsonSerializer.Serialize(decision);

                lock (_lock)
                {
                    RotateIfNeeded();
                    File.AppendAllText(_logFile, json + Environment.NewLine);
                }

                _logger.LogInformation("🤖 Agent Decision: {Phase} -> {Decision} (Confidence: {Confidence:P0})",
                    decision.Phase, decision.DecisionMade, decision.Confidence);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log agent decision");
            }
        }

        public void LogStep(string step, object details, string? sessionId = null)
        {
            LogDecision(new AgentDecision
            {
                Phase = step,
                DecisionMade = "ExecutionStep",
                Reasoning = JsonSerializer.Serialize(details),
                Confidence = 1.0,
                Timestamp = DateTime.Now,
                SessionId = sessionId
            });
        }

        private void RotateIfNeeded()
        {
            var info = new FileInfo(_logFile);
            if (!info.Exists || info.Length < MaxFileBytes) return;

            var archive = Path.Combine(_logDir, $"agent-decisions-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            File.Move(_logFile, archive);
        }

        private void CleanupOldFiles()
        {
            try
            {
                // only archives; the live agent-decisions.jsonl doesn't match this pattern
                foreach (var f in Directory.GetFiles(_logDir, "agent-decisions-*.jsonl"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays))
                        File.Delete(f);
            }
            catch { /* never block startup */ }
        }
    }

    public class AgentDecision
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Phase { get; set; } = "";
        public string DecisionMade { get; set; } = "";
        public string Reasoning { get; set; } = "";
        public double Confidence { get; set; }
        public Dictionary<string, object> Context { get; set; } = new();
        public List<string> AlternativesConsidered { get; set; } = new();
        public string? SessionId { get; set; }
    }
}