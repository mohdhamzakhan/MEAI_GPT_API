using System.Text;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service
{
    public class AbbreviationExpansionService
    {
        private readonly Dictionary<string, List<string>> _abbreviationMap;
        private readonly Dictionary<string, List<string>> _reverseMap;
        private readonly ILogger<AbbreviationExpansionService> _logger;

        public AbbreviationExpansionService(ILogger<AbbreviationExpansionService> logger, string abbreviationFilePath)
        {
            _logger = logger;
            _abbreviationMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _reverseMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            LoadAbbreviations(abbreviationFilePath);
        }

        private void LoadAbbreviations(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning($"Abbreviation file not found: {filePath}");
                    return;
                }

                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//") || !line.Contains("="))
                        continue;

                    var parts = line.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var abbreviation = parts[0].Trim();
                        var fullForm = parts[1].Trim();

                        // Add to abbreviation map
                        if (!_abbreviationMap.ContainsKey(abbreviation))
                            _abbreviationMap[abbreviation] = new List<string>();
                        _abbreviationMap[abbreviation].Add(fullForm);

                        // Add to reverse map
                        if (!_reverseMap.ContainsKey(fullForm))
                            _reverseMap[fullForm] = new List<string>();
                        _reverseMap[fullForm].Add(abbreviation);

                        _logger.LogDebug($"Loaded: {abbreviation} -> {fullForm}");
                    }
                }

                _logger.LogInformation($"Loaded {_abbreviationMap.Count} abbreviations from {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load abbreviations from {filePath}");
            }
        }

        // Expand query to include both abbreviations and full forms

        public string ExpandQuery(string query)
        {
            var expandedQueries = new List<string> { query }; // always keep original
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                var cleanWord = word.Trim('.', ',', '?', '!', ';', ':');

                if (_abbreviationMap.TryGetValue(cleanWord, out var fullForms))
                {
                    expandedQueries.Add(query.Replace(word, fullForms.First(), StringComparison.OrdinalIgnoreCase));
                }
                else if (_reverseMap.TryGetValue(cleanWord, out var abbrevs))
                {
                    expandedQueries.Add(query.Replace(word, abbrevs.First(), StringComparison.OrdinalIgnoreCase));
                }
            }

            var result = string.Join(" || ", expandedQueries.Distinct());
            if (expandedQueries.Count > 1)
            {
                _logger.LogInformation($"Expanded query: '{query}' -> '{result}'");
            }

            return result;
        }
        public List<string> ExpandQueryList(string query)
        {
            var expanded = new List<string> { query };
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                var cleanWord = word.Trim('.', ',', '?', '!', ';', ':');

                if (_abbreviationMap.TryGetValue(cleanWord, out var fullForms))
                    expanded.Add(query.Replace(word, fullForms.First(), StringComparison.OrdinalIgnoreCase));

                if (_reverseMap.TryGetValue(cleanWord, out var abbrevs))
                    expanded.Add(query.Replace(word, abbrevs.First(), StringComparison.OrdinalIgnoreCase));
            }

            return expanded.Distinct().ToList();
        }


        private void BuildCombinations(
            List<List<string>> perTokenVariants,
            int index,
            List<string> current,
            List<string> results,
            int maxCombinations)
        {
            if (results.Count >= maxCombinations) return;

            if (index == perTokenVariants.Count)
            {
                results.Add(string.Concat(current)); // we preserved whitespace tokens, so just concat
                return;
            }

            foreach (var option in perTokenVariants[index])
            {
                if (results.Count >= maxCombinations) break;
                current.Add(option);
                BuildCombinations(perTokenVariants, index + 1, current, results, maxCombinations);
                current.RemoveAt(current.Count - 1);
            }
        }



        // Get all variations of a term
        public List<string> GetAllVariations(string term)
        {
            var variations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { term };

            if (_abbreviationMap.ContainsKey(term))
            {
                foreach (var fullForm in _abbreviationMap[term])
                    variations.Add(fullForm);
            }

            if (_reverseMap.ContainsKey(term))
            {
                foreach (var abbrev in _reverseMap[term])
                    variations.Add(abbrev);
            }

            return variations.ToList();
        }

        // Enhanced text preprocessing for embeddings
        public string PreprocessTextForEmbedding(string text)
        {
            var expandedText = new StringBuilder(text);

            // Replace abbreviations with both forms
            foreach (var kvp in _abbreviationMap)
            {
                var abbreviation = kvp.Key;
                var fullForms = kvp.Value;

                // Use word boundary regex to avoid partial matches
                var pattern = @"\b" + Regex.Escape(abbreviation) + @"\b";

                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                {
                    foreach (var fullForm in fullForms)
                    {
                        // Add both forms to increase semantic density
                        var replacement = $"{abbreviation} ({fullForm})";
                        expandedText = expandedText.Replace(abbreviation, replacement);
                    }
                }
            }

            return expandedText.ToString();
        }
    }
}
