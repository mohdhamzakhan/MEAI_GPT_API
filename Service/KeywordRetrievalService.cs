// Services/KeywordRetrievalService.cs
//
// Second candidate source for retrieval: exact-phrase search over chunk TEXT using
// Chroma's where_document filter. Embeddings miss queries like
// "compliance reminder session form" when the phrase is a small part of a large chunk;
// a literal phrase match finds it. Each hit is scored with a REAL cosine similarity
// (computed from the chunk's stored embedding), so downstream thresholds, the
// coverage gate and the reranker keep working on the same scale as normal hits.
//
// Adjust the `using` lines if your namespaces differ (ModelConfiguration, ChromaDbOptions,
// DynamicCollectionManager).

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Models;
using MEAIGPTAPI.Services;
using Microsoft.Extensions.Options;

namespace MEAI_GPT_API.Services
{
    public class KeywordRetrievalService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ChromaDbOptions _chroma;
        private readonly DynamicCollectionManager _collections;
        private readonly ILogger<KeywordRetrievalService> _logger;

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            // grammar
            "a","an","the","is","are","am","was","were","be","been","do","does","did","you","your","i","my","me",
            "we","our","us","of","in","on","at","to","for","and","or","if","it","its","this","that","these","those",
            "which","what","who","whom","whose","when","where","why","how","please","can","could","would","should",
            "will","shall","may","might","have","has","had","with","without","about","under","from","into","than",
            "then","there","their","them","they","any","some","all","not","no","yes","tell","give","show","explain",
            "need","want","know","get","got","also","more","much","many","per","via",
            // too generic to be useful as a phrase on their own
            "policy","policies","document","documents","scheme","company","employee","employees","mei","meai"
        };

        public KeywordRetrievalService(
            IHttpClientFactory httpClientFactory,
            IOptions<ChromaDbOptions> chromaOptions,
            DynamicCollectionManager collections,
            ILogger<KeywordRetrievalService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _chroma = chromaOptions.Value;
            _collections = collections;
            _logger = logger;
        }

        private sealed record Hit(
            string Id, string Text, string SourceFile, string? Plant, string? SectionNumber,
            string? SectionTitle, string? AnnexureRefs, int EffectiveDate, List<float>? Embedding);

        /// <param name="buildWhere">
        /// Pass DynamicRagService.BuildWhere: (plantFilter, applyEligibility) => where-clause.
        /// Plant chunks keep the grade/category check; Centralized/context chunks are exempt.
        /// </param>
        public async Task<List<RelevantChunk>> SearchAsync(
            string query,
            List<float> queryEmbedding,
            ModelConfiguration embeddingModel,
            string plant,
            Func<object, bool, Dictionary<string, object>> buildWhere,
            int maxResults = 8,
            double minSimilarity = 0.30,
            CancellationToken ct = default)
        {
            var output = new List<RelevantChunk>();

            try
            {
                var phrases = ExtractPhrases(query);
                if (phrases.Count == 0 || queryEmbedding == null || queryEmbedding.Count == 0)
                    return output;

                var collectionId = await _collections.GetOrCreateCollectionAsync(embeddingModel);
                var normalizedPlant = plant.Trim().ToLowerInvariant();
                var client = _httpClientFactory.CreateClient("ChromaDB");
                var url = $"/api/v2/tenants/{_chroma.Tenant}/databases/{_chroma.Database}/collections/{collectionId}/get";

                var scopes = new List<Dictionary<string, object>>
                {
                    buildWhere(normalizedPlant, true),
                    buildWhere(new Dictionary<string, object> { { "$in", new[] { "centralized", "context" } } }, false)
                };

                var hits = new ConcurrentDictionary<string, Hit>();
                using var gate = new SemaphoreSlim(4);
                var tasks = new List<Task>();

                foreach (var phrase in phrases)
                {
                    foreach (var scope in scopes)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync(ct);
                            try
                            {
                                var variants = Variants(phrase);
                                var whereDoc = new Dictionary<string, object>
                                {
                                    ["$or"] = variants
                                        .Select(v => new Dictionary<string, object> { ["$contains"] = v })
                                        .ToList()
                                };

                                var body = new Dictionary<string, object>
                                {
                                    ["where"] = scope,
                                    ["where_document"] = whereDoc,
                                    ["include"] = new[] { "documents", "metadatas", "embeddings" },
                                    ["limit"] = 30
                                };

                                using var response = await client.PostAsJsonAsync(url, body, ct);
                                if (!response.IsSuccessStatusCode)
                                {
                                    var err = await response.Content.ReadAsStringAsync(ct);
                                    _logger.LogWarning("Keyword search failed for '{Phrase}': {Status} {Err}",
                                        phrase, response.StatusCode, err.Length > 200 ? err[..200] : err);
                                    return;
                                }

                                ParseInto(await response.Content.ReadAsStringAsync(ct), hits);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Keyword search error for phrase '{Phrase}'", phrase);
                            }
                            finally { gate.Release(); }
                        }, ct));
                    }
                }

                await Task.WhenAll(tasks);

                var queryTokenCount = Math.Max(3, Regex.Matches(query.ToLowerInvariant(), "[a-z0-9]+")
                    .Count(m => !Stop.Contains(m.Value) && m.Value.Length > 2));
                var today = DateTime.Today;

                foreach (var hit in hits.Values)
                {
                    var lowerText = hit.Text.ToLowerInvariant();
                    var matched = phrases.Where(p => lowerText.Contains(p)).ToList();
                    if (matched.Count == 0) continue;

                    var similarity = hit.Embedding is { Count: > 0 } && hit.Embedding.Count == queryEmbedding.Count
                        ? Cosine(queryEmbedding, hit.Embedding)
                        : 0.0;
                    if (similarity < minSimilarity) continue;   // generic words matching unrelated text

                    var weight = matched.Sum(p => p.Split(' ').Length);
                    var lexical = Math.Min(1.0, weight / (double)queryTokenCount);

                    var isShared = string.Equals(hit.Plant, "centralized", StringComparison.OrdinalIgnoreCase);
                    var isContext = string.Equals(hit.Plant, "context", StringComparison.OrdinalIgnoreCase);

                    output.Add(new RelevantChunk
                    {
                        Id = hit.Id,
                        Text = hit.Text,
                        Source = Path.GetFileName(hit.SourceFile),
                        Similarity = similarity,
                        RelevanceScore = similarity + 0.35 * lexical + FreshnessScorer.Boost(hit.EffectiveDate, today),
                        PolicyType = isShared ? "Centralized Policy" : isContext ? "Context Information" : "Plant Policy",
                        PlantTag = hit.Plant,
                        SectionNumber = hit.SectionNumber,
                        SectionTitle = hit.SectionTitle,
                        AnnexureRefs = hit.AnnexureRefs,
                        EffectiveDate = hit.EffectiveDate
                    });
                }

                output = output
                    .OrderByDescending(c => c.RelevanceScore)
                    .Take(maxResults)
                    .ToList();

                _logger.LogInformation(
                    "🔤 Keyword retrieval: phrases=[{Phrases}] rawHits={Raw} kept={Kept} -> {Sources}",
                    string.Join(" | ", phrases), hits.Count, output.Count,
                    string.Join(", ", output.Select(o => o.Source).Distinct()));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // never block the main retrieval path
                _logger.LogWarning(ex, "Keyword retrieval failed — continuing without it");
            }

            return output;
        }

        // ── phrase extraction ───────────────────────────────────────────────────
        // Splits the query at stop words/punctuation so phrases are real adjacent words,
        // e.g. "compliance reminder session form under which policy?" ->
        //   "compliance reminder session form", "compliance reminder session",
        //   "reminder session form", "compliance reminder", "reminder session", "session form"
        public static List<string> ExtractPhrases(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();

            var cut = query.IndexOf("(context:", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) query = query[..cut];

            var segments = new List<List<string>>();
            var current = new List<string>();

            void Flush()
            {
                if (current.Count > 0) segments.Add(current);
                current = new List<string>();
            }

            foreach (Match m in Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9&]+|[^\sa-z0-9&]"))
            {
                var w = m.Value;
                var isWord = Regex.IsMatch(w, "^[a-z0-9&]+$");
                var tooShort = w.Length < 3 && !Regex.IsMatch(w, @"^\d+$");

                if (!isWord || Stop.Contains(w) || tooShort) Flush();
                else current.Add(w);
            }
            Flush();

            var phrases = new List<string>();
            foreach (var seg in segments)
            {
                if (seg.Count == 1)
                {
                    if (seg[0].Length >= 9) phrases.Add(seg[0]);   // distinctive single word
                    continue;
                }

                for (var size = Math.Min(seg.Count, 4); size >= 2; size--)
                    for (var start = 0; start + size <= seg.Count; start++)
                        phrases.Add(string.Join(' ', seg.Skip(start).Take(size)));
            }

            return phrases
                .Distinct()
                .OrderByDescending(p => p.Split(' ').Length)
                .ThenByDescending(p => p.Length)
                .Take(6)
                .ToList();
        }

        // Chroma's $contains is case-sensitive. Chunk text has Title Case in the body and
        // lowercase in the keyword line, so try lower, Title and UPPER.
        private static List<string> Variants(string phrase) => new[]
        {
            phrase,
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(phrase),
            phrase.ToUpperInvariant()
        }.Distinct().ToList();

        // ── response parsing ────────────────────────────────────────────────────
        private static void ParseInto(string json, ConcurrentDictionary<string, Hit> hits)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ids = Arr(root, "ids");
            var docs = Arr(root, "documents");
            var metas = Arr(root, "metadatas");
            var embs = Arr(root, "embeddings");

            for (var i = 0; i < ids.Count && i < docs.Count; i++)
            {
                var id = ids[i].GetString();
                var text = docs[i].ValueKind == JsonValueKind.String ? docs[i].GetString() : null;
                if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(text)) continue;

                var meta = i < metas.Count ? metas[i] : default;

                List<float>? embedding = null;
                if (i < embs.Count && embs[i].ValueKind == JsonValueKind.Array)
                    embedding = embs[i].EnumerateArray().Select(x => x.GetSingle()).ToList();

                var effective = 0;
                if (meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("effective_date", out var ed) &&
                    ed.ValueKind == JsonValueKind.Number && ed.TryGetInt32(out var edInt))
                    effective = edInt;

                hits.TryAdd(id, new Hit(
                    id, text!, S(meta, "source_file") ?? "Unknown", S(meta, "plant"),
                    S(meta, "section_number"), S(meta, "section_title"), S(meta, "annexure_refs"),
                    effective, embedding));
            }
        }

        private static List<JsonElement> Arr(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().ToList()
                : new List<JsonElement>();

        private static string? S(JsonElement meta, string key) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static double Cosine(List<float> a, List<float> b)
        {
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Count; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }
}