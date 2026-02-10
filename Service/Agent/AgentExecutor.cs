using System.Diagnostics;
using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Services.Agent
{
    public class AgentExecutor
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AgentDecisionLogger _decisionLogger;
        private readonly ILogger<AgentExecutor> _logger;
        private readonly Dictionary<string, IAgentTool> _tools = new();

        public AgentExecutor(
            IServiceProvider serviceProvider,
            AgentDecisionLogger decisionLogger,
            ILogger<AgentExecutor> logger)
        {
            _serviceProvider = serviceProvider;
            _decisionLogger = decisionLogger;
            _logger = logger;
        }

        public void RegisterTool(IAgentTool tool)
        {
            _tools[tool.Name] = tool;
            _logger.LogInformation($"📦 Registered tool: {tool.Name}");
        }

        public async Task<ExecutionResult> ExecutePlanAsync(
            ExecutionPlan plan,
            AgentContext context,
            CancellationToken cancellationToken = default)
        {
            var result = new ExecutionResult
            {
                Plan = plan,
                StartTime = DateTime.Now
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                foreach (var step in plan.Steps)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Status = ExecutionStatus.Cancelled;
                        break;
                    }

                    var stepResult = await ExecuteStepAsync(step, context, cancellationToken);
                    result.StepResults.Add(stepResult);

                    if (!stepResult.Success && !step.IsOptional)
                    {
                        result.Status = ExecutionStatus.Failed;
                        result.ErrorMessage = stepResult.ErrorMessage;
                        break;
                    }

                    // Store intermediate results for next steps
                    context.State[step.Name] = stepResult.Data;
                }

                if (result.Status != ExecutionStatus.Failed &&
                    result.Status != ExecutionStatus.Cancelled)
                {
                    result.Status = ExecutionStatus.Completed;
                }

                stopwatch.Stop();
                result.TotalDuration = stopwatch.Elapsed;
                result.EndTime = DateTime.Now;

                _decisionLogger.LogStep("ExecutionComplete", new
                {
                    status = result.Status.ToString(),
                    duration = result.TotalDuration.TotalSeconds,
                    stepsCompleted = result.StepResults.Count(s => s.Success)
                });

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Execution failed");

                result.Status = ExecutionStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.TotalDuration = stopwatch.Elapsed;
                result.EndTime = DateTime.Now;

                return result;
            }
        }

        private async Task<StepResult> ExecuteStepAsync(
            PlanStep step,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            var stepResult = new StepResult
            {
                StepName = step.Name,
                StartTime = DateTime.Now
            };

            var stepWatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"🔧 Executing step: {step.Name} using {step.ToolName}");

                if (!_tools.TryGetValue(step.ToolName, out var tool))
                {
                    throw new ToolExecutionException(
                        step.ToolName,
                        $"Tool '{step.ToolName}' not found in registry"
                    );
                }

                var toolRequest = new ToolRequest
                {
                    Query = context.State.ContainsKey("query")
                        ? context.State["query"].ToString() ?? ""
                        : "",
                    Context = context,
                    Parameters = step.Parameters
                };

                var toolResult = await tool.ExecuteAsync(toolRequest);

                stepResult.Success = toolResult.Success;
                stepResult.Data = toolResult.Data;
                stepResult.Confidence = toolResult.Confidence;
                stepResult.ErrorMessage = toolResult.ErrorMessage;
                stepResult.Metadata = toolResult.Metadata;

                stepWatch.Stop();
                stepResult.Duration = stepWatch.Elapsed;
                stepResult.EndTime = DateTime.Now;

                _decisionLogger.LogDecision(new AgentDecision
                {
                    Phase = $"Step_{step.Name}",
                    DecisionMade = toolResult.Success ? "Success" : "Failed",
                    Reasoning = toolResult.ErrorMessage ?? "Completed successfully",
                    Confidence = toolResult.Confidence
                });

                return stepResult;
            }
            catch (Exception ex)
            {
                stepWatch.Stop();
                _logger.LogError(ex, $"Step execution failed: {step.Name}");

                stepResult.Success = false;
                stepResult.ErrorMessage = ex.Message;
                stepResult.Duration = stepWatch.Elapsed;
                stepResult.EndTime = DateTime.Now;

                return stepResult;
            }
        }
    }

    public class ExecutionResult
    {
        public ExecutionPlan Plan { get; set; } = new();
        public ExecutionStatus Status { get; set; }
        public List<StepResult> StepResults { get; set; } = new();
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class StepResult
    {
        public string StepName { get; set; } = "";
        public bool Success { get; set; }
        public object? Data { get; set; }
        public double Confidence { get; set; }
        public string? ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public enum ExecutionStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled
    }
}