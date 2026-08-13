using MEAI_GPT_API.Models;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service
{
    /// <summary>
    /// Resolves "Annexure N of {policy}" references to an actual file path on
    /// the configured network share. Deliberately does NOT construct a path
    /// with a guessed extension — annexures are pushed to the server in mixed
    /// formats (pdf/doc/docx/xls/xlsx), so this looks at what's actually there
    /// and returns null rather than fabricate a link that won't open.
    /// </summary>
    public class AnnexureLinkService
    {
        private readonly AnnexureLinkOptions _options;
        private readonly ILogger<AnnexureLinkService> _logger;

        // (resolved path or null, when it was checked) — null results are
        // cached too, so a missing file doesn't cause a filesystem hit on
        // every single query that mentions it.
        private readonly ConcurrentDictionary<string, (string? Path, DateTime Cached)> _cache = new();

        public AnnexureLinkService(IOptions<AnnexureLinkOptions> options, ILogger<AnnexureLinkService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Looks for a file matching "Annexure {annexureNumber}.*" (allowing
        /// "Annexure2", "Annexure_2", "Annexure-2", "Annex 2", etc.) under
        /// {BaseServerPath}\{policyName}\, restricted to AllowedExtensions.
        /// Returns the first match's full path, or null if the feature is
        /// disabled, the folder isn't reachable, or nothing matches.
        /// </summary>
        public string? ResolveAnnexureLink(string policyName, string annexureNumber)
        {
            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseServerPath))
                return null;

            if (string.IsNullOrWhiteSpace(policyName) || string.IsNullOrWhiteSpace(annexureNumber))
                return null;

            var safePolicyName = SanitizePathSegment(policyName);
            var cacheKey = $"{safePolicyName}::{annexureNumber}";

            if (_cache.TryGetValue(cacheKey, out var cached) &&
                DateTime.UtcNow - cached.Cached < TimeSpan.FromMinutes(_options.CacheMinutes))
            {
                return cached.Path;
            }

            string? resolved = null;

            try
            {
                var folderPath = Path.Combine(_options.BaseServerPath, safePolicyName);

                if (Directory.Exists(folderPath))
                {
                    var pattern = new Regex(
                        $@"^annex(?:ure)?[\s_\-]*{Regex.Escape(annexureNumber)}\b",
                        RegexOptions.IgnoreCase);

                    resolved = Directory.EnumerateFiles(folderPath)
                        .Where(f => _options.AllowedExtensions.Contains(
                            Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                        .FirstOrDefault(f => pattern.IsMatch(Path.GetFileNameWithoutExtension(f)));

                    if (resolved == null)
                    {
                        _logger.LogInformation(
                            "No annexure file found for '{Policy}' Annexure {Num} under {Folder}",
                            policyName, annexureNumber, folderPath);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Annexure server folder not reachable (check the app server has access to this UNC path): {Folder}",
                        folderPath);
                }
            }
            catch (Exception ex)
            {
                // Network share hiccups shouldn't break the answer itself —
                // just mean no link gets attached this time.
                _logger.LogWarning(ex, "Failed to resolve annexure link for '{Policy}' Annexure {Num}", policyName, annexureNumber);
            }

            _cache[cacheKey] = (resolved, DateTime.UtcNow);
            return resolved;
        }

        private static string SanitizePathSegment(string input)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                input = input.Replace(c, ' ');
            return input.Trim();
        }
    }
}