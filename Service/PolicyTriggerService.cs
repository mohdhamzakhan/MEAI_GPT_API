// Service/Models/PolicyTriggerService.cs
//
// Auto-generates a "trigger phrase -> anchor text" map for every policy
// document during the embedding refresh cycle, and uses that map to expand
// incoming user queries — the same way AbbreviationExpansionService expands
// "MEAI" -> "Mitsubishi Electric Automotive India", but driven by an LLM
// reading each document instead of a hand-maintained text file.
//
// This exists to close gaps like: a user asks "what formalities does a
// resigned employee complete", but the actual document is titled
// "Settlement Policy" and talks about "full and final settlement" — no
// shared vocabulary, so pure embedding similarity under-ranks it even
// though it's indexed correctly. GenerateTriggersForDocumentAsync reads
// each document once and asks the LLM: "what informal, everyday phrases
// would someone use to ask about this?" — then ExpandQueryWithTriggers
// checks incoming queries against that phrase list at query time.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using MEAIGPTAPI.Services;

namespace MEAI_GPT_API.Service.Models
{
    public class PolicyTriggerEntry
    {
        [JsonPropertyName("SourceFile")]
        public string SourceFile { get; set; } = string.Empty;

        [JsonPropertyName("Triggers")]
        public List<string> Triggers { get; set; } = new();

        [JsonPropertyName("AnchorText")]
        public string AnchorText { get; set; } = string.Empty;
    }

    // Separate, smaller sidecar file: source file -> content hash, so a
    // refresh only re-calls the LLM for documents that actually changed
    // since the last run, not all ~180 policies every time.
    internal class PolicyTriggerHashRecord
    {
        [JsonPropertyName("SourceFile")]
        public string SourceFile { get; set; } = string.Empty;

        [JsonPropertyName("ContentHash")]
        public string ContentHash { get; set; } = string.Empty;
    }

    public class PolicyTriggerService
    {
        private readonly OllamaHttpClient _ollamaClient;
        private readonly ILogger<PolicyTriggerService> _logger;
        private readonly string _triggersFilePath;
        private readonly string _hashesFilePath;
        private readonly string _generationModelName;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        private List<PolicyTriggerEntry> _triggers = new();
        private Dictionary<string, string> _contentHashes = new(StringComparer.OrdinalIgnoreCase);

