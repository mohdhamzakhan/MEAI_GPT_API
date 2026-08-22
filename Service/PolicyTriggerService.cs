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
        public async Task GenerateTriggersForDocumentAsync(string sourceFile, string content, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    return;

                var hash = ComputeHash(content);
                if (_contentHashes.TryGetValue(sourceFile, out var existingHash) && existingHash == hash)
                {
                    _logger.LogDebug($"⏭️ Skipping trigger generation for {sourceFile} — content unchanged");
                    return;
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
                cts.CancelAfter(TimeSpan.FromMinutes(2));

                var response = await _ollamaClient.PostAsJsonAsync("/api/chat", requestData, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"⚠️ Trigger generation call failed for {sourceFile}: {response.StatusCode} - {err}");
                    return;
                }

                var raw = await response.Content.ReadAsStringAsync();
                var entries = ParseTriggerResponse(raw, sourceFile);

                if (entries.Count == 0)
                {
                    _logger.LogWarning($"⚠️ No trigger entries parsed for {sourceFile} — leaving previous entries (if any) untouched");
                    return;
                }

                await UpsertEntriesAsync(sourceFile, entries, hash);
                _logger.LogInformation($"✅ Generated {entries.Count} trigger group(s) for {sourceFile}");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"⚠️ Trigger generation timed out for {sourceFile}");
            }
            catch (Exception ex)
            {
                // Deliberately swallowed: trigger generation is an
                // enhancement layer, not part of the critical indexing
                // path. A bad response, a malformed doc, or a model hiccup
                // here must never take down document processing the way
                // the earlier chunking bug did.
                _logger.LogError(ex, $"❌ Trigger generation failed for {sourceFile}");
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

Task: identify the distinct topics/situations a real employee would informally
ask about that this document actually answers. For each topic, produce:
- ""Triggers"": everyday words, phrases, abbreviations, and synonyms an
  employee might type — NOT formal policy language. Include common
  misspellings/short forms where natural (e.g. ""f&f"", ""fnf"" for full and
  final settlement).
- ""AnchorText"": a short, precise phrase in the document's own formal
  language that best represents this topic (used to semantically re-anchor
  the search, so keep it close to how the document itself phrases things).

Most documents only need ONE topic entry. Only produce multiple entries if
the document genuinely covers clearly distinct situations (e.g. a combined
policy covering both loans AND advances).

Respond with ONLY a JSON array in exactly this shape, nothing else:
[
  {{
    ""Triggers"": [""...""],
    ""AnchorText"": ""...""
  }}
]";
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
    }
}