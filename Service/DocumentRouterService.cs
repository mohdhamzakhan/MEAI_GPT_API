using MEAI_GPT_API.Services;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service
{
    // Replaces the scaling problem of TopicAnchorService (a hand-written
    // trigger-phrase list per document -- doesn't scale to 130+ policies,
    // since it requires a human to predict every real phrasing of every
    // document, forever). Instead: fetch the CURRENT full document list live
    // (so it self-updates as documents are added/removed, no config file to
    // maintain), and ask one fast, cheap model call "which 1-2 of these
    // titles is this question actually about?" per query. This scales to
    // any number of documents automatically.
    //
    // Fail-open by design: any failure here (model unavailable, malformed
    // response, ChromaDB error) returns an empty list rather than throwing --
    // this is a supplementary retrieval boost, not a required dependency.
    // The normal embedding search still runs regardless of whether this
    // finds anything.
    public class DocumentRouterService
    {
        private readonly HttpClient _chromaClient;
        private readonly HttpClient _ollamaClient;
        private readonly DynamicCollectionManager _collectionManager;
        private readonly IConfiguration _config;
        private readonly ILogger<DocumentRouterService> _logger;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
        private readonly ConcurrentDictionary<string, (DateTime CachedAt, List<(string SourceFile, string Title)> Docs)> _cache = new();

        // Tags that mean "visible regardless of which plant is asking" --
        // mirrors the metadata["plant"] values assigned during indexing in
        // DynamicRagService (see the ProcessFileForModelAsync area: "context",
        // "centralized", "general", "additional_source"). Keep in sync if
        // that assignment logic changes.
        private static readonly HashSet<string> GlobalPlantTags =
            new(StringComparer.OrdinalIgnoreCase) { "context", "centralized", "general", "additional_source" };

        public DocumentRouterService(
            IHttpClientFactory httpClientFactory,
            DynamicCollectionManager collectionManager,
            IConfiguration config,
            ILogger<DocumentRouterService> logger)
        {
            _chromaClient = httpClientFactory.CreateClient("ChromaDB");
            _ollamaClient = httpClientFactory.CreateClient("OllamaAPI");
            _collectionManager = collectionManager;
            _config = config;
            _logger = logger;
        }

        // Returns document TITLE TERMS (e.g. "Due Settlement Policy"), not
        // filenames, for 0-2 documents the router model thinks the query is
        // about -- callers fold these into an anchored search the same way
        // TopicAnchorService's static entries are used. Empty list means
        // "no confident match" or "router unavailable", never an error the
        // caller needs to handle specially.
        public async Task<List<string>> RouteAsync(string query, string plant, string embeddingModelName)
        {
            var enabled = _config.GetValue<bool>("DocumentRouter:Enabled", true);
            if (!enabled || string.IsNullOrWhiteSpace(query)) return new List<string>();

            try
            {
                var docs = await GetCachedDocumentsAsync(plant, embeddingModelName);
                if (docs.Count == 0) return new List<string>();

                var routerModel = _config.GetValue<string>("DocumentRouter:Model", "llama3.2:1b");
                var maxCandidates = _config.GetValue<int>("DocumentRouter:MaxDocumentsInPrompt", 200);

                // Defensive cap: an extremely large corpus could make the
                // prompt unreasonably long for a small fast model. If this
                // ever needs to be hit in practice, revisit with a two-stage
                // approach (coarse category first, then title) rather than
                // just silently truncating the list.
                var candidateDocs = docs.Take(maxCandidates).ToList();

                var prompt = BuildRouterPrompt(candidateDocs, query);

                var requestBody = new
                {
                    model = routerModel,
                    prompt = prompt,
                    stream = false,
                    think = false, // see SelfVerifier.cs -- Qwen3-family "thinking" burns the token budget before answering; harmless no-op for non-reasoning models like llama3.2
                    options = new { temperature = 0.0, num_predict = 20 }
                };

                var response = await _ollamaClient.PostAsJsonAsync("/api/generate", requestBody);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Document router call returned {StatusCode} — skipping router anchor for this query", response.StatusCode);
                    return new List<string>();
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var parsedResponse = JsonDocument.Parse(responseJson);
                var rawAnswer = parsedResponse.RootElement.TryGetProperty("response", out var respProp)
                    ? respProp.GetString() ?? ""
                    : "";

                if (rawAnswer.Contains("none", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(rawAnswer, @"\d"))
                {
                    return new List<string>();
                }

                var allNumbers = Regex.Matches(rawAnswer, @"\d+")
    .Select(m => int.Parse(m.Value))
    .ToList();

                // ✅ NEW: if the model ignored the "1-2 numbers only" instruction and
                // listed many numbers instead (seen in practice: 7+ numbers from
                // llama3.2:1b on a single query), that's a clear sign of router failure,
                // not a real answer — trusting the first 2 in that noise picks
                // essentially arbitrary documents. Fail open (empty list) instead.
                if (allNumbers.Count > 3)
                {
                    _logger.LogWarning(
                        "Document router returned {Count} numbers (expected 1-2) — treating as failure, raw output: '{Raw}'",
                        allNumbers.Count, rawAnswer.Trim());
                    return new List<string>();
                }

                var indices = allNumbers
    .Where(n => n >= 1 && n <= candidateDocs.Count)
    .Distinct()
    .Take(2)
    .ToList();

                var titles = indices.Select(i => candidateDocs[i - 1].Title).ToList();

                if (titles.Any())
                {
                    _logger.LogInformation(
                        "Document router matched {Titles} for query '{Query}' (raw model output: '{Raw}')",
                        string.Join(", ", titles), query, rawAnswer.Trim());
                }

                return titles;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Document router failed — continuing without router anchor for this query");
                return new List<string>();
            }
        }

        private static string BuildRouterPrompt(List<(string SourceFile, string Title)> docs, string query)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Below is a numbered list of company policy document titles, followed by an employee's question.");
            sb.AppendLine("Reply with ONLY the numbers of the 1-2 documents most likely to contain the answer, comma-separated (e.g. \"5,12\").");
            sb.AppendLine("If none of the titles seem relevant, reply with exactly: none");
            sb.AppendLine();
            for (int i = 0; i < docs.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {docs[i].Title}");
            }
            sb.AppendLine();
            sb.AppendLine($"Question: {query}");
            sb.AppendLine("Answer (numbers only, or \"none\"):");
            return sb.ToString();
        }

        private async Task<List<(string SourceFile, string Title)>> GetCachedDocumentsAsync(string plant, string embeddingModelName)
        {
            var cacheKey = $"{plant}::{embeddingModelName}";
            if (_cache.TryGetValue(cacheKey, out var cached) && (DateTime.UtcNow - cached.CachedAt) < CacheDuration)
            {
                return cached.Docs;
            }

            var distinct = await GetDistinctSourceFilesAsync(embeddingModelName);

            var filtered = distinct
                .Where(d => string.Equals(d.Plant, plant, StringComparison.OrdinalIgnoreCase) || GlobalPlantTags.Contains(d.Plant))
                .Select(d => (d.SourceFile, Title: ExtractDocumentTitleTerms(d.SourceFile)))
                .Where(d => !string.IsNullOrWhiteSpace(d.Title))
                .DistinctBy(d => d.Title)
                .ToList();

            _cache[cacheKey] = (DateTime.UtcNow, filtered);
            _logger.LogInformation("Document router cache refreshed for plant '{Plant}': {Count} candidate documents", plant, filtered.Count);
            return filtered;
        }

        // Kept in sync BY HAND with DynamicRagService's private
        // ExtractDocumentTitleTerms and the Python port in
        // regression_suite/generate_smoke_tests.py. All three must agree on
        // the filename convention, or titles will silently drift apart
        // between what generation sees, what the regression suite tests,
        // and what the router matches against.
        private static string ExtractDocumentTitleTerms(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";

            var name = System.IO.Path.GetFileNameWithoutExtension(fileName);

            var underscoreIdx = name.IndexOf('_');
            if (underscoreIdx >= 0 && underscoreIdx < 8)
            {
                name = name[(underscoreIdx + 1)..];
            }

            var parenIdx = name.IndexOf('(');
            if (parenIdx >= 0)
            {
                name = name[..parenIdx];
            }

            return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        // Independent copy of DynamicRagService's GetDistinctSourceFilesAsync.
        // NOT called via IRAGService, because DynamicRagService (the only
        // implementation of IRAGService) would need to inject this class to
        // use the router -- and if this class also depended on IRAGService,
        // that's a circular DI dependency .NET's container will refuse to
        // construct. Keep this in sync with DynamicRagService's version if
        // the ChromaDB /get request or response contract ever changes.
        private async Task<List<(string SourceFile, string Plant)>> GetDistinctSourceFilesAsync(string model, int limit = 20000)
        {
            var collectionId = _collectionManager.GetCollectionId(model);
            if (string.IsNullOrWhiteSpace(collectionId))
            {
                throw new InvalidOperationException($"No collection found for model '{model}'.");
            }

            var url =
                $"/api/v2/tenants/default_tenant" +
                $"/databases/default_database" +
                $"/collections/{Uri.EscapeDataString(collectionId)}/get";

            var requestBody = new { include = new[] { "metadatas" }, limit = Math.Clamp(limit, 1, 50000) };

            var response = await _chromaClient.PostAsJsonAsync(url, requestBody);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ChromaDB request failed. StatusCode={response.StatusCode}, Response={responseContent}");
            }

            using var parsed = JsonDocument.Parse(responseContent);
            var distinct = new List<(string SourceFile, string Plant)>();
            var seen = new HashSet<(string, string)>();

            if (parsed.RootElement.TryGetProperty("metadatas", out var metadatasProp) && metadatasProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var meta in metadatasProp.EnumerateArray())
                {
                    var source = meta.TryGetProperty("source_file", out var s) ? s.GetString() ?? "" : "";
                    var plantTag = meta.TryGetProperty("plant", out var p) ? p.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(source)) continue;

                    if (seen.Add((source, plantTag)))
                    {
                        distinct.Add((source, plantTag));
                    }
                }
            }

            return distinct;
        }
    }
}