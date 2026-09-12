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
using System.Text.RegularExpressions;
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
        [JsonPropertyName("ExtractionPath")]
        public string ExtractionPath { get; set; } = "Unknown"; // "PreFilterSkip" | "Deterministic" | "LlmExtracted"

        [JsonPropertyName("ChunkPreview")]
        public string? ChunkPreview { get; set; } // first ~150 chars, for manual spot-checking
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
            var normalized = chunkText.ToLowerInvariant().Replace("&", " and ");
            var lower = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
                "director", "president", "gm", "avp", "vp", "ceo", "coo", "head",
                "incharge", "in-charge", "in charge" };
            return rankWords.Any(lowerText.Contains);
        }

        /// <summary>
        /// Extracts (or returns cached) eligibility for one chunk. Never
        /// throws — any failure is logged and treated as "no constraint
        /// found," exactly like PolicyTriggerService's error handling,
        /// since this is an enrichment layer that must never block or fail
        /// the core indexing pipeline.
        /// </summary>
        // Mirrors PolicyAnalysisService.GradeTiers exactly — deliberately
        // duplicated rather than shared, since these two services live in
        // different parts of the codebase and this pattern set is small and
        // stable. If GradeTiers changes, update both.
        //
        // "Jr. Supervisor and above" / "indirect category" is the same
        // threshold as employee_category=indirect with a grade floor at the
        // bottom of the Indirect ladder (ManagementStaff) — confirmed
        // against the org chart data, "Jr. Supervisor" just isn't the term
        // the org chart itself uses. "Below Jr. Supervisor" / "direct
        // category" is a pure Direct-employee statement with no grade band
        // at all, since Direct employees have no position on that ladder.
        private static readonly string[] IndirectThresholdPatterns = {
            @"jr\.?\s*supervisor\s*(&|and)\s*above",
            @"supervisor\s*(&|and)\s*above",
            @"indirect\s*category",
        };
        private static readonly string[] DirectThresholdPatterns = {
            @"below\s*jr\.?\s*supervisor",
            @"below\s*supervisor",
            @"direct\s*category",
        };

        /// <summary>
        /// Deterministic check for the single most common eligibility
        /// phrasing in these policies. Returns null if neither pattern set
        /// matches, meaning the caller should fall through to the general
        /// LLM-based extraction instead. Matching here means zero LLM calls,
        /// zero chance of extraction failure, and guaranteed agreement with
        /// PolicyAnalysisService's existing chunk-level grade detection —
        /// both are checking for the literal same phrases.
        /// </summary>
        private ChunkEligibility? TryDeterministicThresholdMatch(string sourceFile, string chunkKey, string chunkText)
        {
            bool isIndirect = IndirectThresholdPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(chunkText, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            bool isDirect = DirectThresholdPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(chunkText, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase));

            if (isIndirect && !isDirect)
            {
                // Bottom of the Indirect ladder — same semantics as "Jr.
                // Supervisor and above" without needing that literal title
                // present anywhere in grade-hierarchy.json.
                return new ChunkEligibility
                {
                    SourceFile = sourceFile,
                    ChunkKey = chunkKey,
                    MinGradeBand = _hierarchy.AllBands.FirstOrDefault(),
                    EmployeeCategory = "indirect"
                };
            }

            if (isDirect && !isIndirect)
            {
                return new ChunkEligibility
                {
                    SourceFile = sourceFile,
                    ChunkKey = chunkKey,
                    EmployeeCategory = "direct"
                    // No grade band — Direct employees have no position on
                    // the Indirect-only ladder at all.
                };
            }

            // Both or neither matched — genuinely ambiguous or irrelevant,
            // let the LLM path make the call (or find nothing, correctly).
            return null;
        }

        public async Task<(ChunkEligibility Entry, bool MadeRealCall)> GetOrExtractAsync(string sourceFile, string chunkText, CancellationToken cancellationToken = default)
        {
            var key = MakeChunkKey(sourceFile, chunkText);

            if (_cache.TryGetValue(key, out var cached))
                return (cached, false);

            var deterministic = TryDeterministicThresholdMatch(sourceFile, key, chunkText);
            if (deterministic != null)
            {
                deterministic.ExtractionPath = "Deterministic";
                deterministic.ChunkPreview = chunkText.Length > 150 ? chunkText[..150] : chunkText;
                await SaveEntryAsync(deterministic);
                _logger.LogInformation($"✅ Deterministic threshold match for chunk in {sourceFile}: category={deterministic.EmployeeCategory}, minGrade={deterministic.MinGradeBand}");
                return (deterministic, false); // no LLM call made
            }

            if (!MightContainEligibilityClause(chunkText))
            {
                var none = new ChunkEligibility
                {
                    SourceFile = sourceFile,
                    ChunkKey = key,
                    ExtractionPath = "PreFilterSkip",
                    ChunkPreview = chunkText.Length > 150 ? chunkText[..150] : chunkText
                };
                await SaveEntryAsync(none);
                return (none, false);
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

                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(60));

                        var response = await _ollamaClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning($"⚠️ Grade eligibility extraction call failed (attempt {attempt}/{maxAttempts}) for a chunk in {sourceFile}: {response.StatusCode}");
                            if (attempt == maxAttempts)
                                return (new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key }, true);
                            await Task.Delay(2000 * attempt);
                            continue;
                        }

                        var raw = await response.Content.ReadAsStringAsync();
                        var entry = ParseExtractionResponse(raw, sourceFile, key, chunkText);
                        entry.ExtractionPath = "LlmExtracted";
                        entry.ChunkPreview = chunkText.Length > 150 ? chunkText[..150] : chunkText;
                        ApplyStructuralInferenceRules(entry);
                        await SaveEntryAsync(entry);
                        _logger.LogInformation($"✅ Extracted eligibility for chunk in {sourceFile}: min={entry.MinGradeBand}, max={entry.MaxGradeBand}, category={entry.EmployeeCategory}");
                        return (entry, true);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning($"⚠️ Grade eligibility extraction timed out (attempt {attempt}/{maxAttempts}) for a chunk in {sourceFile}");
                        if (attempt == maxAttempts)
                            return (new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key }, true);
                        await Task.Delay(2000 * attempt);
                    }
                }

                // Unreachable in practice — every exit from the loop above returns
                // explicitly on the final attempt. This satisfies the compiler's
                // flow analysis, which can't prove that from the loop bounds alone.
                return (new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key }, true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"⚠️ Grade eligibility extraction timed out for a chunk in {sourceFile}");
                return (new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key }, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Grade eligibility extraction failed for a chunk in {sourceFile}");
                return (new ChunkEligibility { SourceFile = sourceFile, ChunkKey = key }, true);
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