        public PolicyTriggerService(
            OllamaHttpClient ollamaClient,
            ILogger<PolicyTriggerService> logger,
            string triggersFilePath,
            string generationModelName)
        {
            _ollamaClient = ollamaClient;
            _logger = logger;
            _triggersFilePath = triggersFilePath;
            _hashesFilePath = Path.Combine(
                Path.GetDirectoryName(triggersFilePath) ?? ".",
                Path.GetFileNameWithoutExtension(triggersFilePath) + ".hashes.json");
            _generationModelName = generationModelName;

            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (File.Exists(_triggersFilePath))
                {
                    var json = File.ReadAllText(_triggersFilePath);
                    _triggers = JsonSerializer.Deserialize<List<PolicyTriggerEntry>>(json) ?? new();
                    _logger.LogInformation($"📋 Loaded {_triggers.Count} policy trigger entries from {_triggersFilePath}");
                }

                if (File.Exists(_hashesFilePath))
                {
                    var json = File.ReadAllText(_hashesFilePath);
                    var records = JsonSerializer.Deserialize<List<PolicyTriggerHashRecord>>(json) ?? new();
                    _contentHashes = records.ToDictionary(r => r.SourceFile, r => r.ContentHash, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load policy triggers from {_triggersFilePath} — starting empty");
                _triggers = new();
                _contentHashes = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string ComputeHash(string content)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(bytes);
        }

        /// <summary>
        /// Generates (or refreshes) trigger phrases for one document. Safe to
        /// call once per file during the refresh cycle — it no-ops if the
        /// document's content hasn't changed since the last successful run,
        /// so a full refresh doesn't re-call the LLM for every unchanged
        /// policy every time. Never throws: a trigger-generation failure
        /// must never block or fail the actual indexing pipeline.
        /// </summary>
        public async Task<bool> GenerateTriggersForDocumentAsync(string sourceFile, string content, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    return false;

                var hash = ComputeHash(content);
                if (_contentHashes.TryGetValue(sourceFile, out var existingHash) && existingHash == hash)
                {
                    _logger.LogDebug($"⏭️ Skipping trigger generation for {sourceFile} — content unchanged");
                    return false;
                }

                _logger.LogInformation($"🏷️ Generating query triggers for {sourceFile}");

                // Cap input size — we only need enough of the document to
                // identify its topic and the everyday language someone
                // would use to ask about it, not the full text.
                var excerpt = content.Length > 6000 ? content.Substring(0, 6000) : content;
                var fileTitle = Path.GetFileNameWithoutExtension(sourceFile);

                var prompt = BuildPrompt(fileTitle, excerpt);

                var requestData = new
                {
                    model = _generationModelName,
                    messages = new[]
                    {
                        new { role = "system", content = "You output ONLY valid JSON. No markdown fences, no commentary, no explanation — just the JSON array." },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.2,
                    stream = false
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // 45s, not 2 minutes: this is a lightweight background
                // enhancement, not a critical-path call. A refresh touching
                // ~180 policies can't afford to eat 2 minutes per file on
                // every hang — better to skip fast and let the next refresh
                // (with its content-hash check) pick the file back up once
                // the LLM server has recovered.
                cts.CancelAfter(TimeSpan.FromSeconds(45));

                var response = await _ollamaClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"⚠️ Trigger generation call failed for {sourceFile}: {response.StatusCode} - {err}");
                    return false;
                }

                var raw = await response.Content.ReadAsStringAsync();
                var entries = ParseTriggerResponse(raw, sourceFile);

                if (entries.Count == 0)
                {
                    _logger.LogWarning($"⚠️ No trigger entries parsed for {sourceFile} — leaving previous entries (if any) untouched");
                    return false;
                }

                // Deterministic safety net: merge simple triggers derived
                // from the filename itself into the first entry, regardless
                // of what the LLM produced. Guards against exactly the gap
                // we hit in practice — a generated list that covers
                // abbreviations and formal phrasing but omits the document's
                // own everyday name (e.g. "settlement" for a Settlement
                // Policy). Cheap, deterministic, and doesn't depend on the
                // model getting it right every time.
                var titleTriggers = DeriveTitleTriggers(fileTitle);
                foreach (var t in titleTriggers)
                {
                    if (!entries[0].Triggers.Contains(t))
                        entries[0].Triggers.Add(t);
                }

                await UpsertEntriesAsync(sourceFile, entries, hash);
                _logger.LogInformation($"✅ Generated {entries.Count} trigger group(s) for {sourceFile}");
                return true; // ✅ real call made
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"⚠️ Trigger generation timed out for {sourceFile}");
                return true;
            }
            catch (Exception ex)
            {
                // Deliberately swallowed: trigger generation is an
                // enhancement layer, not part of the critical indexing
                // path. A bad response, a malformed doc, or a model hiccup
                // here must never take down document processing the way
                // the earlier chunking bug did.
                _logger.LogError(ex, $"❌ Trigger generation failed for {sourceFile}");
                return false;
            }
        }

