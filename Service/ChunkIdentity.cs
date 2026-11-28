// Services/ChunkIdentity.cs
//
// 1. StableChunkId      – deterministic chunk IDs (string.GetHashCode() changes on every restart,
//                         so the same chunk got a new ID each run and could be indexed twice).
// 2. PolicyDates        – extracts a policy's effective date from its own text
//                         ("The revision in this policy will be effective from 1st April 2026").
// 3. FreshnessScorer    – small ranking bonus for recent policies, penalty for not-yet-effective ones.
// 4. SupersededVersionFilter – when two versions of the same policy are retrieved
//                         (e.g. LTA Policy .doc and .docx, or Rev00 / Rev04), keep only the newest.
//
// Requires one new property on RelevantChunk:   public int EffectiveDate { get; set; }

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Services
{
    public static class ChunkIdentity
    {
        /// <summary>Same file + same text + same model always gives the same ID.</summary>
        public static string StableChunkId(string sourceFile, string text, string modelName)
        {
            var key = $"{Normalize(sourceFile)}\n{(text ?? "").Trim()}\n{modelName}";
            var name = Path.GetFileNameWithoutExtension(sourceFile ?? "chunk");
            return $"{name}_{ShortHash(key)}";
        }

        public static string ShortHash(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes)[..16];
        }

        private static string Normalize(string path) =>
            (path ?? "").Replace('\\', '/').ToLowerInvariant();
    }

    public static class PolicyDates
    {
        private static readonly Regex TextMonth = new(
            @"effective(?:\s+date)?\s*(?:from|on|:)?\s*(?:the\s+)?(\d{1,2})(?:st|nd|rd|th)?[\s\-]+(?:of\s+)?([A-Za-z]{3,9})\.?,?[\s\-]+(\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Numeric = new(
            @"(?:effective|w\.?e\.?f\.?)\s*(?:from|on|:)?\s*(\d{1,2})[./\-](\d{1,2})[./\-](\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] Months =
            { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>
        /// Returns yyyymmdd (e.g. 20260401) or 0 if no effective date is found.
        /// Chunks that sit under an "EFFECTIVE DATE" heading are trusted first; if a policy
        /// lists several dates the latest one (the current revision) wins.
        /// </summary>
        public static int ExtractEffectiveDate(IEnumerable<string> chunkTexts)
        {
            var texts = chunkTexts?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
            if (texts.Count == 0) return 0;

            var preferred = texts
                .Where(t => t.Contains("EFFECTIVE DATE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var found = Scan(preferred);
            if (found == 0) found = Scan(texts);
            return found;
        }

        private static int Scan(IEnumerable<string> texts)
        {
            var best = 0;
            var maxAllowed = DateTime.Today.AddYears(3);

            foreach (var text in texts)
            {
                foreach (Match m in TextMonth.Matches(text))
                {
                    var d = ParseTextDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                    if (d.HasValue && d.Value.Year >= 2000 && d.Value <= maxAllowed)
                        best = Math.Max(best, ToInt(d.Value));
                }

                foreach (Match m in Numeric.Matches(text))
                {
                    if (int.TryParse(m.Groups[1].Value, out var day) &&
                        int.TryParse(m.Groups[2].Value, out var month) &&
                        int.TryParse(m.Groups[3].Value, out var year) &&
                        month is >= 1 and <= 12 && day is >= 1 and <= 31 && year >= 2000)
                    {
                        try
                        {
                            var d = new DateTime(year, month, day);
                            if (d <= maxAllowed) best = Math.Max(best, ToInt(d));
                        }
                        catch (ArgumentOutOfRangeException) { }
                    }
                }
            }

            return best;
        }

        private static DateTime? ParseTextDate(string day, string month, string year)
        {
            var idx = Array.IndexOf(Months, month.Length >= 3 ? month[..3].ToLowerInvariant() : month.ToLowerInvariant());
            if (idx < 0) return null;
            if (!int.TryParse(day, out var d) || !int.TryParse(year, out var y)) return null;

            try { return new DateTime(y, idx + 1, d); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public static int ToInt(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;

        public static DateTime? ToDate(int yyyymmdd)
        {
            if (yyyymmdd < 20000101) return null;
            try { return new DateTime(yyyymmdd / 10000, yyyymmdd / 100 % 100, yyyymmdd % 100); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    public static class FreshnessScorer
    {
        /// <summary>
        /// Small, additive ranking adjustment. Deliberately tiny: it should break ties between
        /// versions or similar policies, never override relevance.
        /// 0 when the policy has no known effective date.
        /// </summary>
        public static double Boost(int effectiveDate, DateTime today)
        {
            var date = PolicyDates.ToDate(effectiveDate);
            if (!date.HasValue) return 0;

            var ageDays = (today - date.Value).TotalDays;
            if (ageDays < 0) return -0.10;      // not yet in force
            if (ageDays <= 365) return 0.05;
            if (ageDays <= 730) return 0.03;
            if (ageDays <= 1460) return 0.01;
            return 0;
        }
    }

    public static class SupersededVersionFilter
    {
        private static readonly Regex VersionToken = new(@"\b(rev|v|ver|version)\s?\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);

        /// <summary>
        /// If the pool holds several versions of the same policy (same name once the extension and
        /// Rev/V tokens are removed) and at least two have known effective dates, drop the older ones.
        /// Documents without a known date are never dropped.
        /// </summary>
        public static List<RelevantChunk> Apply(List<RelevantChunk> chunks)
        {
            if (chunks == null || chunks.Count < 2) return chunks ?? new List<RelevantChunk>();

            var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in chunks.GroupBy(c => VersionKey(c.Source)))
            {
                var perSource = group
                    .GroupBy(c => c.Source ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Source: g.Key, Effective: g.Max(c => c.EffectiveDate)))
                    .ToList();

                if (perSource.Count < 2) continue;

                var newest = perSource.Max(s => s.Effective);
                if (newest <= 0) continue;

                foreach (var s in perSource.Where(s => s.Effective > 0 && s.Effective < newest))
                    drop.Add(s.Source);
            }

            return drop.Count == 0
                ? chunks
                : chunks.Where(c => !drop.Contains(c.Source ?? "")).ToList();
        }

        private static string VersionKey(string? source)
        {
            var name = Path.GetFileNameWithoutExtension(source ?? "").ToLowerInvariant();
            name = VersionToken.Replace(name, " ");
            return NonAlnum.Replace(name, " ").Trim();
        }
    }
}