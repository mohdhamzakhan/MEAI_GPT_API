namespace MEAI_GPT_API.Service
{
    public class InternetSearchOptions
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "SearxNG";
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int MaxResults { get; set; } = 5;
        public int TimeoutSeconds { get; set; } = 10;
        public double MinPolicyScore { get; set; } = 0.35;
        public List<string> BlockedTerms { get; set; } = new();
    }

    public record WebResult(string Title, string Url, string Snippet);

    public interface IWebSearchService
    {
        bool IsEnabled { get; }
        Task<List<WebResult>> SearchAsync(string query, CancellationToken ct = default);
    }
}
