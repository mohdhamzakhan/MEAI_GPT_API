// Service/Models/GradeEligibilityService.cs
//
// Reads each chunk's actual text once during the refresh cycle and asks:
// does this clause restrict eligibility by grade band and/or by Direct/
// Indirect employee status? If so, extracts it and caches the result,
// keyed by a hash of the chunk's own text so unchanged content never gets
// re-sent to the LLM on subsequent refreshes.
//
// Deliberately requires NO folder/file reorganization — this is the
// content-based alternative discussed after the folder-convention design
// was ruled out. See ELIGIBILITY-DIMENSIONS-GENERIC.md for the full
// rationale and the nested employee_category/grade relationship this
// encodes (grade only exists for Indirect employees; see
// ApplyStructuralInferenceRules below).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using MEAIGPTAPI.Services;

namespace MEAI_GPT_API.Service.Models
{
    public class ChunkEligibility
    {
        [JsonPropertyName("SourceFile")]
        public string SourceFile { get; set; } = string.Empty;

        [JsonPropertyName("ChunkKey")]
        public string ChunkKey { get; set; } = string.Empty;

        [JsonPropertyName("MinGradeBand")]
        public string? MinGradeBand { get; set; }  // resolved band name, e.g. "ManagementStaff"

        [JsonPropertyName("MaxGradeBand")]
        public string? MaxGradeBand { get; set; }

        [JsonPropertyName("EmployeeCategory")]
        public string? EmployeeCategory { get; set; } // "direct" | "indirect" | null

        [JsonPropertyName("DirectSubtype")]
        public string? DirectSubtype { get; set; } // "Direct Worker" | "Administrative Staff" | null
    }

    public class GradeEligibilityService
    {
        private readonly OllamaHttpClient _ollamaClient;
        private readonly GradeHierarchyService _hierarchy;
        private readonly ILogger<GradeEligibilityService> _logger;
        private readonly string _cacheFilePath;
        private readonly string _generationModelName;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        private Dictionary<string, ChunkEligibility> _cache = new(); // keyed by ChunkKey

        private static readonly string[] EligibilityHints =
        {
            "and above", "or above", "and below", "or below",
            "eligib", "applicable to", "applicable for", "for employees",
            "direct employee", "indirect employee", "direct worker", "administrative staff"
        };

        public GradeEligibilityService(
            OllamaHttpClient ollamaClient,
            GradeHierarchyService hierarchy,
            ILogger<GradeEligibilityService> logger,
            string cacheFilePath,
            string generationModelName)
        {
            _ollamaClient = ollamaClient;
            _hierarchy = hierarchy;
            _logger = logger;
            _cacheFilePath = cacheFilePath;
            _generationModelName = generationModelName;

            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    var list = JsonSerializer.Deserialize<List<ChunkEligibility>>(json) ?? new();
                    _cache = list.ToDictionary(e => e.ChunkKey, e => e);
                    _logger.LogInformation($"📋 Loaded {_cache.Count} cached grade-eligibility entries from {_cacheFilePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load grade eligibility cache from {_cacheFilePath} — starting empty");
                _cache = new();
            }
        }

