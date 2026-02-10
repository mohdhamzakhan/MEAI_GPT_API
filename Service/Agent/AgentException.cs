namespace MEAI_GPT_API.Services.Agent
{
    public class AgentException : Exception
    {
        public string AgentPhase { get; set; } = "";
        public Dictionary<string, object> Context { get; set; } = new();

        public AgentException(string message) : base(message) { }

        public AgentException(string message, string phase, Dictionary<string, object>? context = null)
            : base(message)
        {
            AgentPhase = phase;
            Context = context ?? new();
        }

        public AgentException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ToolExecutionException : AgentException
    {
        public string ToolName { get; set; } = "";

        public ToolExecutionException(string toolName, string message)
            : base(message, "ToolExecution")
        {
            ToolName = toolName;
        }
    }

    public class PlanningException : AgentException
    {
        public PlanningException(string message) : base(message, "Planning") { }
    }

    public class VerificationException : AgentException
    {
        public VerificationException(string message) : base(message, "Verification") { }
    }
}