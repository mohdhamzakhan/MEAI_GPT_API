// Services/HybridRetrieval.cs
//
// Step 02 "Hybrid Retrieval" done properly.
//
// The old Bm25Service only re-ranks chunks that embeddings already returned, so a chunk the
// embeddings missed can never be rescued. This file adds:
//
//   ChunkBm25Index          (singleton)  in-memory BM25 index over EVERY chunk in the collection.
//                                        Indexes only the chunk body + section title, not the
//                                        "DOCUMENT TYPE" / "KEYWORDS" header lines (those are noisy,
//                                        e.g. fcpa/ukba tags on a compliance chunk).
//   HybridRetrievalService  (scoped)     takes the vector results, runs BM25 over the whole index,
//                                        adds the BM25-only chunks, and applies Reciprocal Rank
//                                        Fusion as a ranking bonus.
//
// Eligibility is NOT re-implemented here: BM25 candidates are verified through Chroma with the
// same where-clause the vector search uses (BuildWhere), so grade/category rules stay in one place.
// Centralized/context chunks stay exempt from the grade check.
//
// Adjust the `using` lines if your namespaces differ.

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
    // ═══════════════════════════════════════════════════════════════════════════
    //  BM25 index
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class ChunkBm25Index
    {
        private const double K1 = 1.2;
        private const double B = 0.75;
        private const int PageSize = 3000;
        private static readonly TimeSpan CountCheckInterval = TimeSpan.FromMinutes(10);

        private static readonly Regex TokenRx = new("[a-z0-9]+", RegexOptions.Compiled);

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","the","is","are","was","were","be","been","do","does","did","you","your","i","my","me","we",
            "our","us","of","in","on","at","to","for","and","or","if","it","its","this","that","these","those",
            "which","what","who","when","where","why","how","please","can","could","would","should","will","shall",
            "may","with","without","about","under","from","into","than","then","there","their","them","they","any",
            "some","all","not","no","has","have","had","as","by","per"
        };

        public sealed record IndexedDoc(string Id, string BodyLower, string SourceFile, string? Plant, int Length);

        private sealed class Snapshot
        {
            public IndexedDoc[] Docs = Array.Empty<IndexedDoc>();
            public Dictionary<string, List<(int Doc, int Tf)>> Postings = new();
            public double AvgLength = 1;
            public int Count;
            public string CollectionId = "";
            public DateTime BuiltAt;
            public DateTime CheckedAt;
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ChromaDbOptions _chroma;
        private readonly DynamicCollectionManager _collections;
        private readonly ILogger<ChunkBm25Index> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private volatile Snapshot? _snapshot;

        public ChunkBm25Index(
            IHttpClientFactory httpClientFactory,
            IOptions<ChromaDbOptions> chromaOptions,
            DynamicCollectionManager collections,
            ILogger<ChunkBm25Index> logger)
        {
            _httpClientFactory = httpClientFactory;
            _chroma = chromaOptions.Value;
            _collections = collections;
            _logger = logger;
        }

        public bool IsBuilt => _snapshot != null;
        public int DocumentCount => _snapshot?.Docs.Length ?? 0;

        /// <summary>
        /// Builds the index on first use, then re-checks the collection size at most every 10 minutes
        /// and rebuilds only if it changed. Pass force=true after re-indexing documents.
        /// </summary>
        public async Task EnsureFreshAsync(ModelConfiguration model, bool force = false, CancellationToken ct = default)
        {
            var current = _snapshot;
            if (!force && current != null && DateTime.UtcNow - current.CheckedAt < CountCheckInterval) return;

            await _lock.WaitAsync(ct);
            try
            {
                current = _snapshot;
                if (!force && current != null && DateTime.UtcNow - current.CheckedAt < CountCheckInterval) return;

                var collectionId = await _collections.GetOrCreateCollectionAsync(model);
                var count = await CountAsync(collectionId, ct);

                if (!force && current != null && current.Count == count && current.CollectionId == collectionId)
                {
                    current.CheckedAt = DateTime.UtcNow;
                    return;
                }

                _snapshot = await BuildAsync(collectionId, count, ct);
            }
            finally { _lock.Release(); }
        }

        private async Task<int> CountAsync(string collectionId, CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient("ChromaDB");
            var url = $"/api/v2/tenants/{_chroma.Tenant}/databases/{_chroma.Database}/collections/{collectionId}/count";
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return int.TryParse(text, out var n) ? n : -1;
        }

        private async Task<Snapshot> BuildAsync(string collectionId, int expectedCount, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var client = _httpClientFactory.CreateClient("ChromaDB");
            var url = $"/api/v2/tenants/{_chroma.Tenant}/databases/{_chroma.Database}/collections/{collectionId}/get";

            var docs = new List<IndexedDoc>();
            var tokenLists = new List<List<string>>();

            for (var offset = 0; ; offset += PageSize)
            {
                var body = new Dictionary<string, object>
                {
                    ["include"] = new[] { "documents", "metadatas" },
                    ["limit"] = PageSize,
                    ["offset"] = offset
                };

                using var response = await client.PostAsJsonAsync(url, body, ct);
                response.EnsureSuccessStatusCode();

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = json.RootElement;
                var ids = Arr(root, "ids");
                var texts = Arr(root, "documents");
                var metas = Arr(root, "metadatas");

                for (var i = 0; i < ids.Count && i < texts.Count; i++)
                {
                    var id = ids[i].GetString();
                    var text = texts[i].ValueKind == JsonValueKind.String ? texts[i].GetString() : null;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(text)) continue;

                    var meta = i < metas.Count ? metas[i] : default;
                    var (title, bodyText) = Split(text!);

                    var tokens = Tokenize(bodyText);
                    var titleTokens = Tokenize(title);
                    tokens.AddRange(titleTokens);
                    tokens.AddRange(titleTokens);   // title counts double

                    docs.Add(new IndexedDoc(
                        id!, bodyText.ToLowerInvariant(), Str(meta, "source_file") ?? "Unknown",
                        Str(meta, "plant")?.ToLowerInvariant(), tokens.Count));
                    tokenLists.Add(tokens);
                }

                if (ids.Count < PageSize) break;
            }

            var postings = new Dictionary<string, List<(int Doc, int Tf)>>();
            for (var d = 0; d < tokenLists.Count; d++)
            {
                foreach (var group in tokenLists[d].GroupBy(t => t))
                {
                    if (!postings.TryGetValue(group.Key, out var list))
                        postings[group.Key] = list = new List<(int, int)>();
                    list.Add((d, group.Count()));
                }
            }

            var snapshot = new Snapshot
            {
                Docs = docs.ToArray(),
                Postings = postings,
                AvgLength = docs.Count == 0 ? 1 : Math.Max(1, docs.Average(x => x.Length)),
                Count = expectedCount >= 0 ? expectedCount : docs.Count,
                CollectionId = collectionId,
                BuiltAt = DateTime.UtcNow,
                CheckedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "📚 BM25 index built: {Docs} chunks, {Terms} terms, avg length {Avg:F0} tokens in {Ms} ms",
                snapshot.Docs.Length, postings.Count, snapshot.AvgLength,
                (int)(DateTime.UtcNow - started).TotalMilliseconds);

            return snapshot;
        }

        /// <summary>BM25 search restricted to chunks whose plant tag is in <paramref name="plants"/>.</summary>
        public List<(IndexedDoc Doc, double Score)> Search(string query, ISet<string> plants, int topK)
        {
            var snap = _snapshot;
            if (snap == null || snap.Docs.Length == 0 || string.IsNullOrWhiteSpace(query))
                return new List<(IndexedDoc, double)>();

            var cut = query.IndexOf("(context:", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) query = query[..cut];

            var ordered = Tokenize(query, stem: false);              // for phrase bonus
            var terms = Tokenize(query).Distinct().ToList();         // stemmed, for postings
            if (terms.Count == 0) return new List<(IndexedDoc, double)>();

            var scores = new Dictionary<int, double>();
            var n = snap.Docs.Length;

            foreach (var term in terms)
            {
                if (!snap.Postings.TryGetValue(term, out var list)) continue;

                var df = list.Count;
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));

                foreach (var (docIdx, tf) in list)
                {
                    var doc = snap.Docs[docIdx];
                    if (doc.Plant == null || !plants.Contains(doc.Plant)) continue;

                    var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * doc.Length / snap.AvgLength));
                    scores[docIdx] = scores.GetValueOrDefault(docIdx) + idf * norm;
                }
            }

            if (scores.Count == 0) return new List<(IndexedDoc, double)>();

            // exact adjacent-word bonus among the best candidates ("compliance reminder")
            var bigrams = new List<string>();
            for (var i = 0; i + 1 < ordered.Count; i++) bigrams.Add($"{ordered[i]} {ordered[i + 1]}");

            var candidates = scores.OrderByDescending(kv => kv.Value).Take(topK * 3).ToList();
            var rescored = new List<(IndexedDoc, double)>();
            foreach (var (docIdx, score) in candidates)
            {
                var doc = snap.Docs[docIdx];
                var bonus = bigrams.Count(b => doc.BodyLower.Contains(b)) * 1.5;
                rescored.Add((doc, score + bonus));
            }

            return rescored.OrderByDescending(r => r.Item2).Take(topK).ToList();
        }

        // ── text helpers ────────────────────────────────────────────────────────
        // A chunk looks like:
        //   DOCUMENT SECTION: ... / SECTION TITLE: ... / DOCUMENT TYPE: ... / CONTENT TYPE: ...
        //   ===== CONTENT =====  <body>  ===== KEYWORDS =====  <keyword list>
        private static (string Title, string Body) Split(string text)
        {
            var title = "";
            var m = Regex.Match(text, @"SECTION TITLE:[ \t]*(.*)");
            if (m.Success) title = m.Groups[1].Value.Trim();

            const string contentMarker = "===== CONTENT =====";
            const string keywordMarker = "===== KEYWORDS =====";

            var start = text.IndexOf(contentMarker, StringComparison.Ordinal);
            if (start < 0) return (title, text);

            start += contentMarker.Length;
            var end = text.IndexOf(keywordMarker, start, StringComparison.Ordinal);
            return (title, end > start ? text[start..end] : text[start..]);
        }

        private static List<string> Tokenize(string text, bool stem = true)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (Match m in TokenRx.Matches(text.ToLowerInvariant()))
            {
                var w = m.Value;
                if (w.Length < 2 || Stop.Contains(w)) continue;
                result.Add(stem ? Stem(w) : w);
            }
            return result;
        }

        // deliberately light: plural 's' only, so "forms"/"form", "sessions"/"session" match
        private static string Stem(string w) =>
            w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w;

        private static List<JsonElement> Arr(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().ToList()
                : new List<JsonElement>();

        private static string? Str(JsonElement meta, string key) =>
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Hybrid fusion
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class HybridRetrievalService
    {
        private const int RrfK = 20;                 // small lists (<= ~60), so a sharper k than the usual 60
        private const double FusionWeight = 0.30;    // max ranking bonus added to RelevanceScore
        private static readonly string[] ExemptTags = { "centralized", "context" };

        private readonly ChunkBm25Index _index;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ChromaDbOptions _chroma;
        private readonly DynamicCollectionManager _collections;
        private readonly ILogger<HybridRetrievalService> _logger;

        public HybridRetrievalService(
            ChunkBm25Index index,
            IHttpClientFactory httpClientFactory,
            IOptions<ChromaDbOptions> chromaOptions,
            DynamicCollectionManager collections,
            ILogger<HybridRetrievalService> logger)
        {
            _index = index;
            _httpClientFactory = httpClientFactory;
            _chroma = chromaOptions.Value;
            _collections = collections;
            _logger = logger;
        }

        private sealed record Verified(string Id, string Text, string SourceFile, string? Plant,
            string? SectionNumber, string? SectionTitle, string? AnnexureRefs, int EffectiveDate, List<float>? Embedding);

        /// <summary>
        /// Returns the vector chunks plus BM25-only chunks, all with a fusion bonus on RelevanceScore.
        /// Never throws: on any failure the original vector chunks are returned unchanged.
        /// </summary>
        public async Task<List<RelevantChunk>> AugmentAsync(
            string query,
            List<float> queryEmbedding,
            List<RelevantChunk> vectorChunks,
            ModelConfiguration embeddingModel,
            string plant,
            Func<object, bool, Dictionary<string, object>> buildWhere,
            int bm25TopK = 30,
            double minSimilarityForBm25Only = 0.30,
            CancellationToken ct = default)
        {
            try
            {
                await _index.EnsureFreshAsync(embeddingModel, ct: ct);

                var normalizedPlant = plant.Trim().ToLowerInvariant();
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { normalizedPlant, "centralized", "context", "general", "additional_source" };

                var hits = _index.Search(query, tags, bm25TopK);
                if (hits.Count == 0) return vectorChunks;

                // Verify candidates through Chroma with the SAME eligibility rules as vector search.
                var collectionId = await _collections.GetOrCreateCollectionAsync(embeddingModel);

                var plantIds = hits.Where(h => !ExemptTags.Contains(h.Doc.Plant ?? "")).Select(h => h.Doc.Id).ToList();
                var exemptIds = hits.Where(h => ExemptTags.Contains(h.Doc.Plant ?? "")).Select(h => h.Doc.Id).ToList();

                var verified = new Dictionary<string, Verified>();
                if (plantIds.Count > 0)
                    await FetchAsync(collectionId, plantIds,
                        buildWhere(new Dictionary<string, object> { { "$in", new[] { normalizedPlant, "general", "additional_source" } } }, true),
                        verified, ct);
                if (exemptIds.Count > 0)
                    await FetchAsync(collectionId, exemptIds,
                        buildWhere(new Dictionary<string, object> { { "$in", ExemptTags } }, false),
                        verified, ct);

                // rankings
                var vectorRank = vectorChunks
                    .GroupBy(c => c.Text)
                    .Select(g => g.OrderByDescending(c => c.Similarity).First())
                    .OrderByDescending(c => c.Similarity)
                    .Select((c, i) => (c.Text, Rank: i + 1))
                    .ToDictionary(x => x.Text, x => x.Rank);

                var bm25Rank = new Dictionary<string, int>();   // chunk text -> rank among verified hits
                var bm25Chunks = new List<(Verified V, double Score)>();
                foreach (var (doc, score) in hits)
                {
                    if (!verified.TryGetValue(doc.Id, out var v)) continue;   // filtered out by eligibility
                    bm25Chunks.Add((v, score));
                }
                for (var i = 0; i < bm25Chunks.Count; i++)
                    bm25Rank.TryAdd(bm25Chunks[i].V.Text, i + 1);

                var today = DateTime.Today;
                var result = new List<RelevantChunk>(vectorChunks);
                var existingTexts = new HashSet<string>(vectorChunks.Select(c => c.Text));
                var added = 0;

                foreach (var (v, _) in bm25Chunks)
                {
                    if (existingTexts.Contains(v.Text)) continue;

                    var similarity = v.Embedding is { Count: > 0 } && v.Embedding.Count == queryEmbedding.Count
                        ? Cosine(queryEmbedding, v.Embedding) : 0.0;
                    if (similarity < minSimilarityForBm25Only) continue;

                    var shared = string.Equals(v.Plant, "centralized", StringComparison.OrdinalIgnoreCase);
                    var context = string.Equals(v.Plant, "context", StringComparison.OrdinalIgnoreCase);

                    result.Add(new RelevantChunk
                    {
                        Id = v.Id,
                        Text = v.Text,
                        Source = Path.GetFileName(v.SourceFile),
                        Similarity = similarity,
                        RelevanceScore = similarity + FreshnessScorer.Boost(v.EffectiveDate, today),
                        PolicyType = shared ? "Centralized Policy" : context ? "Context Information" : "Plant Policy",
                        PlantTag = v.Plant,
                        SectionNumber = v.SectionNumber,
                        SectionTitle = v.SectionTitle,
                        AnnexureRefs = v.AnnexureRefs,
                        EffectiveDate = v.EffectiveDate
                    });
                    existingTexts.Add(v.Text);
                    added++;
                }

                // Reciprocal Rank Fusion as a bonus on the existing score (keeps plant/intent boosts intact).
                var maxRrf = 2.0 / (RrfK + 1);
                foreach (var c in result)
                {
                    var rrf = 0.0;
                    if (vectorRank.TryGetValue(c.Text, out var vr)) rrf += 1.0 / (RrfK + vr);
                    if (bm25Rank.TryGetValue(c.Text, out var br)) rrf += 1.0 / (RrfK + br);
                    c.RelevanceScore += FusionWeight * rrf / maxRrf;
                }

                var overlap = bm25Rank.Keys.Count(vectorRank.ContainsKey);
                _logger.LogInformation(
                    "🔀 Hybrid: vector={V} bm25={B} (verified {Ver}) overlap={O} bm25-only added={A} -> {Sources}",
                    vectorRank.Count, hits.Count, bm25Chunks.Count, overlap, added,
                    string.Join(", ", result.Where(c => !vectorRank.ContainsKey(c.Text)).Select(c => c.Source).Distinct()));

                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hybrid retrieval failed — using vector results only");
                return vectorChunks;
            }
        }

        // Chroma /get with ids + where returns only the ids that also satisfy the where-clause.
        private async Task FetchAsync(
            string collectionId, List<string> ids, Dictionary<string, object> where,
            Dictionary<string, Verified> sink, CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient("ChromaDB");
            var url = $"/api/v2/tenants/{_chroma.Tenant}/databases/{_chroma.Database}/collections/{collectionId}/get";

            var body = new Dictionary<string, object>
            {
                ["ids"] = ids,
                ["where"] = where,
                ["include"] = new[] { "documents", "metadatas", "embeddings" }
            };

            using var response = await client.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Hybrid verify failed: {Status} {Err}", response.StatusCode, err.Length > 200 ? err[..200] : err);
                return;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;

            List<JsonElement> Arr(string n) =>
                root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.Array
                    ? el.EnumerateArray().ToList() : new List<JsonElement>();

            var rid = Arr("ids"); var docs = Arr("documents"); var metas = Arr("metadatas"); var embs = Arr("embeddings");

            for (var i = 0; i < rid.Count && i < docs.Count; i++)
            {
                var id = rid[i].GetString();
                var text = docs[i].ValueKind == JsonValueKind.String ? docs[i].GetString() : null;
                if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(text)) continue;

                var meta = i < metas.Count ? metas[i] : default;
                string? S(string k) => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var effective = 0;
                if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("effective_date", out var ed) &&
                    ed.ValueKind == JsonValueKind.Number && ed.TryGetInt32(out var edInt)) effective = edInt;

                List<float>? emb = null;
                if (i < embs.Count && embs[i].ValueKind == JsonValueKind.Array)
                    emb = embs[i].EnumerateArray().Select(x => x.GetSingle()).ToList();

                sink[id!] = new Verified(id!, text!, S("source_file") ?? "Unknown", S("plant"),
                    S("section_number"), S("section_title"), S("annexure_refs"), effective, emb);
            }
        }

        private static double Cosine(List<float> a, List<float> b)
        {
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Count; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }
}