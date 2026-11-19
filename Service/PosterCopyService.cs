using System.Net.Http.Json;
using System.Text.Json;
using MEAIGPTAPI.Services;            // OllamaHttpClient (namespace from your stack trace)

namespace MEAI_GPT_API.Service
{
    public class PosterCopy
    {
        public string Scene { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
    }

    public class PosterCopyService
    {
        private readonly OllamaHttpClient _ollama;
        private readonly ILogger<PosterCopyService> _log;
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public PosterCopyService(OllamaHttpClient ollama, ILogger<PosterCopyService> log)
        { _ollama = ollama; _log = log; }

        public async Task<PosterCopy> DraftAsync(string brief, string language, string model, CancellationToken ct)
        {
            var prompt = $@"You write the wording for a poster. The brief is:
""{brief}""

Return ONLY this JSON:
{{ ""scene"": ""..."", ""title"": ""..."", ""subtitle"": ""..."" }}

Rules:
- scene: an English description of the PICTURE only (max 40 words): subject, setting, style, colours. It must not mention any text, words, letters or signs.
- title: max 8 words, written in {language}.
- subtitle: max 14 words, written in {language}.
- Use ONLY facts from the brief. Do NOT invent dates, numbers, names, places or slogans that the brief does not contain.
- Write each of title and subtitle entirely in one language and script (no mixing).";

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var request = new
                {
                    model,
                    messages = new object[]
                    {
                        new { role = "system", content = "You output ONLY valid JSON." },
                        new { role = "user", content = prompt }
                    },
                    stream = false,
                    format = "json",
                    think = false,
                    options = new { temperature = 0.4, num_ctx = 4096, num_predict = 400 }
                };

                using var resp = await _ollama.PostAsJsonAsync("/api/chat", request, ct,
                    maxRetries: 1, perAttemptTimeout: TimeSpan.FromSeconds(60));
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Model returned {(int)resp.StatusCode}");

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

                try
                {
                    var copy = JsonSerializer.Deserialize<PosterCopy>(content, Json);
                    if (copy != null && !string.IsNullOrWhiteSpace(copy.Scene) && !string.IsNullOrWhiteSpace(copy.Title))
                    {
                        copy.Scene = Cut(copy.Scene, 300);
                        copy.Title = Cut(copy.Title.Trim(), 120);
                        copy.Subtitle = string.IsNullOrWhiteSpace(copy.Subtitle) ? null : Cut(copy.Subtitle.Trim(), 200);
                        return copy;
                    }
                }
                catch (JsonException ex) { _log.LogWarning(ex, "Poster copy JSON invalid (attempt {A})", attempt); }
            }
            throw new InvalidOperationException("The model did not return usable poster text.");
        }

        private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}