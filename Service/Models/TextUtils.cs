namespace MEAI_GPT_API.Service.Models
{
    public static class TextUtils
    {
        public static bool IsCommonWord(string word)
        {
            var commonWords = new[]
            {
                "a", "an", "the", "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
                "my", "your", "his", "her", "its", "our", "their", "mine", "yours", "hers", "ours", "theirs",
                "this", "that", "these", "those", "who", "what", "which", "when", "where", "why", "how",
                "in", "on", "at", "by", "for", "with", "about", "to", "from", "of", "into", "onto", "upon",
                "and", "or", "but", "so", "yet", "nor", "because", "since", "although", "though",
                "is", "am", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does",
                "good", "bad", "big", "small", "new", "old", "first", "last", "long", "short", "high", "low",
                "tell", "show", "explain", "describe", "list", "give", "provide", "find", "search"
            };
            return commonWords.Contains(word.ToLowerInvariant());
        }

        public static double CalculateTextSimilarity(string text1, string text2)
        {
            var words1 = text1.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var words2 = text2.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        public static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
            var charCount = text.Length;

            return (int)Math.Ceiling(wordCount * 1.3 + charCount / 5.0);
        }

        public static int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            var d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;

            for (int i = 1; i <= s.Length; i++)
            {
                for (int j = 1; j <= t.Length; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = new[] {
                        d[i - 1, j] + 1,
                        d[i, j - 1] + 1,
                        d[i - 1, j - 1] + cost
                    }.Min();
                }
            }

            return d[s.Length, t.Length];
        }
        public static double CalculateAdvancedSimilarity(string text1, string text2)
        {
            // Jaccard similarity for word overlap
            var words1 = text1.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !TextUtils.IsCommonWord(w))
                .ToHashSet();

            var words2 = text2.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !TextUtils.IsCommonWord(w))
                .ToHashSet();

            if (!words1.Any() || !words2.Any()) return 0;

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            var jaccardSim = (double)intersection / union;

            // Boost similarity if question structures are similar
            var structuralBoost = GetStructuralSimilarity(text1, text2);

            return Math.Min(1.0, jaccardSim + structuralBoost);
        }
        public static double GetStructuralSimilarity(string q1, string q2)
        {
            var q1Lower = q1.ToLowerInvariant().Trim();
            var q2Lower = q2.ToLowerInvariant().Trim();

            // Question starters
            string[] questionStarters = { "what", "how", "can", "could", "would", "tell", "give", "show" };

            var q1Starter = questionStarters.FirstOrDefault(s => q1Lower.StartsWith(s));
            var q2Starter = questionStarters.FirstOrDefault(s => q2Lower.StartsWith(s));

            if (q1Starter != null && q1Starter == q2Starter)
            {
                return 0.1; // Small boost for same question pattern
            }

            return 0;
        }
    }
}
