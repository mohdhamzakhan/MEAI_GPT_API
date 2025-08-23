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
        public string CleanCopyPasteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 🔧 STEP 1: Fix common Word-to-Notepad conversion issues
            var cleaned = text
                // Fix dash variations (common in Word-to-Notepad copy
                .Replace("â€œ", "\"")     // Common encoding for smart quotes
                .Replace("â€", "\"")      // Another smart quote variant
                .Replace("â€™", "'")      // Smart apostrophe
                .Replace("â€¢", "•")      // Bullet point
                .Replace("Â ", " ")       // Non-breaking space issues

                // Normalize different dash types to standard em dash
                .Replace("—", "–")        // En dash to em dash
                .Replace("--", "–")       // Double hyphen to em dash
                .Replace(" - ", " – ")    // Spaced hyphen to spaced em dash

                // Fix section header patterns that might be broken
                .Replace("Section1", "Section 1")
                .Replace("Section2", "Section 2")
                .Replace("Section3", "Section 3")
                .Replace("Section4", "Section 4")
                .Replace("Section5", "Section 5")
                .Replace("Section6", "Section 6")
                .Replace("Section7", "Section 7")
                .Replace("Section8", "Section 8")
                .Replace("Section9", "Section 9")
                .Replace("Section10", "Section 10")
                .Replace("Section11", "Section 11")
                .Replace("Section12", "Section 12")
                .Replace("Section13", "Section 13")
                .Replace("Section14", "Section 14")
                .Replace("Section15", "Section 15")
                .Replace("Section16", "Section 16")
                .Replace("Section17", "Section 17")
                .Replace("Section18", "Section 18")
                .Replace("Section19", "Section 19")
                .Replace("Section20", "Section 20")
                .Replace("Section10", "Section 10")

                ;

            // 🔧 STEP 2: Fix line break issues
            cleaned = cleaned
                .Replace("\r\n", "\n")    // Normalize line endings
                .Replace("\r", "\n")      // Handle old Mac line endings

                // Fix cases where section headers got split across lines
                .Replace("Section\n1", "Section 1")
                .Replace("Section\n2", "Section 2")

                .Replace("Section\n3", "Section 3")
                .Replace("Section\n4", "Section 4")
                .Replace("Section\n5", "Section 5")
                .Replace("Section\n6", "Section 6")
                .Replace("Section\n7", "Section 7")
                .Replace("Section\n8", "Section 8")
                .Replace("Section\n9", "Section 9")
                .Replace("Section\n10", "Section 10")
                .Replace("Section\n11", "Section 11")
                .Replace("Section\n12", "Section 12")
                .Replace("Section\n13", "Section 13")
                .Replace("Section\n14", "Section 14")
                .Replace("Section\n15", "Section 15")
                .Replace("Section\n16", "Section 16")
                .Replace("Section\n17", "Section 17")
                .Replace("Section\n18", "Section 18")
                .Replace("Section\n19", "Section 19")
                .Replace("Section\n20", "Section 20")

                ;

            // 🔧 STEP 3: Fix broken section numbering
            var sectionHeaderPattern = @"Section\s*(\d+)\s*[–\-—]?\s*(.+)";
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                sectionHeaderPattern,
                "Section $1 – $2",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            // 🔧 STEP 4: Remove excessive whitespace but preserve structure
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[ \t]+", " ");  // Multiple spaces to single
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n"); // Multiple newlines to double

            return cleaned.Trim();
        }
        public string CleanTextForEmbedding(string text, ModelConfiguration model)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var isNomicModel = model.Name.Contains("nomic", StringComparison.OrdinalIgnoreCase);

            // 🔧 STEP 1: Remove problematic characters that might cause encoding issues
            var cleaned = text

                .Replace("\u0000", "") // Null character
                .Replace("\u0001", "") // Start of heading
                .Replace("\u0002", "") // Start of text
                .Replace("\u0003", "") // End of text
                .Replace("\u0004", "") // End of transmission
                .Replace("\u0005", "") // Enquiry
                .Replace("\u0006", "") // Acknowledge
                .Replace("\u0007", "") // Bell
                .Replace("\u0008", "") // Backspace
                .Replace("\u000B", " ") // Vertical tab -> space
                .Replace("\u000C", " ") // Form feed -> space
                .Replace("\u000E", "") // Shift out
                .Replace("\u000F", "") // Shift in
                .Replace("\u0010", "") // Data link escape
                .Replace("\u0011", "") // Device control 1
                .Replace("\u0012", "") // Device control 2
                .Replace("\u0013", "") // Device control 3
                .Replace("\u0014", "") // Device control 4
                .Replace("\u0015", "") // Negative acknowledge
                .Replace("\u0016", "") // Synchronous idle
                .Replace("\u0017", "") // End of transmission block
                .Replace("\u0018", "") // Cancel
                .Replace("\u0019", "") // End of medium
                .Replace("\u001A", "") // Substitute
                .Replace("\u001B", "") // Escape
                .Replace("\u001C", "") // File separator
                .Replace("\u001D", "") // Group separator
                .Replace("\u001E", "") // Record separator
                .Replace("\u001F", "") // Unit separator

                // Replace various dash types with standard hyphen
                .Replace("–", "-")  // em dash
                .Replace("—", "-")  // en dash  
                .Replace("−", "-")  // minus sign
                                    // Replace smart quotes - FIXED with Unicode escapes
                .Replace("\u201C", "\"")  // Left double quotation mark
                .Replace("\u201D", "\"")  // Right double quotation mark
                .Replace("\u2018", "'")   // Left single quotation mark
                .Replace("\u2019", "'")   // Right single quotation mark
                                          // Remove other problematic Unicode characters
                .Replace("…", "...")
                .Replace("•", "*")
                .Replace("▪", "*");

            // Rest of your existing method...

            // 🔧 STEP 2: Normalize whitespace
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            cleaned = cleaned.Trim();

            // 🔧 STEP 3: Remove non-printable characters
            cleaned = Regex.Replace(cleaned, @"[^\x20-\x7E\t\n\r]", " ");

            // 🔧 STEP 4: Ensure proper length limits for different models
            int maxLength = isNomicModel ? 2000 : 1000;
            if (cleaned.Length > maxLength)
            {
                // Try to cut at sentence boundary
                var sentences = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var truncated = "";
                foreach (var sentence in sentences)
                {
                    if (truncated.Length + sentence.Length + 1 <= maxLength)
                    {
                        truncated += sentence + ".";
                    }
                    else
                    {
                        break;
                    }
                }

                if (truncated.Length > 10)
                {
                    cleaned = truncated;
                }
                else
                {
                    cleaned = cleaned.Substring(0, maxLength);
                }
            }

            // 🔧 STEP 5: Final validation
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                _logger.LogWarning($"⚠️ Text cleaning resulted in empty string for model {model.Name}");
                return "empty content";
            }

            _logger.LogDebug($"✅ Cleaned text for {model.Name}: {cleaned.Length} chars");
            return cleaned;
        }
        public string CleanText(string text)
        {
            // Replace problematic Unicode characters first
            text = text
        // Replace various dash types with standard hyphen
        .Replace("–", "-")  // em dash
        .Replace("—", "-")  // en dash  
        .Replace("−", "-")  // minus sign
                            // Replace smart quotes - FIXED with Unicode escapes
        .Replace("\u201C", "\"")  // Left double quotation mark
        .Replace("\u201D", "\"")  // Right double quotation mark
        .Replace("\u2018", "'")   // Left single quotation mark
        .Replace("\u2019", "'")   // Right single quotation mark
                                  // Remove other problematic Unicode characters
        .Replace("…", "...")
        .Replace("•", "*")
        .Replace("▪", "*");

            // Clean whitespace and special characters
            text = Regex.Replace(text, @"\s+", " ");
            text = Regex.Replace(text, @"[^\w\s.,!?-]", "");
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            return text.Trim();
        }
        public int EstimateTokenCount(string text) => TextUtils.EstimateTokenCount(text);
        public double CalculateTextSimilarity(string text1, string text2) => TextUtils.CalculateTextSimilarity(text1, text2);
    }
}
