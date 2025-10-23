using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MEAI_GPT_API.Service
{
    public class HelperMethods
    {
        private readonly HttpClient _httpClient;
        public HelperMethods(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("OllamaAPI");
        }
        public async Task<string> RephraseWithLLMAsync(string originalAnswer, string modelName)
        {
            var systemPrompt = "You are a professional assistant. Rephrase the following content to make it sound polished, concise, and formal.";
            var userMessage = $"Rephrase this:\n\n\"{originalAnswer}\"";

            var messages = new[]
            {
        new { role = "system", content = systemPrompt },
        new { role = "user", content = userMessage }
    };

            var payload = new
            {
                model = modelName,
                messages = messages,
                stream = true, // Enable streaming
                temperature = 0.2
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") // Adjust path as needed
            {
                Content = JsonContent.Create(payload)
            };

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var finalContent = new StringBuilder();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("message", out var msgElem) &&
                        msgElem.TryGetProperty("content", out var contentElem))
                    {
                        finalContent.Append(contentElem.GetString());
                    }
                }
                catch
                {
                    // ignore bad JSON or keep logging
                }
            }

            return finalContent.ToString();
        }
    }
}