        private static string BuildPrompt(string fileTitle, string excerpt)
        {
            return $@"You are building a search-trigger map for an internal HR/policy chatbot.

Document title: ""{fileTitle}""

Document excerpt:
---
{excerpt}
---

TASK
----
Identify the distinct employee-facing topics or situations that this document
ACTUALLY answers.

The goal is to help a search system recognize how a real employee may ask
about the content using informal language, while semantically re-anchoring
the query to the formal terminology used in the document.

For each distinct topic, produce:

1. ""Triggers""
   A comprehensive list of words, phrases, abbreviations, synonyms, and
   natural employee queries that could indicate this topic.

   Triggers MUST primarily reflect how an employee would actually search or
   ask the question, NOT how the policy document is formally written.

2. ""AnchorText""
   A short, precise phrase taken from or closely matching the document's
   own formal terminology that best represents the topic.

   AnchorText is used to semantically re-anchor a user's informal query to
   the terminology in the document.

IMPORTANT: DO NOT INVENT INFORMATION
-------------------------------------
Only create topics and triggers that are genuinely supported by the
document excerpt.

Do NOT introduce:
- Topics not covered by the document.
- HR concepts merely associated with the topic.
- Benefits, rules, conditions, or processes not present in the document.
- Generic triggers that could match unrelated HR policies.

For example, if the document discusses ""salary advance"", do NOT add
""salary"" as a standalone trigger unless the document also independently
covers salary-related matters.

TRIGGER COVERAGE REQUIREMENTS
-----------------------------
For EVERY topic, make the Triggers list sufficiently comprehensive.

Where applicable, the Triggers list MUST include ALL of the following:

1. BASE ACTION / VERB FORMS

Include the fundamental action and its natural grammatical forms.

Example:
- resign
- resigning
- resignation
- resigned

Do NOT include only formal result/process terms such as:
- full and final settlement
- exit clearance

when the underlying situation is resignation.

The employee's basic action or situation must also be represented.

2. OBVIOUS EVERYDAY PHRASING

Include the most literal and obvious ways an employee might describe the
situation, even if they appear simple.

Examples:
- leaving the company
- leaving the organisation
- leaving the organization
- quitting
- joining the company
- taking leave
- going on leave

Do not omit an obvious phrase merely because it seems too simple.

3. QUESTION-STYLE SEARCHES

Include realistic phrases an employee might type into a chatbot.

Examples:
- what formalities
- what happens when I resign
- how do I resign
- what is the process
- what do I need to do
- what happens after resignation
- how can I apply
- who is eligible
- what are the requirements

Only include question forms that are relevant to the actual document topic.

4. FORMAL / POLICY TERMS

Also include important formal terminology from the document when it is
useful for retrieval.

5. ABBREVIATIONS AND SHORT FORMS

Include commonly used abbreviations, acronyms, and short forms where they
are natural.

Example:
- full and final
- full & final
- f&f
- fnf
- FNF

6. COMMON VARIATIONS

Where natural, include:
- singular/plural variants
- common spelling variations
- common abbreviations
- common informal wording
- common misspellings that employees are realistically likely to use

Do NOT generate artificial or unlikely misspellings just to increase the
number of triggers.

7. SYNONYMS

Include genuine everyday synonyms when they would reasonably be used for
the same situation.

Do NOT add loosely related words simply because they are semantically
similar.

TRIGGER QUALITY RULE
--------------------
Every trigger should satisfy this question:

""Could a real employee reasonably type this word or phrase when asking
about this specific topic?""

If the answer is no, do not include it.

Do not optimize for the largest possible trigger list.
Optimize for HIGH-RECALL + HIGH-PRECISION.

TOPIC GROUPING
--------------
Most documents should produce ONE topic entry.

Only create multiple topic entries when the document genuinely covers
clearly distinct employee situations that should be searchable separately.

For example, if a document covers both:
- employee loans
- employee advances

and these are separate concepts in the document, they may be separate
topics.

However, do NOT split one concept into multiple entries merely because
there are several related phrases.

For example, resignation, leaving the company, quitting, exit process,
and full & final settlement may belong to ONE topic when the document
covers them as one overall exit/resignation process.

AVOID DUPLICATE TOPICS
----------------------
If two potential topics have substantially overlapping meaning and would
retrieve the same section of the document, merge them into one topic and
combine their useful triggers.

ANCHORTEXT RULES
----------------
AnchorText MUST:

- Represent the actual topic covered by the document.
- Use the document's own terminology whenever possible.
- Be concise.
- Be semantically precise.
- NOT introduce terminology that does not appear in or clearly correspond
  to the document.
- NOT simply repeat one informal trigger.

Good:
""Due Settlement Policy Full and Final Settlement""

Poor:
""Leaving the company""

because the latter is an informal trigger rather than a formal anchor.

WORKED EXAMPLE
--------------
A document about resignation and exit dues should produce coverage at
approximately this level:

[
  {{
    ""Triggers"": [
      ""resign"",
      ""resigning"",
      ""resignation"",
      ""resigned"",
      ""resigned employee"",
      ""notice period"",
      ""last working day"",
      ""relieving"",
      ""relieving letter"",
      ""full and final"",
      ""full & final"",
      ""f&f"",
      ""fnf"",
      ""leaving the company"",
      ""leaving the organisation"",
      ""leaving the organization"",
      ""quitting"",
      ""exit formalities"",
      ""exit process"",
      ""exit clearance"",
      ""what formalities"",
      ""what happens when I resign"",
      ""what happens after resignation"",
      ""how do I resign"",
      ""what is the exit process""
    ],
    ""AnchorText"": ""Due Settlement Policy Full and Final Settlement""
  }}
]

IMPORTANT:
The example demonstrates the REQUIRED LEVEL OF COVERAGE.
It does NOT mean that every document should receive these triggers.
Only use triggers supported by the actual document.

OUTPUT FORMAT
-------------
Respond with ONLY a valid JSON array.

Do not include:
- Markdown
- Code fences
- Explanations
- Comments
- Additional fields
- Text before or after the JSON

The output MUST have exactly this structure:

[
  {{
    ""Triggers"": [""trigger1"", ""trigger2"", ""trigger3""],
    ""AnchorText"": ""formal document terminology""
  }}
]

Each topic object MUST contain exactly:
- ""Triggers""
- ""AnchorText""

Do not return any other properties.";
        }

