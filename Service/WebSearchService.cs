using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace MEAI_GPT_API.Service
{
    public class WebSearchService : IWebSearchService
    {
        private readonly IHttpClientFactory _factory;
        private readonly IOptionsMonitor<InternetSearchOptions> _opts;
        private readonly ILogger<WebSearchService> _log;

        public WebSearchService(IHttpClientFactory factory,
            IOptionsMonitor<InternetSearchOptions> opts, ILogger<WebSearchService> log)
        { _factory = factory; _opts = opts; _log = log; }

        public bool IsEnabled => _opts.CurrentValue.Enabled;

        public async Task<List<WebResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            var o = _opts.CurrentValue;
            if (!o.Enabled || string.IsNullOrWhiteSpace(query)) return new();

            // Never send internal/sensitive queries outside the company
            if (o.BlockedTerms.Any(t => query.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                _log.LogInformation("Internet search skipped: blocked term in query");
                return new();
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
                var http = _factory.CreateClient("WebSearch");

                return o.Provider.Equals("Brave", StringComparison.OrdinalIgnoreCase)
                    ? await SearchBrave(http, o, query, cts.Token)
                    : await SearchSearx(http, o, query, cts.Token);
            }
            catch (Exception ex)
            {
                // Non-fatal: the chat still answers without web results
                _log.LogWarning(ex, "Internet search failed");
                return new();
            }
        }

        private static async Task<List<WebResult>> SearchSearx(
            HttpClient http, InternetSearchOptions o, string q, CancellationToken ct)
        {
            var url = $"{o.BaseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(q)}&format=json";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            return doc.RootElement.GetProperty("results").EnumerateArray()
                .Take(o.MaxResults)
                .Select(r => new WebResult(
                    Clean(r.GetProperty("title").GetString()),
                    r.GetProperty("url").GetString() ?? "",
                    Clean(r.TryGetProperty("content", out var c) ? c.GetString() : "")))
                .ToList();
        }

        private static async Task<List<WebResult>> SearchBrave(
            HttpClient http, InternetSearchOptions o, string q, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(q)}&count={o.MaxResults}");
            req.Headers.Add("X-Subscription-Token", o.ApiKey);
            req.Headers.Add("Accept", "application/json");

            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("web").GetProperty("results").EnumerateArray()
                .Take(o.MaxResults)
                .Select(r => new WebResult(
                    Clean(r.GetProperty("title").GetString()),
                    r.GetProperty("url").GetString() ?? "",
                    Clean(r.TryGetProperty("description", out var d) ? d.GetString() : "")))
                .ToList();
        }

        // Strip HTML: the frontend renders messages with dangerouslySetInnerHTML
        private static string Clean(string? s) =>
            WebUtility.HtmlEncode(System.Text.RegularExpressions.Regex.Replace(s ?? "", "<.*?>", ""));
    }
}
