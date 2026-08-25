// Service/LearnedTriggerService.cs
//
// Closes the loop that PolicyTriggerService and DocumentRouterService can't:
// both are automated (LLM-generated triggers, LLM-routed document guesses)
// and can be wrong, as seen in practice (the router matching "Foreign Travel
// Policy" and "Product Cybersecurity Policy" for a resignation question).
// A human correction is ground truth — when someone clicks "Correct" and
// confirms the right answer, this service extracts the actual question that
// failed and permanently binds it to the correct document, so the exact
// same question (or a close paraphrase) is never mis-routed again.
//
// Deliberately kept as a SEPARATE store from policy-triggers.json rather
// than merged into it: this file is auditable ("what did a human actually
// confirm") and safe to review/prune independently of the LLM-generated
// file, which gets fully regenerated on content changes.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MEAI_GPT_API.Service
{
    public class LearnedTriggerEntry
    {
        [JsonPropertyName("SourceFile")]
        public string SourceFile { get; set; } = string.Empty;

        // The ORIGINAL failing question, normalized. Unlike PolicyTriggerService's
        // hand/LLM-picked short phrases, this is the actual query that was
        // gotten wrong — the strongest possible signal for "if you see this
        // again, here's the right document."
        [JsonPropertyName("Triggers")]
        public List<string> Triggers { get; set; } = new();

        [JsonPropertyName("AnchorText")]
        public string AnchorText { get; set; } = string.Empty;

        [JsonPropertyName("LearnedAt")]
        public DateTime LearnedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("CorrectionCount")]
        public int CorrectionCount { get; set; } = 1;
    }

    public class LearnedTriggerService
    {
        private readonly ILogger<LearnedTriggerService> _logger;
        private readonly string _filePath;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        private List<LearnedTriggerEntry> _entries = new();

        public LearnedTriggerService(ILogger<LearnedTriggerService> logger, string filePath)
        {
            _logger = logger;
            _filePath = filePath;
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _entries = JsonSerializer.Deserialize<List<LearnedTriggerEntry>>(json) ?? new();
                    _logger.LogInformation($"📋 Loaded {_entries.Count} learned trigger entries from {_filePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load learned triggers from {_filePath} — starting empty");
                _entries = new();
            }
        }

        // Normalizes the same way PolicyTriggerService's MatchTriggers
        // compares (lowercase substring match) — kept simple and
        // predictable rather than fuzzy, since this is meant to catch exact
        // or near-exact repeats of a question that was already gotten
        // wrong once, not loosely related questions.
        private static string Normalize(string text) =>
            text.Trim().ToLowerInvariant();

        /// <summary>
        /// Called when a human correction is saved. Binds the ORIGINAL
        /// question (not the correction text) to the given source
        /// document, so future occurrences of this same question are
        /// anchored correctly regardless of what embedding similarity,
        /// PolicyTriggerService, or DocumentRouterService independently
        /// decide. If this exact question already has a learned entry
        /// pointing elsewhere, it's overwritten — a newer human correction
        /// always wins over a stale one.
        /// </summary>
        public async Task PromoteAsync(string originalQuestion, string correctSourceFile)
        {
            if (string.IsNullOrWhiteSpace(originalQuestion) || string.IsNullOrWhiteSpace(correctSourceFile))
                return;

            var normalizedQuestion = Normalize(originalQuestion);

            await _fileLock.WaitAsync();
            try
            {
                var existing = _entries.FirstOrDefault(e =>
                    e.Triggers.Any(t => string.Equals(t, normalizedQuestion, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    if (string.Equals(existing.SourceFile, correctSourceFile, StringComparison.OrdinalIgnoreCase))
                    {
                        // Same question corrected the same way again — just bump the
                        // confidence counter, useful later for surfacing "well-confirmed"
                        // vs. "corrected once" entries if this ever needs pruning.
                        existing.CorrectionCount++;
                    }
                    else
                    {
                        // A newer correction disagrees with a previous one — trust the
                        // latest human judgment over the older one.
                        _logger.LogWarning(
                            "Learned trigger for '{Question}' changing source from {Old} to {New} — newer correction overrides",
                            originalQuestion, existing.SourceFile, correctSourceFile);
                        existing.SourceFile = correctSourceFile;
                        existing.AnchorText = ExtractDocumentTitleTerms(correctSourceFile);
                        existing.LearnedAt = DateTime.UtcNow;
                        existing.CorrectionCount = 1;
                    }
                }
                else
                {
                    _entries.Add(new LearnedTriggerEntry
                    {
                        SourceFile = correctSourceFile,
                        Triggers = new List<string> { normalizedQuestion },
                        AnchorText = ExtractDocumentTitleTerms(correctSourceFile)
                    });
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(_entries, options));

                _logger.LogInformation(
                    "✅ Learned trigger promoted: '{Question}' -> {Source}",
                    originalQuestion, correctSourceFile);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Same shape/contract as PolicyTriggerService.MatchTriggers, so the
        /// caller in GetRelevantChunksWithExpansionAsync can merge both
        /// with identical handling — just a stronger boost, since this is
        /// human-verified rather than LLM-generated or LLM-routed.
        /// </summary>
        public List<(string AnchorText, string SourceFile)> MatchTriggers(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _entries.Count == 0)
                return new();

            var normalizedQuery = Normalize(query);
            var matches = new List<(string AnchorText, string SourceFile)>();

            foreach (var entry in _entries)
            {
                // Substring match in either direction: catches both a verbatim
                // repeat and a slightly-trimmed/expanded rephrasing that still
                // contains the original question as a substring, or vice versa.
                if (entry.Triggers.Any(t => normalizedQuery.Contains(t) || t.Contains(normalizedQuery)))
                {
                    matches.Add((entry.AnchorText, entry.SourceFile));
                }
            }

            if (matches.Count > 0)
            {
                _logger.LogInformation(
                    "🎓 Learned-correction trigger match for '{Query}': {Matches}",
                    query, string.Join(", ", matches.Select(m => $"{m.SourceFile} <- \"{m.AnchorText}\"")));
            }

            return matches.Distinct().ToList();
        }

        // Duplicated from PolicyTriggerService/DocumentRouterService by design
        // — see the "kept in sync BY HAND" comment on those. All title-term
        // extraction call sites must agree on the same filename convention.
        private static string ExtractDocumentTitleTerms(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";

            var name = Path.GetFileNameWithoutExtension(fileName);

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
    }
}