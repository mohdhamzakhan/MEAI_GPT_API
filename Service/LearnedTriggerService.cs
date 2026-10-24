using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service
{
    public class LearnedTriggerEntry
    {
        [JsonPropertyName("SourceFile")] public string SourceFile { get; set; } = string.Empty;
        [JsonPropertyName("Triggers")] public List<string> Triggers { get; set; } = new();
        [JsonPropertyName("AnchorText")] public string AnchorText { get; set; } = string.Empty;
        [JsonPropertyName("LearnedAt")] public DateTime LearnedAt { get; set; } = DateTime.UtcNow;
        [JsonPropertyName("CorrectionCount")] public int CorrectionCount { get; set; } = 1;

        // correction (human/admin) > appreciation (user thumbs-up) > auto (threshold-learned).
        // Old files have no Origin, so they default to the human tier.
        [JsonPropertyName("Origin")] public string Origin { get; set; } = "correction";
        [JsonPropertyName("LastMatchedAt")] public DateTime? LastMatchedAt { get; set; }
        [JsonPropertyName("MatchCount")] public int MatchCount { get; set; }
    }

    public record LearnedMatch(string AnchorText, string SourceFile, string Origin);

    public class LearnedTriggerService
    {
        public const string OriginCorrection = "correction";
        public const string OriginAppreciation = "appreciation";
        public const string OriginAuto = "auto";

        private static readonly Regex NonWord = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly ILogger<LearnedTriggerService> _logger;
        private readonly string _filePath;
        private readonly SemaphoreSlim _fileLock = new(1, 1);   // serializes writers
        private readonly object _sync = new();                  // guards _entries for readers and writers
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
                    _entries = JsonSerializer.Deserialize<List<LearnedTriggerEntry>>(File.ReadAllText(_filePath)) ?? new();
                    _logger.LogInformation($"📋 Loaded {_entries.Count} learned trigger entries from {_filePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load learned triggers from {_filePath} — starting empty");
                _entries = new();
            }
        }

        // ───────────── normalization & matching ─────────────

        private static string Normalize(string text) =>
            Regex.Replace(NonWord.Replace(text.ToLowerInvariant(), " "), @"\s+", " ").Trim();

        private static HashSet<string> Tokens(string norm) =>
            norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        private static int Rank(string? origin) => origin switch
        {
            OriginCorrection => 3,
            OriginAppreciation => 2,
            OriginAuto => 1,
            _ => 3
        };

        // Exact equality always matches. Otherwise word-based: a short query
        // can no longer match by being a substring of a longer trigger.
        private static bool IsMatch(string query, string trigger)
        {
            if (query == trigger) return true;

            var q = Tokens(query);
            var t = Tokens(trigger);
            if (q.Count < 3 || t.Count < 3) return false;

            var inter = q.Intersect(t).Count();

            // near-identical wording
            if ((double)inter / q.Union(t).Count() >= 0.8) return true;

            // one side fully contained in the other (e.g. a topic-anchored
            // retrieval query that wraps the original question)
            var smaller = Math.Min(q.Count, t.Count);
            return smaller >= 4 && inter == smaller;
        }

        // ───────────── writes ─────────────

        private async Task<bool> MutateAsync(Func<bool> mutate)
        {
            await _fileLock.WaitAsync();
            try
            {
                string json;
                lock (_sync)
                {
                    if (!mutate()) return false;
                    json = JsonSerializer.Serialize(_entries, JsonOpts);
                }

                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // write then swap, so a crash mid-write can't corrupt the file
                var tmp = _filePath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
                return true;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private void AddTrigger(string norm, string sourceName, string origin)
        {
            var target = _entries.FirstOrDefault(e =>
                e.Origin == origin && e.SourceFile.Equals(sourceName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                target = new LearnedTriggerEntry
                {
                    SourceFile = sourceName,
                    Origin = origin,
                    AnchorText = ExtractDocumentTitleTerms(sourceName)
                };
                _entries.Add(target);
            }

            target.Triggers.Add(norm);
            target.LearnedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Binds a question to a document. Higher tiers are never overwritten by
        /// lower ones (auto can't undo a human correction); equal or higher tiers
        /// replace an older binding for the same question.
        /// </summary>
        public async Task PromoteAsync(string originalQuestion, string correctSourceFile,
            string origin = OriginCorrection)
        {
            if (string.IsNullOrWhiteSpace(originalQuestion) || string.IsNullOrWhiteSpace(correctSourceFile))
                return;

            var norm = Normalize(originalQuestion);
            if (norm.Length == 0) return;
            var sourceName = Path.GetFileName(correctSourceFile);

            var changed = await MutateAsync(() =>
            {
                var existing = _entries.FirstOrDefault(e => e.Triggers.Contains(norm));

                if (existing != null)
                {
                    var sameSource = existing.SourceFile.Equals(sourceName, StringComparison.OrdinalIgnoreCase);

                    if (sameSource && Rank(origin) <= Rank(existing.Origin))
                    {
                        existing.CorrectionCount++;          // same question, same document, confirmed again
                        return true;
                    }

                    if (!sameSource && Rank(origin) < Rank(existing.Origin))
                    {
                        _logger.LogInformation(
                            "Learned trigger for '{Q}' kept at {Old} ({OldOrigin}); lower-tier {Origin} suggestion for {New} ignored",
                            originalQuestion, existing.SourceFile, existing.Origin, origin, sourceName);
                        return false;
                    }

                    if (!sameSource)
                        _logger.LogWarning("Learned trigger for '{Q}' moving from {Old} to {New} ({Origin})",
                            originalQuestion, existing.SourceFile, sourceName, origin);

                    // move this one trigger (not the whole entry) to its new home/tier
                    existing.Triggers.Remove(norm);
                    if (existing.Triggers.Count == 0) _entries.Remove(existing);
                }

                AddTrigger(norm, sourceName, origin);
                return true;
            });

            if (changed)
                _logger.LogInformation("✅ Learned trigger promoted ({Origin}): '{Q}' -> {Src}",
                    origin, originalQuestion, sourceName);
        }

        // ───────────── reads ─────────────

        public List<LearnedMatch> MatchTriggersDetailed(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();

            var norm = Normalize(query);
            var matches = new List<LearnedMatch>();

            lock (_sync)
            {
                foreach (var e in _entries)
                {
                    if (!e.Triggers.Any(t => IsMatch(norm, t))) continue;
                    matches.Add(new LearnedMatch(e.AnchorText, e.SourceFile, e.Origin));
                    e.MatchCount++;                       // persisted with the next write
                    e.LastMatchedAt = DateTime.UtcNow;
                }
            }

            if (matches.Count > 0)
                _logger.LogInformation("🎓 Learned trigger match for '{Query}': {Matches}", query,
                    string.Join(", ", matches.Select(m => $"{m.SourceFile} [{m.Origin}]")));

            return matches.Distinct().ToList();
        }

        // Kept so existing callers still compile.
        public List<(string AnchorText, string SourceFile)> MatchTriggers(string query) =>
            MatchTriggersDetailed(query).Select(m => (m.AnchorText, m.SourceFile)).Distinct().ToList();

        // ───────────── admin / maintenance ─────────────

        public List<LearnedTriggerEntry> GetAll()
        {
            lock (_sync)
                return _entries.Select(e => new LearnedTriggerEntry
                {
                    SourceFile = e.SourceFile,
                    Triggers = e.Triggers.ToList(),
                    AnchorText = e.AnchorText,
                    LearnedAt = e.LearnedAt,
                    CorrectionCount = e.CorrectionCount,
                    Origin = e.Origin,
                    LastMatchedAt = e.LastMatchedAt,
                    MatchCount = e.MatchCount
                }).ToList();
        }

        public Task<bool> RemoveTriggerAsync(string question) =>
            MutateAsync(() =>
            {
                var norm = Normalize(question);
                var e = _entries.FirstOrDefault(x => x.Triggers.Contains(norm));
                if (e == null) return false;
                e.Triggers.Remove(norm);
                if (e.Triggers.Count == 0) _entries.Remove(e);
                return true;
            });

        /// <summary>Drops entries whose document is no longer indexed. Never prunes against an empty list.</summary>
        public async Task<int> PruneMissingSourcesAsync(IEnumerable<string> indexedFileNames)
        {
            var existing = new HashSet<string>(
                indexedFileNames.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
            if (existing.Count == 0) return 0;

            var removed = 0;
            await MutateAsync(() =>
            {
                removed = _entries.RemoveAll(e => !existing.Contains(e.SourceFile));
                return removed > 0;
            });
            return removed;
        }

        private static string ExtractDocumentTitleTerms(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            var name = Path.GetFileNameWithoutExtension(fileName);

            var underscoreIdx = name.IndexOf('_');
            if (underscoreIdx >= 0 && underscoreIdx < 8) name = name[(underscoreIdx + 1)..];

            var parenIdx = name.IndexOf('(');
            if (parenIdx >= 0) name = name[..parenIdx];

            return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        }
    }
}