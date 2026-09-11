// Service/Models/GradeHierarchyService.cs
//
// Loads the grade band hierarchy + title-to-band mapping from
// grade-hierarchy.json and resolves:
//   1. A band name -> its numeric rank (position in the ordered Bands list)
//   2. A job title mentioned in policy text -> the band it refers to,
//      handling titles that appear in more than one band (see
//      AmbiguousTitles in the config) by picking the band appropriate to
//      whether it's being used as a lower bound ("X and above") or an
//      upper bound ("X and below").
//
// Deliberately does NOT resolve an actual employee's band from their job
// title — that ambiguity (the same title genuinely sitting in two bands
// depending on context) means title-string matching isn't reliable enough
// for an individual's real eligibility. An employee's actual band should
// come from HR data directly (see EmployeeDirectoryService), not from
// re-deriving it here. This service is for interpreting POLICY TEXT during
// extraction, where "prefer inclusive" is an acceptable default; it is not
// for resolving a specific person's status.

using System.Text.Json;

namespace MEAI_GPT_API.Service.Models
{
    internal class AmbiguousTitleResolution
    {
        public string PreferForMinBound { get; set; } = "";
        public string PreferForMaxBound { get; set; } = "";
    }

    internal class GradeHierarchyConfig
    {
        public List<string> Bands { get; set; } = new();
        public Dictionary<string, List<string>> TitleToBand { get; set; } = new();
        public Dictionary<string, AmbiguousTitleResolution> AmbiguousTitles { get; set; } = new();
    }

    internal class EmployeeSelfServiceConfig
    {
        public List<string> DirectCategorySubtypes { get; set; } = new();
        public Dictionary<string, List<string>> TitlesByBand { get; set; } = new();
    }

    /// <summary>
    /// Public DTO for the "Your position" picker — bands in seniority
    /// order, each with its curated single-band title list (empty list is
    /// valid and means that band has no distinct picker entries; see
    /// OperationalManagement), plus the two Direct-category subtypes which
    /// have no grade ladder at all.
    /// </summary>
    public class DesignationOptions
    {
        public List<string> Bands { get; set; } = new();
        public Dictionary<string, List<string>> TitlesByBand { get; set; } = new();
        public List<string> DirectCategorySubtypes { get; set; } = new();
    }

    public class GradeHierarchyService
    {
        private readonly ILogger<GradeHierarchyService> _logger;
        private readonly GradeHierarchyConfig _config;
        private readonly EmployeeSelfServiceConfig _selfServiceConfig;

        // title (lowercased, trimmed) -> set of bands it appears in
        private readonly Dictionary<string, List<string>> _titleLookup;

        // Reverse lookup for the picker: canonical self-service title
        // (as stored verbatim, not normalized) -> its one band. Built from
        // EmployeeSelfService.TitlesByBand, which is single-band-per-title
        // by construction, so this is a plain 1:1 map, not the multi-band
        // ambiguity _titleLookup has to handle.
        private readonly Dictionary<string, string> _selfServiceTitleToBand;

        public GradeHierarchyService(IConfiguration configuration, ILogger<GradeHierarchyService> logger)
        {
            _logger = logger;
            _config = LoadConfig(configuration);
            _titleLookup = BuildTitleLookup(_config);
            _selfServiceConfig = LoadSelfServiceConfig(configuration);
            _selfServiceTitleToBand = _selfServiceConfig.TitlesByBand
                .SelectMany(kv => kv.Value.Select(title => (title, band: kv.Key)))
                .ToDictionary(x => x.title, x => x.band, StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(
                $"📊 Grade hierarchy loaded: {_config.Bands.Count} bands, {_titleLookup.Count} distinct titles, {_config.AmbiguousTitles.Count} flagged as ambiguous");
        }

        /// <summary>
        /// Public options for the "Your position" picker — bands in
        /// seniority order (same order as the underlying Bands list, so
        /// the frontend's Level dropdown reads junior-to-senior without
        /// having to know the ordering itself), each band's curated title
        /// list, and the two Direct-category subtypes.
        /// </summary>
        public DesignationOptions GetDesignationOptions()
        {
            return new DesignationOptions
            {
                Bands = _config.Bands,
                TitlesByBand = _selfServiceConfig.TitlesByBand,
                DirectCategorySubtypes = _selfServiceConfig.DirectCategorySubtypes
            };
        }

        /// <summary>
        /// Given a previously-saved title (e.g. an employee's stored
        /// Grade), returns which band it belongs to per the self-service
        /// list — used to pre-select the Level dropdown when someone
        /// reopens the picker to update their position. Returns null for a
        /// title that isn't in the curated list (e.g. an old free-text
        /// value saved before this became a dropdown).
        /// </summary>
        public string? GetSelfServiceBandForTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            return _selfServiceTitleToBand.TryGetValue(title.Trim(), out var band) ? band : null;
        }