        private static string MakeChunkKey(string sourceFile, string chunkText)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunkText)))[..16];
            return $"{sourceFile}::{hash}";
        }

        /// <summary>
        /// Cheap pre-filter — skips the LLM call entirely for chunks that
        /// don't even plausibly mention eligibility. Expected to eliminate
        /// the large majority of chunks in a typical policy document.
        /// </summary>
        private bool MightContainEligibilityClause(string chunkText)
        {
            var lower = chunkText.ToLowerInvariant();

            var mentionsAnyBandTitle = _hierarchy.AllBands.Any(b => lower.Contains(b.ToLowerInvariant()))
                || ContainsAnyKnownTitle(lower);

            var mentionsEligibilityLanguage = EligibilityHints.Any(h => lower.Contains(h));

            return mentionsEligibilityLanguage && (mentionsAnyBandTitle || lower.Contains("direct") || lower.Contains("indirect"));
        }

        private bool ContainsAnyKnownTitle(string lowerText)
        {
            // Cheap heuristic: common rank-indicating words. Doesn't need to
            // be exhaustive or precise — false positives just mean the LLM
            // gets called on a few extra chunks (cheap), false negatives
            // mean a real eligibility clause gets missed (expensive to
            // correctness), so this errs on the side of over-triggering.
            string[] rankWords = { "manager", "engineer", "executive", "officer",
                "director", "president", "gm", "avp", "vp", "ceo", "coo", "head" };
            return rankWords.Any(lowerText.Contains);
        }

        /// <summary>
        /// Extracts (or returns cached) eligibility for one chunk. Never
        /// throws — any failure is logged and treated as "no constraint
        /// found," exactly like PolicyTriggerService's error handling,
        /// since this is an enrichment layer that must never block or fail
        /// the core indexing pipeline.
        /// </summary>
        public async Task<ChunkEligibility> GetOrExtractAsync(string sourceFile, string chunkText, CancellationToken cancellationToken = default)
        {
            var key = MakeChunkKey(sourceFile, chunkText);

            if (_cache.TryGetValue(key, out var cached))
                return cached;

            if (!MightContainEligibilityClause(chunkText))
            {
                var none = new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key };
                await SaveEntryAsync(none);
                return none;
            }

            try
            {
                _logger.LogInformation($"🔍 Extracting grade eligibility for a chunk in {sourceFile}");

                var prompt = BuildExtractionPrompt(chunkText);

                var requestData = new
                {
                    model = _generationModelName,
                    messages = new[]
                    {
                        new { role = "system", content = "You output ONLY valid JSON. No markdown fences, no commentary." },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.1,
                    stream = false
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(45)); // same rationale as PolicyTriggerService

                var response = await _ollamaClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"⚠️ Grade eligibility extraction call failed for a chunk in {sourceFile}: {response.StatusCode}");
                    var failed = new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key };
                    return failed; // NOT cached — retry next refresh, unlike the true-negative case above
                }

                var raw = await response.Content.ReadAsStringAsync();
                var entry = ParseExtractionResponse(raw, sourceFile, key);

                ApplyStructuralInferenceRules(entry);

                await SaveEntryAsync(entry);
                _logger.LogInformation($"✅ Extracted eligibility for chunk in {sourceFile}: min={entry.MinGradeBand}, max={entry.MaxGradeBand}, category={entry.EmployeeCategory}");
                return entry;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"⚠️ Grade eligibility extraction timed out for a chunk in {sourceFile}");
                return new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Grade eligibility extraction failed for a chunk in {sourceFile}");
                return new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key };
            }
        }

        /// <summary>
        /// Structural fact about the org, not a per-clause judgment call:
        /// grade bands only exist for Indirect employees. If extraction
        /// found a grade constraint but no explicit employee_category, it
        /// can only mean Indirect — enforce that deterministically rather
        /// than trusting the model to state it explicitly every time.
        /// </summary>
        private void ApplyStructuralInferenceRules(ChunkEligibility entry)
        {
            var hasGradeConstraint = entry.MinGradeBand != null || entry.MaxGradeBand != null;
            if (hasGradeConstraint && entry.EmployeeCategory == null)
            {
                entry.EmployeeCategory = "indirect";
            }
        }

        private string BuildExtractionPrompt(string chunkText)
        {
            var bandList = string.Join(" -> ", _hierarchy.AllBands);

            return $@"You are extracting employee eligibility rules from HR policy text.

Grade bands (Indirect employees only), ascending order: {bandList}

Employees are separately classified as Direct or Indirect. Direct employees
(Direct Workers or Administrative Staff) do not have a position on the
grade-band ladder above — that ladder only applies to Indirect employees.

Text:
---
{chunkText}
---

IMPORTANT: only extract eligibility ('who this benefit/clause applies to'),
never approval authority ('who must approve this'). A sentence like
'requires Manager approval' is NOT an eligibility statement about Managers.

If the text names a specific job title (e.g. ""Deputy Manager"") rather than
a band name directly, extract the title text itself in min_grade_title /
max_grade_title — band resolution happens separately.

Respond with ONLY this JSON, nothing else:
{{
  ""min_grade_title"": ""<job title or band name as it appears in the text, or null>"",
  ""max_grade_title"": ""<job title or band name as it appears in the text, or null>"",
  ""employee_category"": ""<'direct', 'indirect', or null if not restricted by this>"",
  ""direct_subtype"": ""<'Direct Worker', 'Administrative Staff', or null>""
}}";
        }

        private ChunkEligibility ParseExtractionResponse(string rawHttpBody, string sourceFile, string chunkKey)
        {
            var entry = new ChunkEligibility { SourceFile = sourceFile, ChunkKey = chunkKey };

            try
            {
                using var doc = JsonDocument.Parse(rawHttpBody);
                var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
                content = StripMarkdownFences(content).Trim();

                using var parsed = JsonDocument.Parse(content);
                var root = parsed.RootElement;

                var minTitle = GetStringOrNull(root, "min_grade_title");
                var maxTitle = GetStringOrNull(root, "max_grade_title");

                // Resolve title text -> a band, using the min/max-appropriate
                // ambiguous-title default (see GradeHierarchyService).
                entry.MinGradeBand = minTitle != null ? _hierarchy.ResolveTitleForMinBound(minTitle) : null;
                entry.MaxGradeBand = maxTitle != null ? _hierarchy.ResolveTitleForMaxBound(maxTitle) : null;

                entry.EmployeeCategory = GetStringOrNull(root, "employee_category")?.ToLowerInvariant();
                entry.DirectSubtype = GetStringOrNull(root, "direct_subtype");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"⚠️ Could not parse grade eligibility response for {sourceFile}");
            }

            return entry;
        }

        private static string? GetStringOrNull(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var prop)) return null;
            if (prop.ValueKind != JsonValueKind.String) return null;
            var val = prop.GetString();
            return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
        }

        private static string StripMarkdownFences(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0) text = text.Substring(firstNewline + 1);
                var lastFence = text.LastIndexOf("```");
                if (lastFence >= 0) text = text.Substring(0, lastFence);
            }
            return text;
        }

        private async Task SaveEntryAsync(ChunkEligibility entry)
        {
            await _fileLock.WaitAsync();
            try
            {
                _cache[entry.ChunkKey] = entry;

                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(_cacheFilePath, JsonSerializer.Serialize(_cache.Values.ToList(), options));
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }
}