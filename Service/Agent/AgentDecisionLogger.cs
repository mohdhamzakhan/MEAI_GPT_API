using System.Text.Json;

namespace MEAI_GPT_API.Services.Agent
{
    public class AgentDecisionLogger
    {
        private readonly ILogger<AgentDecisionLogger> _logger;
        private readonly string _logFile;

        public AgentDecisionLogger(ILogger<AgentDecisionLogger> logger)
        {
            _logger = logger;
            _logFile = Path.Combine(AppContext.BaseDirectory, "Logs", "agent-decisions.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        }

        public void LogDecision(AgentDecision decision)
        {
            try
            {
                var json = JsonSerializer.Serialize(decision, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                File.AppendAllText(_logFile, json + Environment.NewLine);

                _logger.LogInformation(
                    "🤖 Agent Decision: {Phase} -> {Decision} (Confidence: {Confidence:P0})",
                    decision.Phase,
                    decision.DecisionMade,
                    decision.Confidence
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log agent decision");
            }
        }

        public void LogStep(string step, object details)
        {
            LogDecision(new AgentDecision
            {
                Phase = step,
                DecisionMade = "ExecutionStep",
                Reasoning = JsonSerializer.Serialize(details),
                Confidence = 1.0,
                Timestamp = DateTime.Now
            });
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
    }
}