        private EmployeeSelfServiceConfig LoadSelfServiceConfig(IConfiguration configuration)
        {
            try
            {
                var path = configuration["GradeHierarchyFilePath"] ?? "./context/grade-hierarchy.json";
                if (!File.Exists(path)) return new EmployeeSelfServiceConfig();

                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("EmployeeSelfService", out var root))
                {
                    _logger.LogWarning("⚠️ No EmployeeSelfService section in grade-hierarchy.json — the position picker will have no options");
                    return new EmployeeSelfServiceConfig();
                }

                return JsonSerializer.Deserialize<EmployeeSelfServiceConfig>(root.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new EmployeeSelfServiceConfig();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load EmployeeSelfService section — the position picker will have no options");
                return new EmployeeSelfServiceConfig();
            }
        }

        private GradeHierarchyConfig LoadConfig(IConfiguration configuration)
        {
            try
            {
                var path = configuration["GradeHierarchyFilePath"] ?? "./context/grade-hierarchy.json";
                if (!File.Exists(path))
                {
                    _logger.LogWarning($"⚠️ Grade hierarchy file not found at {path} — grade-based eligibility filtering will be inactive");
                    return new GradeHierarchyConfig();
                }

                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement.GetProperty("GradeHierarchy");

                var config = JsonSerializer.Deserialize<GradeHierarchyConfig>(root.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new GradeHierarchyConfig();

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load grade-hierarchy.json — grade-based eligibility filtering will be inactive");
                return new GradeHierarchyConfig();
            }
        }

        /// <summary>
        /// Normalizes a title/phrase before matching: lowercases, expands
        /// "&" to "and" (policy text frequently uses "Manager & above"
        /// where the config and/or extraction uses the word "and"),
        /// strips periods (so "Asst. Manager" == "Asst Manager" == "asst
        /// manager"), and collapses whitespace. Without this, punctuation
        /// variants of the same abbreviation silently split into unrelated,
        /// unambiguous single-band entries instead of correctly being
        /// treated as the same ambiguous title.
        /// </summary>
        internal static string Normalize(string s)
        {
            var result = s.ToLowerInvariant()
                .Replace("&", " and ")
                .Replace(".", "");
            return string.Join(' ', result.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private Dictionary<string, List<string>> BuildTitleLookup(GradeHierarchyConfig config)
        {
            var lookup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (band, titles) in config.TitleToBand)
            {
                foreach (var title in titles)
                {
                    var key = Normalize(title);
                    if (!lookup.TryGetValue(key, out var bands))
                    {
                        bands = new List<string>();
                        lookup[key] = bands;
                    }
                    if (!bands.Contains(band, StringComparer.OrdinalIgnoreCase))
                        bands.Add(band);
                }
            }

            return lookup;
        }

        /// <summary>All configured band names, in ascending order (lowest first).</summary>
        public List<string> AllBands => _config.Bands;

        /// <summary>
        /// Returns a band's rank (0 = lowest), or null if not a recognized
        /// band name. Callers must not silently substitute a default when
        /// this returns null — see the "don't silently default" guidance
        /// in the eligibility design docs.
        /// </summary>
        public int? RankOf(string bandName)
        {
            var idx = _config.Bands.FindIndex(b => string.Equals(b, bandName, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : null;
        }

        /// <summary>
        /// Resolves a job title (as it appears in policy text) to a single
        /// band, for use when the title is acting as a LOWER bound
        /// ("Deputy Manager and above"). If the title is unambiguous,
        /// returns its one band. If ambiguous (appears in multiple bands),
        /// returns the band configured in AmbiguousTitles.PreferForMinBound
        /// (defaulting to the lowest matching band if not explicitly
        /// configured). Returns null if the title isn't recognized at all —
        /// callers should treat that as "couldn't extract a grade
        /// constraint," not silently pick a band.
        /// </summary>
        public string? ResolveTitleForMinBound(string title) => ResolveTitle(title, preferMin: true);

        /// <summary>
        /// Same as <see cref="ResolveTitleForMinBound"/> but for a title
        /// acting as an UPPER bound ("below Deputy Manager" / "Deputy
        /// Manager and below").
        /// </summary>
        public string? ResolveTitleForMaxBound(string title) => ResolveTitle(title, preferMin: false);

        // ✅ NEW: scans free text (a user's QUESTION, e.g. "what are the
        // benefits for AM") for any known job title and resolves it to a
        // band. This is distinct from ResolveTitleForMinBound (which takes
        // an already-isolated title string extracted from POLICY text) and
        // from an employee's own resolved grade (EmployeeDirectoryService) —
        // this is for "the question itself names a grade," independent of
        // who is actually asking. Callers are expected to run abbreviation
        // expansion (e.g. "AM" -> "Assistant Manager") on the text BEFORE
        // calling this, since abbreviations like "AM" are deliberately not
        // in TitleToBand themselves (too easy to false-positive against
        // ordinary English inside arbitrary questions).
        //
        // Matches the LONGEST known title found as a whole-word-ish
        // substring (so "Deputy Manager" beats a coincidental "Manager"
        // match in the same text), reusing ResolveTitleForMinBound's
        // existing ambiguity handling once a title is found. Returns null
        // if no known title is mentioned at all.
        public string? TryResolveGradeMentionedInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var normalizedText = Normalize(text);

            string? bestTitle = null;
            var bestLength = 0;

            foreach (var key in _titleLookup.Keys)
            {
                if (key.Length <= bestLength) continue; // can't beat current best, skip the regex

                // Require a grade-referencing preposition immediately before
                // the title ("benefits FOR am", "eligibility OF manager",
                // "applicable TO deputy manager") — without this, an
                // unrelated mention like "who is my manager" or "escalate to
                // my manager" would incorrectly engage eligibility filtering
                // and narrow the candidate pool for a question that was
                // never asking about a grade at all.
                var pattern = $@"\b(?:for|of|to)\s+{System.Text.RegularExpressions.Regex.Escape(key)}(?![a-z0-9])";
                if (System.Text.RegularExpressions.Regex.IsMatch(normalizedText, pattern))
                {
                    bestTitle = key;
                    bestLength = key.Length;
                }
            }

            if (bestTitle == null) return null;

            var band = ResolveTitleForMinBound(bestTitle);
            if (band != null)
            {
                _logger.LogInformation($"🎯 Grade mention detected in question text: '{bestTitle}' -> band '{band}'");
            }
            return band;
        }

        private string? ResolveTitle(string title, bool preferMin)
        {
            var key = Normalize(title);
            if (!_titleLookup.TryGetValue(key, out var bands) || bands.Count == 0)
            {
                _logger.LogDebug($"Title '{title}' not found in grade hierarchy");
                return null;
            }

            if (bands.Count == 1)
                return bands[0];

            // Ambiguous — appears in more than one band. AmbiguousTitles
            // keys are normalized the same way so "Asst. Manager" and
            // "Assistant Manager" both hit the same override entry.
            var ambiguousMatch = _config.AmbiguousTitles
                .FirstOrDefault(kv => Normalize(kv.Key) == key);
            if (ambiguousMatch.Value != null)
            {
                var preferred = preferMin ? ambiguousMatch.Value.PreferForMinBound : ambiguousMatch.Value.PreferForMaxBound;
                if (!string.IsNullOrEmpty(preferred) && bands.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                    return preferred;
            }

            // No explicit override configured — fall back to the
            // inclusive-by-default rule: lowest band for a min-bound
            // context, highest band for a max-bound context.
            var ranked = bands
                .Select(b => (Band: b, Rank: RankOf(b) ?? int.MaxValue))
                .OrderBy(x => x.Rank)
                .ToList();

            var fallback = preferMin ? ranked.First().Band : ranked.Last().Band;

            _logger.LogWarning(
                $"⚠️ Title '{title}' is ambiguous across bands [{string.Join(", ", bands)}] with no explicit override — defaulting to '{fallback}' ({(preferMin ? "lowest" : "highest")}, inclusive-by-default rule)");

            return fallback;
        }
    }
}