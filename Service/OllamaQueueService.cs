using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Threading.Channels;

namespace MEAI_GPT_API.Service
{
    public class OllamaQueueService
    {
        private readonly Channel<OllamaRequest> _channel = Channel.CreateUnbounded<OllamaRequest>();
        private readonly ILogger<OllamaQueueService> _logger;
        private readonly string _baseUrl;
        private readonly HttpClient _httpClient;

        public OllamaQueueService(
            IHttpClientFactory httpClientFactory,
            ILogger<OllamaQueueService> logger)
        {
            _httpClient = httpClientFactory.CreateClient("OllamaAPI");
            _logger = logger;

            _ = ProcessQueueAsync();
        }

        public Task<string> EnqueueAsync(string prompt, string model = "llama3.1:8b")
        {
            var req = new OllamaRequest { Prompt = prompt, Model = model };
            _channel.Writer.TryWrite(req);
            return req.Completion.Task;
        }

        private async Task ProcessQueueAsync()
        {
            await foreach (var request in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    var payload = new
                    {
                        model = request.Model,
                        prompt = request.Prompt,
                        stream = false
                    };

                    var response = await _httpClient.PostAsJsonAsync("/api/generate", payload);

                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadAsStringAsync();

                    request.Completion.SetResult(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ollama request failed");
                    request.Completion.SetException(ex);
                }
            }
        }

        private class OllamaRequest
        {
            public string Prompt { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public TaskCompletionSource<string> Completion { get; } = new();
        }
    }
}
