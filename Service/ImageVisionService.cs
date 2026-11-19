using System.Net.Http.Json;
using System.Text.Json;
using MEAIGPTAPI.Services;            // OllamaHttpClient
using Microsoft.Extensions.Options;

namespace MEAI_GPT_API.Service
{
    public class ImageVisionService
    {
        private readonly OllamaHttpClient _ollama;
        private readonly IOptionsMonitor<ImageWorkspaceOptions> _opts;

        public ImageVisionService(OllamaHttpClient ollama, IOptionsMonitor<ImageWorkspaceOptions> opts)
        { _ollama = ollama; _opts = opts; }

        public async Task<string> AskAsync(string question, List<byte[]> images, string fallbackModel, CancellationToken ct)
        {
            var model = string.IsNullOrWhiteSpace(_opts.CurrentValue.VisionModel) ? fallbackModel : _opts.CurrentValue.VisionModel;

            var request = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content =
                        "You answer questions about the attached image(s). Use ONLY what is actually visible. " +
                        "If something is not visible or unreadable, say so. Never invent text, numbers or names. " +
                        "When several images are attached they are numbered in the order given." },
                    new { role = "user", content = question, images = images.Select(Convert.ToBase64String).ToArray() }
                },
                stream = false,
                think = false,
                options = new { temperature = 0.1, num_ctx = 8192, num_predict = 1200 }
            };

            using var resp = await _ollama.PostAsJsonAsync("/api/chat", request, ct,
                maxRetries: 1, perAttemptTimeout: TimeSpan.FromSeconds(120));
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Vision model returned {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
        }
    }
}