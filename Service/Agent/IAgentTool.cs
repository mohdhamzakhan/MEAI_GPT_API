using MEAI_GPT_API.Models;
using static MEAI_GPT_API.Models.Conversation;

namespace MEAI_GPT_API.Services.Agent
{
    /// <summary>
    /// Base interface for all agent tools
    /// </summary>
    public interface IAgentTool
    {
        string Name { get; }
        string Description { get; }
        int Priority { get; } // Higher = checked first

        /// <summary>
        /// Determines if this tool can handle the given query
        /// </summary>
        Task<bool> CanHandleAsync(string query, AgentContext context);

        /// <summary>
        /// Execute the tool and return structured result
        /// </summary>
        Task<ToolResult> ExecuteAsync(ToolRequest request);

        /// <summary>
        /// Estimate the confidence of handling this query (0-1)
        /// </summary>
        Task<double> EstimateConfidenceAsync(string query, AgentContext context);
    }

    public class ToolRequest
    {
        public string Query { get; set; } = "";
        public AgentContext Context { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class ToolResult
    {
        public bool Success { get; set; }
        public string ToolName { get; set; } = "";
        public object? Data { get; set; }
        public double Confidence { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> Sources { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public long ExecutionTimeMs { get; set; }
    }

    public class AgentContext
    {
        public string SessionId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Plant { get; set; } = "";
        public List<ConversationTurn> History { get; set; } = new();
        public List<string> NamedEntities { get; set; } = new();
        public Dictionary<string, object> State { get; set; } = new();
        public DateTime LastAccessed { get; set; } = DateTime.Now;
    }
}