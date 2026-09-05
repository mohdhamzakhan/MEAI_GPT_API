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

    public class GradeHierarchyService
    {
        private readonly ILogger<GradeHierarchyService> _logger;
        private readonly GradeHierarchyConfig _config;

        // title (lowercased, trimmed) -> set of bands it appears in
        private readonly Dictionary<string, List<string>> _titleLookup;

        public GradeHierarchyService(IConfiguration configuration, ILogger<GradeHierarchyService> logger)
        {
            _logger = logger;
            _config = LoadConfig(configuration);
            _titleLookup = BuildTitleLookup(_config);

            _logger.LogInformation(
                $"📊 Grade hierarchy loaded: {_config.Bands.Count} bands, {_titleLookup.Count} distinct titles, {_config.AmbiguousTitles.Count} flagged as ambiguous");
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

        private Dictionary<string, List<string>> BuildTitleLookup(GradeHierarchyConfig config)
        {
            var lookup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (band, titles) in config.TitleToBand)
            {
                foreach (var title in titles)
                {
                    var key = title.Trim();
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

        private string? ResolveTitle(string title, bool preferMin)
        {
            var key = title.Trim();
            if (!_titleLookup.TryGetValue(key, out var bands) || bands.Count == 0)
            {
                _logger.LogDebug($"Title '{title}' not found in grade hierarchy");
                return null;
            }

            if (bands.Count == 1)
                return bands[0];

            // Ambiguous — appears in more than one band.
            if (_config.AmbiguousTitles.TryGetValue(key, out var resolution))
            {
                var preferred = preferMin ? resolution.PreferForMinBound : resolution.PreferForMaxBound;
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