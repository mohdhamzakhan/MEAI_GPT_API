using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;

namespace MEAI_GPT_API.Services.Agent
{
    public class AgentPlanner
    {
        private readonly QueryIntentAnalyzer _intentAnalyzer;
        private readonly AgentDecisionLogger _decisionLogger;
        private readonly ILogger<AgentPlanner> _logger;

        public AgentPlanner(
            QueryIntentAnalyzer intentAnalyzer,
            AgentDecisionLogger decisionLogger,
            ILogger<AgentPlanner> logger)
        {
            _intentAnalyzer = intentAnalyzer;
            _decisionLogger = decisionLogger;
            _logger = logger;
        }

        public async Task<ExecutionPlan> PlanQueryAsync(
            string query,
            AgentContext context,
            bool meaiInfo)
        {
            var plan = new ExecutionPlan
            {
                OriginalQuery = query,
                CreatedAt = DateTime.Now
            };

            try
            {
                // 1. Analyze intent
                var intent = _intentAnalyzer.DetectPolicyIntent(query);
                plan.Intent = intent;

                _decisionLogger.LogDecision(new AgentDecision
                {
                    Phase = "IntentAnalysis",
                    DecisionMade = $"Detected: {intent.IntendedPolicyType ?? "General"}",
                    Reasoning = $"Confidence: {intent.Confidence:P0}",
                    Confidence = intent.Confidence
                });

                // 2. Determine retrieval strategy
                if (meaiInfo)
                {
                    plan.AddStep(new PlanStep
                    {
                        Name = "PolicyRetrieval",
                        ToolName = "PolicySearchTool",
                        Parameters = new Dictionary<string, object>
                        {
                            ["strategy"] = intent.Confidence > 0.7 ? "Targeted" : "Broad",
                            ["policyType"] = intent.IntendedPolicyType ?? "General",
                            ["maxResults"] = 10
                        },
                        ExpectedDuration = TimeSpan.FromSeconds(2)
                    });

                    // Add reranking if needed
                    if (intent.Confidence < 0.6)
                    {
                        plan.AddStep(new PlanStep
                        {
                            Name = "SemanticReranking",
                            ToolName = "RerankTool",
                            Parameters = new Dictionary<string, object>
                            {
                                ["topK"] = 5
                            },
                            ExpectedDuration = TimeSpan.FromSeconds(1)
                        });
                    }
                }
                else
                {
                    plan.AddStep(new PlanStep
                    {
                        Name = "GeneralKnowledge",
                        ToolName = "GeneralChatTool",
                        Parameters = new Dictionary<string, object>(),
                        ExpectedDuration = TimeSpan.FromSeconds(3)
                    });
                }

                // 3. Add generation step
                plan.AddStep(new PlanStep
                {
                    Name = "ResponseGeneration",
                    ToolName = "LLMGenerationTool",
                    Parameters = new Dictionary<string, object>
                    {
                        ["temperature"] = meaiInfo ? 0.1 : 0.7,
                        ["modelSelection"] = "Auto"
                    },
                    ExpectedDuration = TimeSpan.FromSeconds(5)
                });

                // 4. Add verification step
                plan.AddStep(new PlanStep
                {
                    Name = "ResponseVerification",
                    ToolName = "VerificationTool",
                    Parameters = new Dictionary<string, object>
                    {
                        ["minConfidence"] = 0.7,
                        ["checkFactuality"] = meaiInfo
                    },
                    ExpectedDuration = TimeSpan.FromMilliseconds(500)
                });

                plan.IsComplete = true;
                plan.EstimatedTotalDuration = TimeSpan.FromSeconds(
                    plan.Steps.Sum(s => s.ExpectedDuration.TotalSeconds)
                );

                _decisionLogger.LogDecision(new AgentDecision
                {
                    Phase = "Planning",
                    DecisionMade = $"Created {plan.Steps.Count}-step plan",
                    Reasoning = string.Join(" -> ", plan.Steps.Select(s => s.Name)),
                    Confidence = 1.0,
                    Context = new Dictionary<string, object>
                    {
                        ["estimatedDuration"] = plan.EstimatedTotalDuration.TotalSeconds
                    }
                });

                return plan;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planning failed");
                throw new PlanningException($"Failed to create execution plan: {ex.Message}");
            }
        }
    }

    public class ExecutionPlan
    {
        public string OriginalQuery { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public PolicyIntent? Intent { get; set; }
        public List<PlanStep> Steps { get; set; } = new();
        public bool IsComplete { get; set; }
        public TimeSpan EstimatedTotalDuration { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();

        public void AddStep(PlanStep step)
        {
            step.Order = Steps.Count;
            Steps.Add(step);
        }
    }

    public class PlanStep
    {
        public int Order { get; set; }
        public string Name { get; set; } = "";
        public string ToolName { get; set; } = "";
        public Dictionary<string, object> Parameters { get; set; } = new();
        public TimeSpan ExpectedDuration { get; set; }
        public bool IsOptional { get; set; }
        public List<string> DependsOn { get; set; } = new();
    }
}