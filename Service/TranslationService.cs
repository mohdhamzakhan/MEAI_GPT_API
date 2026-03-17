// Create Services/TranslationService.cs
using System.Text.Json;

namespace MEAI_GPT_API.Services
{
    public class TranslationService
    {
        private readonly HttpClient _ollamaClient;
        private readonly ILogger<TranslationService> _logger;

        public TranslationService(
            IHttpClientFactory httpClientFactory,
            ILogger<TranslationService> logger)
        {
            _ollamaClient = httpClientFactory.CreateClient("OllamaAPI");
            _logger = logger;
        }

        public async Task<TranslationResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null)
        {
            var prompt = sourceLanguage != null
                ? $"Translate the following text from {sourceLanguage} to {targetLanguage}. Provide ONLY the translation, no explanations:\n\n{text}"
                : $"Translate the following text to {targetLanguage}. Provide ONLY the translation, no explanations:\n\n{text}";

            var requestData = new
            {
                model = "llama3.1:8b",
                prompt = prompt,
                stream = false,
                options = new
                {
                    temperature = 0.1,
                    num_predict = 2000
                }
            };

            try
            {
                var response = await _ollamaClient.PostAsJsonAsync("/api/generate", requestData);

                if (!response.IsSuccessStatusCode)
                {
                    return new TranslationResult
                    {
                        Success = false,
                        ErrorMessage = "Translation failed"
                    };
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var translation = doc.RootElement
                    .GetProperty("response")
                    .GetString()
                    ?.Trim() ?? "";

                return new TranslationResult
                {
                    Success = true,
                    TranslatedText = translation,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Translation failed");
                return new TranslationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null)
        {
            var prompt = sourceLanguage != null
                ? $"Translate from {sourceLanguage} to {targetLanguage}:\n\n{text}"
                : $"Translate to {targetLanguage}:\n\n{text}";

            var requestData = new
            {
                model = "llama3.1:8b",
                prompt = prompt,
                stream = true,
                options = new { temperature = 0.1 }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
            {
                Content = JsonContent.Create(requestData)
            };

            using var response = await _ollamaClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var responseProp))
                {
                    var token = responseProp.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        yield return token;
                    }
                }

                if (doc.RootElement.TryGetProperty("done", out var doneProp) &&
                    doneProp.GetBoolean())
                {
                    break;
                }
            }
        }
    }

    public class TranslationResult
    {
        public bool Success { get; set; }
        public string TranslatedText { get; set; } = "";
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public string? ErrorMessage { get; set; }
    }
}