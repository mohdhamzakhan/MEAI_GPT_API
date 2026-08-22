using System.Text.Json;

namespace MEAI_GPT_API.Service
{
    public class TopicAnchor
    {
        // Any of these phrases appearing ANYWHERE in the query (substring
        // match, not word-for-word like AbbreviationExpansionService)
        // triggers this anchor. Put multiple real phrasings of the same
        // concept here -- "resign", "resigning", "notice period", "last
        // working day", "relieving", "full and final", "F&F" can all point
        // at the same document, since a real employee will phrase this
        // differently every time and a single dictionary word never covers
        // that.
        public List<string> Triggers { get; set; } = new();

        // Text folded into an additional anchored search query (see
        // GetRelevantChunksWithExpansionAsync) to pull the right document
        // back into the candidate pool even when the raw question's
        // wording doesn't score it highly on its own. Doesn't need to be
        // the exact filename -- just wording likely to appear in that
        // document's own title/content, since this is used to bias an
        // embedding search, not to do an exact filename match.
        public string AnchorText { get; set; } = "";
    }

    // Fixes a different failure mode than AbbreviationExpansionService:
    // that service resolves ONE fixed short-form word ("EL" -> "Earned
    // Leave") via word-for-word dictionary lookup. This service resolves a
    // WHOLE CONCEPT that gets phrased many different ways -- e.g. a
    // question about resignation could say "resigning", "resignation",
    // "notice period", "last working day", "relieving", "F&F", "full and
    // final settlement" -- none of which is a single fixed abbreviation,
    // so the word-for-word matcher can't catch it. This checks the whole
    // query text for ANY of a list of trigger phrases per concept.
    public class TopicAnchorService
    {
        private readonly List<TopicAnchor> _anchors;
        private readonly ILogger<TopicAnchorService> _logger;

        public TopicAnchorService(ILogger<TopicAnchorService> logger, string configFilePath)
        {
            _logger = logger;
            _anchors = LoadAnchors(configFilePath);
        }

        private List<TopicAnchor> LoadAnchors(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning($"Topic anchor file not found: {filePath} -- topic anchoring disabled, no error, just no effect.");
                    return new List<TopicAnchor>();
                }

                var json = File.ReadAllText(filePath);
                var anchors = JsonSerializer.Deserialize<List<TopicAnchor>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new List<TopicAnchor>();

                _logger.LogInformation($"Loaded {anchors.Count} topic anchors from {filePath}");
                return anchors;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load topic anchors from {filePath} -- topic anchoring disabled, no error, just no effect.");
                return new List<TopicAnchor>();
            }
        }

        // Returns the AnchorText for every anchor whose trigger phrase
        // appears anywhere in the query (case-insensitive substring match).
        // Multiple anchors can fire for one query -- e.g. a question that
        // mentions both resignation AND gratuity would anchor toward both
        // documents.
        public List<string> GetMatchingAnchors(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _anchors.Count == 0)
                return new List<string>();

            var matched = new List<string>();
            foreach (var anchor in _anchors)
            {
                if (anchor.Triggers.Any(t => query.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    matched.Add(anchor.AnchorText);
                }
            }
            return matched.Distinct().ToList();
        }
    }
}