        private List<PolicyTriggerEntry> ParseTriggerResponse(string rawHttpBody, string sourceFile)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawHttpBody);
                // /api/chat (non-streaming) response shape: { message: { content: "..." }, ... }
                var content = doc.RootElement
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;

                content = StripMarkdownFences(content).Trim();

                var parsed = JsonSerializer.Deserialize<List<PolicyTriggerEntry>>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed == null) return new();

                foreach (var entry in parsed)
                {
                    entry.SourceFile = sourceFile;
                    entry.Triggers = entry.Triggers
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t.Trim().ToLowerInvariant())
                        .Distinct()
                        .ToList();
                }

                return parsed.Where(e => e.Triggers.Count > 0 && !string.IsNullOrWhiteSpace(e.AnchorText)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"⚠️ Could not parse trigger generation response for {sourceFile}");
                return new();
            }
        }

        private static string StripMarkdownFences(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0) text = text.Substring(firstNewline + 1);
                var lastFence = text.LastIndexOf("```");
                if (lastFence >= 0) text = text.Substring(0, lastFence);
            }
            return text;
        }

        private async Task UpsertEntriesAsync(string sourceFile, List<PolicyTriggerEntry> newEntries, string contentHash)
        {
            await _fileLock.WaitAsync();
            try
            {
                _triggers.RemoveAll(e => string.Equals(e.SourceFile, sourceFile, StringComparison.OrdinalIgnoreCase));
                _triggers.AddRange(newEntries);
                _contentHashes[sourceFile] = contentHash;

                var options = new JsonSerializerOptions { WriteIndented = true };

                var dir = Path.GetDirectoryName(_triggersFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(_triggersFilePath, JsonSerializer.Serialize(_triggers, options));

                var hashRecords = _contentHashes.Select(kv => new PolicyTriggerHashRecord { SourceFile = kv.Key, ContentHash = kv.Value }).ToList();
                await File.WriteAllTextAsync(_hashesFilePath, JsonSerializer.Serialize(hashRecords, options));
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Checks an incoming query against every trigger phrase. Returns
        /// the anchor texts (and the source files they point to) for every
        /// match, so the caller can both (a) add the anchor text as an
        /// additional expanded-query search — same pattern as
        /// AbbreviationExpansionService — and (b) optionally apply a direct
        /// relevance boost to chunks from the matched source file(s).
        /// </summary>
        public List<(string AnchorText, string SourceFile)> MatchTriggers(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _triggers.Count == 0)
                return new();

            var lowerQuery = query.ToLowerInvariant();
            var matches = new List<(string AnchorText, string SourceFile)>();

            foreach (var entry in _triggers)
            {
                if (entry.Triggers.Any(trigger => lowerQuery.Contains(trigger)))
                {
                    matches.Add((entry.AnchorText, entry.SourceFile));
                }
            }

            if (matches.Count > 0)
            {
                _logger.LogInformation($"🏷️ Trigger match for '{query}': {string.Join(", ", matches.Select(m => $"{m.SourceFile} <- \"{m.AnchorText}\""))}");
            }

            return matches.Distinct().ToList();
        }

        private static List<string> DeriveTitleTriggers(string fileTitle)
        {
            if (string.IsNullOrWhiteSpace(fileTitle))
                return new List<string>();

            var triggers = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            // Normalize filename into human-readable text.
            var title = fileTitle
                .Replace("_", " ")
                .Replace("-", " ")
                .Replace(".", " ")
                .Replace("–", " ")
                .Replace("—", " ")
                .Trim();

            if (string.IsNullOrWhiteSpace(title))
                return new List<string>();

            // Add complete title.
            Add(title);

            // Remove common document suffixes/prefixes.
            var cleanedTitle = RemoveGenericDocumentWords(title);

            if (!string.IsNullOrWhiteSpace(cleanedTitle) &&
                !cleanedTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                Add(cleanedTitle);
            }

            // Split into meaningful words.
            var words = cleanedTitle
                .Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .Where(w => w.Length >= 2)
                .ToList();

            // Add individual meaningful words.
            foreach (var word in words)
            {
                if (!IsGenericDocumentWord(word))
                    Add(word);
            }

            // Add 2-word phrases.
            for (int i = 0; i < words.Count - 1; i++)
            {
                var phrase = $"{words[i]} {words[i + 1]}";

                if (!ContainsOnlyGenericWords(phrase))
                    Add(phrase);
            }

            // Add 3-word phrases.
            for (int i = 0; i < words.Count - 2; i++)
            {
                var phrase =
                    $"{words[i]} {words[i + 1]} {words[i + 2]}";

                if (!ContainsOnlyGenericWords(phrase))
                    Add(phrase);
            }

            return triggers
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();


            void Add(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                value = value.Trim();

                if (value.Length < 2)
                    return;

                triggers.Add(value);
            }
        }
        private static string RemoveGenericDocumentWords(string title)
        {
            var genericWords = new HashSet<string>(
                new[]
                {
            "policy",
            "policies",
            "procedure",
            "procedures",
            "guideline",
            "guidelines",
            "process",
            "processes",
            "manual",
            "handbook",
            "document",
            "documents",
            "sop",
            "standard",
            "standards",
            "hr",
            "human",
            "resource",
            "resources"
                },
                StringComparer.OrdinalIgnoreCase);

            var words = title
                .Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !genericWords.Contains(w.Trim()));

            return string.Join(" ", words);
        }
        private static bool IsGenericDocumentWord(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "policy" => true,
                "policies" => true,
                "procedure" => true,
                "procedures" => true,
                "guideline" => true,
                "guidelines" => true,
                "process" => true,
                "processes" => true,
                "manual" => true,
                "handbook" => true,
                "document" => true,
                "documents" => true,
                "sop" => true,
                "standard" => true,
                "standards" => true,
                "hr" => true,
                "human" => true,
                "resource" => true,
                "resources" => true,
                _ => false
            };
        }
        private static bool ContainsOnlyGenericWords(string phrase)
        {
            var words = phrase.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            return words.Length > 0 &&
                   words.All(IsGenericDocumentWord);
        }
    }
}