Some clauses chain several synonymous ways of saying the same threshold
together, e.g. ""Section/Function Incharge, JM Level & above"". When you see
this, extract ONLY the shortest, most standard anchor — prefer an explicit
band-level shorthand (""JM Level"", ""LM Level"", ""MM Level"", etc.) over a
job title if both appear in the same clause, since the level shorthand is
unambiguous. Do not concatenate multiple synonyms into one title string.

Most clauses state only ONE side of the range. ""X and above"" / ""X or
above"" sets min_grade_title only — max_grade_title MUST be null. ""X and
below"" / ""X or below"" / ""up to X"" sets max_grade_title only —
min_grade_title MUST be null. Only set both fields when the text explicitly
states a floor AND a separate ceiling (e.g. ""between Deputy Manager and
Senior Manager""). Never copy the same title into both fields.

Some job titles exist at more than one grade level, so the text sometimes
disambiguates itself with a parenthetical right after the title, e.g.
""Asst. Manager and above (Lower Management Level)"". When a parenthetical
like this immediately follows a title and itself names or describes a
level, extract the PARENTHETICAL'S wording (e.g. ""Lower Management
Level""), not the bare job title — the parenthetical is the author
resolving the ambiguity for you, and discarding it in favor of the plain
title throws that resolution away.

