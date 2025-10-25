using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Threading.Channels;

namespace MEAI_GPT_API.Service
{
    public class OllamaQueueService
    {
        private readonly Channel<OllamaRequest> _channel;
        private readonly ILogger<OllamaQueueService> _logger;
        private readonly HttpClient _httpClient;

        public OllamaQueueService(
            IHttpClientFactory httpClientFactory,
            ILogger<OllamaQueueService> logger)
        {
            _channel = Channel.CreateUnbounded<OllamaRequest>();
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("OllamaAPI");

            // Start background queue processor safely
            _ = Task.Run(ProcessQueueAsyncSafe);
        }

        public Task<string> EnqueueAsync(string prompt, string model = "llama3.1:8b")
        {
            var req = new OllamaRequest
            {
                Prompt = prompt,
                Model = model
            };

            if (!_channel.Writer.TryWrite(req))
            {
                var ex = new InvalidOperationException("Failed to enqueue Ollama request: channel full or closed.");
                _logger.LogError(ex, "EnqueueAsync failed");
                throw ex;
            }

            return req.Completion.Task;
        }

        private async Task ProcessQueueAsyncSafe()
        {
            try
            {
                await ProcessQueueAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OllamaQueueService processing failed");
                // Optionally, restart processing or handle error appropriately here
            }
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
            public TaskCompletionSource<string> Completion { get; } = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
