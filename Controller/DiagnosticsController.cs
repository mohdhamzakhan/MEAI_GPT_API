using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Service.Models;
using MEAI_GPT_API.Services;
using MEAIGPTAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class DiagnosticsController : ControllerBase
    {
        private readonly GradeHierarchyService _gradeHierarchy;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DiagnosticsController> _logger;
        private readonly IRAGService _ragService;
        private readonly DocumentRouterService _documentRouter;
        private readonly IModelManager _modelManager;
        private readonly DynamicCollectionManager _collectionManager;
        private readonly OllamaHttpClient _ollamaClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ChromaDbOptions _chromaOptions;

        public DiagnosticsController(
            GradeHierarchyService gradeHierarchy,
            IConfiguration configuration,
            ILogger<DiagnosticsController> logger,
            IRAGService rAGService,
            DocumentRouterService documentRouter,
            IModelManager modelManager,
            DynamicCollectionManager collectionManager,
            OllamaHttpClient ollamaClient,
            IHttpClientFactory httpClientFactory,
            IOptions<ChromaDbOptions> chromaOptions)
        {
            _gradeHierarchy = gradeHierarchy;
            _configuration = configuration;
            _logger = logger;
            _ragService = rAGService;
            _documentRouter = documentRouter;
            _modelManager = modelManager;
            _collectionManager = collectionManager;
            _ollamaClient = ollamaClient;
            _httpClientFactory = httpClientFactory;
            _chromaOptions = chromaOptions.Value;
        }

        // GET /api/diagnostics/ambiguous-titles
        [HttpGet("ambiguous-titles")]
        public IActionResult GetAmbiguousTitlesAudit()
        {
            var audit = _gradeHierarchy.AuditAmbiguousTitles();
            var missing = audit.Where(a => !a.HasExplicitOverride).ToList();

            return Ok(new
            {
                totalAmbiguousTitles = audit.Count,
                missingOverrides = missing.Count,
                details = audit
            });
        }

        // GET /api/diagnostics/eligibility-nulls
        [HttpGet("eligibility-nulls")]
        public IActionResult GetEligibilityNullAudit()
        {
            var cachePath = _configuration["GradeEligibility:CacheFilePath"]
                            ?? "./context/grade-eligibility-cache.json";

            if (!System.IO.File.Exists(cachePath))
                return NotFound(new { error = $"Cache file not found at {cachePath}" });

            var entries = JsonSerializer.Deserialize<List<ChunkEligibility>>(
                System.IO.File.ReadAllText(cachePath)) ?? new();

            var empty = entries
                .Where(e => e.MinGradeBand is null && e.MaxGradeBand is null && e.EmployeeCategory is null)
                .ToList();

            var byPath = empty
                .GroupBy(e => e.ExtractionPath)
                .Select(g => new { path = g.Key, count = g.Count() })
                .ToList();

            return Ok(new
            {
                totalEntries = entries.Count,
                fullyEmptyEligibility = empty.Count,
                breakdownByPath = byPath,
                // the only bucket actually worth eyeballing:
                llmExtractedButEmpty = empty
                    .Where(e => e.ExtractionPath == "LlmExtracted")
                    .Select(e => new { e.SourceFile, e.ChunkKey, e.ChunkPreview })
                    .Take(50)
            });
        }
        public class RepairEligibilityRequest
        {
            public List<string> SourceFiles { get; set; } = new();
        }

        [HttpPost("repair-eligibility")]
        public async Task<IActionResult> RepairEligibility([FromBody] RepairEligibilityRequest request)
        {
            if (request.SourceFiles == null || !request.SourceFiles.Any())
                return BadRequest(new { error = "Provide sourceFiles: the list of files flagged by the timeout log grep." });

            var results = await _ragService.RepairEligibilityForFilesAsync(request.SourceFiles);
            return Ok(results);
        }

        // GET /api/diagnostics/document-relevance?plant=Sanand&query=what all benefits are there for AM&sourceFileContains=Parking
        //
        // Answers exactly the question "why didn't document X show up for this
        // query" in two parts: (1) was it even offered to the Document Router
        // as a candidate, and (2) what is its RAW embedding similarity against
        // this exact query, bypassing multi-query expansion, reranking, and
        // diversification entirely. A low raw similarity here means the
        // problem is genuinely semantic (the document's content doesn't read
        // as related to the query, no amount of pipeline tuning fixes that on
        // its own) rather than a bug in retrieval plumbing.
        [HttpGet("document-relevance")]
        public async Task<IActionResult> GetDocumentRelevance(
            [FromQuery] string plant,
            [FromQuery] string query,
            [FromQuery] string sourceFileContains,
            [FromQuery] string embeddingModel = "nomic-embed-text:v1.5")
        {
            if (string.IsNullOrWhiteSpace(plant) || string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(sourceFileContains))
                return BadRequest(new { error = "plant, query, and sourceFileContains are all required." });

            var embModel = await _modelManager.GetModelAsync(embeddingModel);
            if (embModel == null)
                return NotFound(new { error = $"Embedding model '{embeddingModel}' not found." });

            var collectionId = await _collectionManager.GetOrCreateCollectionAsync(embModel);

            // Step 1: is it even in the Document Router's candidate pool?
            var candidates = await _documentRouter.GetCandidateDocumentsForDiagnosticsAsync(plant, embeddingModel);
            var routerMatches = candidates
                .Where(c => c.SourceFile.Contains(sourceFileContains, StringComparison.OrdinalIgnoreCase)
                         || c.Title.Contains(sourceFileContains, StringComparison.OrdinalIgnoreCase))
                .Select(c => new { c.SourceFile, c.Title })
                .ToList();

            // Step 2: find the exact source_file value(s) that match, by
            // listing every distinct source file in the collection first --
            // Chroma's where-filters don't support substring match on
            // metadata, only equality, so we resolve the exact value(s)
            // client-side before doing a targeted fetch.
            var listBody = new { limit = 20000, include = new[] { "metadatas" } };
            var listResp = await _chromaClient().PostAsJsonAsync(
                $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/get",
                listBody);
            var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());

            var matchedSourceFiles = new HashSet<string>();
            if (listDoc.RootElement.TryGetProperty("metadatas", out var metasEl))
            {
                foreach (var meta in metasEl.EnumerateArray())
                {
                    if (meta.TryGetProperty("source_file", out var sf))
                    {
                        var sfValue = sf.GetString() ?? "";
                        if (sfValue.Contains(sourceFileContains, StringComparison.OrdinalIgnoreCase))
                            matchedSourceFiles.Add(sfValue);
                    }
                }
            }

            if (!matchedSourceFiles.Any())
            {
                return Ok(new
                {
                    inRouterCandidatePool = routerMatches.Any(),
                    routerCandidateMatches = routerMatches,
                    foundInCollection = false,
                    message = $"No indexed chunk has a source_file containing '{sourceFileContains}' in this collection. " +
                              "Either it was never indexed, or the filename doesn't match what you expect -- check the exact " +
                              "path via the eligibility-nulls or repair-eligibility endpoints, which show real SourceFile values."
                });
            }

            // Step 3: targeted fetch WITH embeddings for just the matched file(s).
            var getBody = new
            {
                where = new Dictionary<string, object>
                {
                    ["source_file"] = new Dictionary<string, object> { ["$in"] = matchedSourceFiles.ToList() }
                },
                include = new[] { "embeddings", "documents", "metadatas" }
            };
            var getResp = await _chromaClient().PostAsJsonAsync(
                $"/api/v2/tenants/{_chromaOptions.Tenant}/databases/{_chromaOptions.Database}/collections/{collectionId}/get",
                getBody);
            using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());

            var docsEl = getDoc.RootElement.GetProperty("documents").EnumerateArray().ToList();
            var embeddingsEl = getDoc.RootElement.GetProperty("embeddings").EnumerateArray().ToList();
            var metaEl2 = getDoc.RootElement.GetProperty("metadatas").EnumerateArray().ToList();

            // Step 4: embed the query the SAME way live retrieval does
            // ("search_query: " prefix -- see GetEmbeddingAsync), then compute
            // real cosine similarity per chunk, no pipeline steps in between.
            var embedRequest = new { model = embModel.Name, prompt = $"search_query: {query}" };
            var embedResp = await _ollamaClient.PostAsJsonAsync("/api/embeddings", embedRequest, HttpContext.RequestAborted, maxRetries: 2, perAttemptTimeout: TimeSpan.FromSeconds(45));
            using var embedDoc = JsonDocument.Parse(await embedResp.Content.ReadAsStringAsync());
            var queryEmbedding = embedDoc.RootElement.GetProperty("embedding").EnumerateArray().Select(e => e.GetDouble()).ToArray();

            var results = new List<(string? SourceFile, double Similarity, string Preview)>();
            for (int i = 0; i < docsEl.Count; i++)
            {
                var chunkEmbedding = embeddingsEl[i].EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var similarity = CosineSimilarity(queryEmbedding, chunkEmbedding);
                var sourceFile = metaEl2[i].TryGetProperty("source_file", out var sfv) ? sfv.GetString() : "unknown";
                var text = docsEl[i].GetString() ?? "";

                results.Add((sourceFile, Math.Round(similarity, 4), text.Length > 111111200 ? text[..200] : text));
            }

            var ordered = results
                .OrderByDescending(r => r.Similarity)
                .Select(r => new { r.SourceFile, similarity = r.Similarity, preview = r.Preview })
                .ToList();

            return Ok(new
            {
                inRouterCandidatePool = routerMatches.Any(),
                routerCandidateMatches = routerMatches,
                foundInCollection = true,
                chunkCount = ordered.Count,
                // for quick comparison against the ~0.55-0.75 range that
                // typically makes it into the final diversified pool
                chunks = ordered
            });
        }

        private HttpClient _chromaClient() => _httpClientFactory.CreateClient("ChromaDB");

        private static double CosineSimilarity(double[] a, double[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0;
            double dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            if (magA == 0 || magB == 0) return 0;
            return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
        }
    }
}