Respond with ONLY this JSON, nothing else:
{{
  ""min_grade_title"": ""<job title or band name as it appears in the text, or null>"",
  ""max_grade_title"": ""<job title or band name as it appears in the text, or null>"",
  ""employee_category"": ""<'direct', 'indirect', or null if not restricted by this>"",
  ""direct_subtype"": ""<'Direct Worker', 'Administrative Staff', or null>""
}}";
        }

        /// <summary>
        /// Small local models (llama3.1:8b here) occasionally emit more than
        /// one JSON object in a single response — a duplicate, an echoed
        /// retry, or trailing commentary — which makes JsonDocument.Parse on
        /// the raw string throw even though a perfectly valid object is
        /// sitting right at the start. Scans for the first balanced
        /// top-level {...} span (respecting quoted strings and escapes) and
        /// returns just that substring for parsing.
        /// </summary>
        private static string? ExtractFirstJsonObject(string text)
        {
            var start = text.IndexOf('{');
            if (start < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                }
            }

            return null; // never closed — malformed, let the caller fail loudly
        }

        /// <summary>
        /// Parses the LLM response. Throws on failure (does NOT swallow the
        /// exception) so the caller's existing failure path — which
        /// deliberately does not cache — is what actually runs. Silently
        /// returning an empty-but-valid entry here would get that entry
        /// permanently cached by SaveEntryAsync as if it were a genuine
        /// "no eligibility clause" result, indistinguishable from a real
        /// negative and never retried.
        /// </summary>
        /// <summary>
        /// Parses the LLM response. Throws on failure (does NOT swallow the
        /// exception) so the caller's existing failure path — which
        /// deliberately does not cache — is what actually runs. Silently
        /// returning an empty-but-valid entry here would get that entry
        /// permanently cached by SaveEntryAsync as if it were a genuine
        /// "no eligibility clause" result, indistinguishable from a real
        /// negative and never retried.
        /// </summary>
        private ChunkEligibility ParseExtractionResponse(string rawHttpBody, string sourceFile, string chunkKey, string chunkText)
        {
            var entry = new ChunkEligibility { SourceFile = sourceFile, ChunkKey = chunkKey };

            using var doc = JsonDocument.Parse(rawHttpBody);
            var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            content = StripMarkdownFences(content).Trim();

            var jsonObject = ExtractFirstJsonObject(content)
                ?? throw new JsonException($"No balanced JSON object found in model output for {sourceFile}");

            using var parsed = JsonDocument.Parse(jsonObject);
            var root = parsed.RootElement;

            var minTitle = GetStringOrNull(root, "min_grade_title");
            var maxTitle = GetStringOrNull(root, "max_grade_title");

            // Despite the prompt explicitly forbidding it, small local
            // models sometimes copy the same title into both fields for a
            // one-sided clause. Left alone, an ambiguous title then
            // resolves to its lowest band for min and highest band for max
            // — two *different*, plausible-looking values that silently
            // fabricate a range the source text never stated. Detect the
            // duplicate at the title-string level (before band resolution
            // hides it) and collapse to whichever side the clause's own
            // wording actually supports.
            if (minTitle != null && maxTitle != null &&
                GradeHierarchyService.Normalize(minTitle) == GradeHierarchyService.Normalize(maxTitle))
            {
                var lowerChunk = chunkText.ToLowerInvariant();
                bool statesUpperBound = lowerChunk.Contains("and below") || lowerChunk.Contains("or below") || lowerChunk.Contains("up to") || lowerChunk.Contains("upto");
                bool statesLowerBound = lowerChunk.Contains("and above") || lowerChunk.Contains("or above") || lowerChunk.Contains("& above");

                _logger.LogWarning($"⚠️ Model returned the same title ('{minTitle}') for both min and max in {sourceFile} — collapsing to a single bound based on clause wording (statesLower={statesLowerBound}, statesUpper={statesUpperBound})");

                if (statesLowerBound && !statesUpperBound) maxTitle = null;
                else if (statesUpperBound && !statesLowerBound) minTitle = null;
                // If both or neither phrase is present, leave both set —
                // ambiguous enough that we can't confidently pick one, and
                // an actual explicit range is rare but does happen.
            }

            // Resolve title text -> a band, using the min/max-appropriate
            // ambiguous-title default (see GradeHierarchyService).
            entry.MinGradeBand = minTitle != null ? _hierarchy.ResolveTitleForMinBound(minTitle) : null;
            entry.MaxGradeBand = maxTitle != null ? _hierarchy.ResolveTitleForMaxBound(maxTitle) : null;

            // Deterministic override: some job titles exist at more than
            // one grade level, so a clause sometimes parenthetically
            // clarifies which one it means right after the title, e.g.
            // "Asst. Manager and above (Lower Management Level)". That
            // clarification is the author resolving the exact ambiguity
            // our title-lookup would otherwise have to guess at via the
            // inclusive-by-default fallback — trust it outright. This
            // check is regex-based against the raw chunk text, not
            // dependent on the LLM having extracted or prioritized it
            // correctly, since that's not reliable enough for something
            // this mechanical.
            var parenMatch = Regex.Match(chunkText, @"\(([^)]*\bManagement\b[^)]*)\)", RegexOptions.IgnoreCase);
            if (parenMatch.Success)
            {
                var clarifiedBand = _hierarchy.ResolveTitleForMinBound(parenMatch.Groups[1].Value);
                if (clarifiedBand != null)
                {
                    if (entry.MinGradeBand != null) entry.MinGradeBand = clarifiedBand;
                    if (entry.MaxGradeBand != null) entry.MaxGradeBand = clarifiedBand;
                }
            }

            entry.EmployeeCategory = GetStringOrNull(root, "employee_category")?.ToLowerInvariant();
            entry.DirectSubtype = GetStringOrNull(root, "direct_subtype");

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