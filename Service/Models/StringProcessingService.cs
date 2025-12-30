using MEAI_GPT_API.Models;
using MEAI_GPT_API.Services;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service.Models
{
    public class StringProcessingService
    {
        private readonly ILogger<DynamicRagService> _logger;
        private readonly ConversationAnalysisService _conversationAnalysis;

        public StringProcessingService(ILogger<DynamicRagService> logger, ConversationAnalysisService conversationAnalysis)
        {
            _logger = logger;
            _conversationAnalysis = conversationAnalysis;
        }

        /// <summary>
        /// Clean text for embedding generation - FIXED VERSION
        /// </summary>
        public string CleanTextForEmbedding(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Empty text provided to CleanTextForEmbedding");
                return "";
            }

            var originalLength = text.Length;
            var isNomicModel = model.Name.Contains("nomic", StringComparison.OrdinalIgnoreCase);

            try
            {
                var cleaned = text;

                var tokensToRemove = new[]
       {
            // Llama/Mistral tokens
            "<|im_start|>", "<|im_end|>",
            "<|endoftext|>", "<|startoftext|>",
            
            // Instruction tokens
            "[INST]", "[/INST]",
            "<<SYS>>", "<</SYS>>",
            
            // Generic tokens
            "<s>", "</s>",
            "<assistant>", "</assistant>",
            "<user>", "</user>",
            "<|user|>", "<|assistant|>",
            
            // GPT-style tokens
            "<|im_sep|>",
            
            // Additional markers
            "###", "***", "---"
        };

                foreach (var token in tokensToRemove)
                {
                    cleaned = cleaned.Replace(token, " ", StringComparison.OrdinalIgnoreCase);
                }

                // STEP 1: Remove only truly problematic control characters (NOT content!)
                // Only remove C0 control characters except tab, newline, carriage return
                cleaned = Regex.Replace(cleaned, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");

                // STEP 2: Normalize Unicode characters (don't remove them!)
                cleaned = cleaned
                    // Smart quotes to regular quotes
                    .Replace("\u201C", "\"")  // Left double quote
                    .Replace("\u201D", "\"")  // Right double quote
                    .Replace("\u2018", "'")   // Left single quote
                    .Replace("\u2019", "'")   // Right single quote
                                              // Dashes to hyphens
                    .Replace("\u2013", "-")   // En dash
                    .Replace("\u2014", "-")   // Em dash
                    .Replace("\u2212", "-")   // Minus sign
                                              // Other common symbols
                    .Replace("\u2026", "...") // Ellipsis
                    .Replace("\u2022", "*")   // Bullet
                    .Replace("\u25AA", "*");  // Small bullet

                // STEP 3: Remove metadata headers ONLY if they're at the start of lines
                // This preserves actual content that mentions "SECTION" or "DOCUMENT"
                cleaned = Regex.Replace(cleaned,
                    @"^DOCUMENT\s+SECTION\s+[\d.]+\s*$",
                    "",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);

                cleaned = Regex.Replace(cleaned,
                    @"^SECTION\s+TITLE\s+.*?$",
                    "",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);

                cleaned = Regex.Replace(cleaned,
                    @"^DOCUMENT\s+TYPE\s+.*?$",
                    "",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);

                cleaned = Regex.Replace(cleaned,
                    @"^CONTENT\s*$",
                    "",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);

                cleaned = Regex.Replace(cleaned,
                    @"^KEYWORDS\s+.*?$",
                    "",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);

                // STEP 4: Normalize whitespace (but don't remove content!)
                cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");        // Multiple spaces to single
                cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");     // Multiple newlines to double

                cleaned = Regex.Replace(cleaned, @"```[\w]*\n", "", RegexOptions.IgnoreCase);
                cleaned = cleaned.Replace("```", "");

                // Remove excessive whitespace and newlines
                cleaned = Regex.Replace(cleaned, @"\s+", " ");

                // Remove control characters
                cleaned = Regex.Replace(cleaned, @"[\x00-\x08\x0B-\x0C\x0E-\x1F\x7F]", "");

                // Remove any remaining special characters that might cause issues
                cleaned = Regex.Replace(cleaned, @"[^\w\s\.\,\!\?\;\:\-\(\)\[\]\{\}\'\""]", " ");

                // Normalize quotes
                cleaned = cleaned.Replace(""", "\"").Replace(""", "\"");
                cleaned = cleaned.Replace("'", "'").Replace("'", "'");

                cleaned = cleaned.Trim();

                // STEP 5: Validate we didn't destroy the content
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    _logger.LogWarning($"⚠️ Text cleaning resulted in empty string for model {model.Name}");
                    _logger.LogWarning($"Original text ({originalLength} chars): {text.Substring(0, Math.Min(200, text.Length))}");

                    // FALLBACK: Return minimally cleaned text instead of empty string
                    cleaned = text
                        .Replace("\x00", "")  // Only remove null characters
                        .Trim();

                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        _logger.LogError($"❌ Text is completely empty even after fallback cleaning");
                        return "";
                    }

                    _logger.LogInformation($"✅ Using fallback cleaning, {cleaned.Length} chars retained");
                }

                // STEP 6: Handle length limits for different models
                int maxLength = isNomicModel ? 8000 : 4000;  // Increased limits

                if (cleaned.Length > maxLength)
                {
                    _logger.LogDebug($"Text length {cleaned.Length} exceeds max {maxLength}, truncating");

                    // Try to cut at sentence boundary
                    var sentences = cleaned.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
                    var truncated = new System.Text.StringBuilder();

                    foreach (var sentence in sentences)
                    {
                        if (truncated.Length + sentence.Length + 2 <= maxLength)
                        {
                            truncated.Append(sentence);
                            truncated.Append(". ");
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (truncated.Length > 100)
                    {
                        cleaned = truncated.ToString().Trim();
                    }
                    else
                    {
                        // Fallback: hard truncation
                        cleaned = cleaned.Substring(0, maxLength);
                    }
                }

                // STEP 7: Final validation
                if (cleaned.Length < 10)
                {
                    _logger.LogWarning($"⚠️ Cleaned text is very short ({cleaned.Length} chars) for model {model.Name}");
                }

                _logger.LogDebug($"✅ Cleaned text for {model.Name}: {originalLength} → {cleaned.Length} chars");
                return cleaned;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in CleanTextForEmbedding for model {model.Name}");

                // On error, return minimally cleaned text
                return text.Replace("\x00", "").Trim();
            }
        }

        /// <summary>
        /// General text cleaning for document processing
        /// </summary>
        public string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            try
            {
                // Normalize Unicode characters
                text = text
                    .Replace("\u201C", "\"")  // Smart quotes
                    .Replace("\u201D", "\"")
                    .Replace("\u2018", "'")
                    .Replace("\u2019", "'")
                    .Replace("\u2013", "-")   // Dashes
                    .Replace("\u2014", "-")
                    .Replace("\u2212", "-")
                    .Replace("\u2026", "...") // Ellipsis
                    .Replace("\u2022", "*")   // Bullets
                    .Replace("\u25AA", "*");

                // Normalize line endings
                text = text.Replace("\r\n", "\n").Replace("\r", "\n");

                // Normalize whitespace (but preserve newlines)
                text = Regex.Replace(text, @"[ \t]+", " ");

                // Remove excessive newlines
                text = Regex.Replace(text, @"\n{3,}", "\n\n");

                return text.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CleanText");
                return text.Trim();
            }
        }

        /// <summary>
        /// Normalize text for embedding - removes metadata headers while preserving content
        /// </summary>
        public string NormalizeForEmbedding(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            try
            {
                var normalized = text;

                // Remove metadata header block if present (common pattern from your chunking)
                // Example: "DOCUMENT SECTION 4.11.1 SECTION TITLE Server Equipment DOCUMENT TYPE Policy CONTENT actual content here"
                var metadataPattern = @"^DOCUMENT\s+SECTION.*?CONTENT\s+";
                normalized = Regex.Replace(normalized, metadataPattern, "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                // Remove keywords footer if present
                normalized = Regex.Replace(normalized, @"\s+KEYWORDS\s+.*$", "", RegexOptions.IgnoreCase);

                // Normalize whitespace
                normalized = Regex.Replace(normalized, @"\s+", " ");
                normalized = normalized.Trim();

                // Validate
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    _logger.LogWarning("NormalizeForEmbedding resulted in empty text, using original");
                    return text.Trim();
                }

                return normalized;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in NormalizeForEmbedding");
                return text.Trim();
            }
        }

        /// <summary>
        /// Clean copy-paste text from Word/Notepad
        /// </summary>
        public string CleanCopyPasteText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Fix common Word-to-Notepad conversion issues
            var cleaned = text
                .Replace("â€œ", "\"")     // Smart quotes
                .Replace("â€", "\"")
                .Replace("â€™", "'")
                .Replace("â€¢", "•")
                .Replace("Â ", " ")
                .Replace("—", "–")
                .Replace("--", "–")
                .Replace(" - ", " – ");

            // Fix section headers
            for (int i = 1; i <= 20; i++)
            {
                cleaned = cleaned
                    .Replace($"Section{i}", $"Section {i}")
                    .Replace($"Section\n{i}", $"Section {i}");
            }

            // Fix line breaks
            cleaned = cleaned
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

            // Fix broken section numbering
            var sectionHeaderPattern = @"Section\s*(\d+)\s*[–\-—]?\s*(.+)";
            cleaned = Regex.Replace(
                cleaned,
                sectionHeaderPattern,
                "Section $1 – $2",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            // Remove excessive whitespace
            cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

            return cleaned.Trim();
        }

        /// <summary>
        /// Calculate similarity between two strings
        /// </summary>
        public double CalculateTextSimilarity(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
                return 0;

            // Use Levenshtein distance
            var longer = text1.Length > text2.Length ? text1 : text2;
            var shorter = text1.Length > text2.Length ? text2 : text1;

            if (longer.Length == 0)
                return 1.0;

            var distance = ComputeLevenshteinDistance(longer, shorter);
            return (longer.Length - distance) / (double)longer.Length;
        }

        private int ComputeLevenshteinDistance(string s, string t)
        {
            var n = s.Length;
            var m = t.Length;
            var d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (var i = 0; i <= n; i++)
                d[i, 0] = i;

            for (var j = 0; j <= m; j++)
                d[0, j] = j;

            for (var i = 1; i <= n; i++)
            {
                for (var j = 1; j <= m; j++)
                {
                    var cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        public int EstimateTokenCount(string text) => TextUtils.EstimateTokenCount(text);
    }
}