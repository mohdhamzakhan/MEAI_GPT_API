using MEAIGPTAPI.Models;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace MEAIGPTAPI.Services
{
    public class OllamaHttpClient
    {
        private readonly List<OllamaEndpoint> _endpoints;
        private int _currentIndex = 0;
        private readonly object _lock = new object();
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OllamaHttpClient> _logger;
        private readonly int _healthCheckInterval;
        private readonly Timer _healthCheckTimer;

        public OllamaHttpClient(
            IHttpClientFactory httpClientFactory,
            ILogger<OllamaHttpClient> logger,
            IOptions<OllamaLoadBalancerOptions> options)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            var config = options.Value;
            _healthCheckInterval = config.HealthCheckIntervalSeconds;

            var endpointUrls = config.Endpoints ?? new List<string>();
            if (!endpointUrls.Any())
            {
                _logger.LogWarning("No Ollama endpoints configured, using default");
                endpointUrls.Add("http://localhost:11434");
            }

            _endpoints = endpointUrls.Select(url => new OllamaEndpoint
            {
                Url = url,
                IsHealthy = true,
                LastHealthCheck = DateTime.MinValue,
                ConsecutiveFailures = 0
            }).ToList();

            _logger.LogInformation($"OllamaHttpClient initialized with {_endpoints.Count} endpoints");
            foreach (var endpoint in _endpoints)
            {
                _logger.LogInformation($"  - {endpoint.Url}");
            }

            // Start periodic health check
            _healthCheckTimer = new Timer(
                async _ => await PerformHealthCheckAsync(),
                null,
                TimeSpan.FromSeconds(5), // Initial delay
                TimeSpan.FromSeconds(_healthCheckInterval));
        }

        private class OllamaEndpoint
        {
            public string Url { get; set; } = string.Empty;
            public bool IsHealthy { get; set; }
            public DateTime LastHealthCheck { get; set; }
            public int ConsecutiveFailures { get; set; }
            public DateTime? LastFailureTime { get; set; }
        }

        private OllamaEndpoint? GetNextHealthyEndpoint()
        {
            lock (_lock)
            {
                var healthyEndpoints = _endpoints.Where(e => e.IsHealthy).ToList();

                if (!healthyEndpoints.Any())
                {
                    _logger.LogWarning("No healthy endpoints available, attempting to use any endpoint");
                    // Try to use the least recently failed endpoint
                    return _endpoints.OrderBy(e => e.LastFailureTime ?? DateTime.MinValue).FirstOrDefault();
                }

                // Round-robin through healthy endpoints
                var endpoint = healthyEndpoints[_currentIndex % healthyEndpoints.Count];
                _currentIndex++;

                _logger.LogDebug($"Selected healthy endpoint: {endpoint.Url}");
                return endpoint;
            }
        }

        private async Task<HttpResponseMessage> ExecuteWithFailoverAsync(
            Func<string, Task<HttpResponseMessage>> action,
            int maxRetries = 3)
        {
            Exception? lastException = null;
            var attemptedEndpoints = new HashSet<string>();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var endpoint = GetNextHealthyEndpoint();

                if (endpoint == null)
                {
                    _logger.LogError("No endpoints available for request");
                    throw new Exception("No Ollama endpoints available", lastException);
                }

                // Skip if we already tried this endpoint
                if (attemptedEndpoints.Contains(endpoint.Url))
                {
                    if (attemptedEndpoints.Count >= _endpoints.Count)
                    {
                        // We've tried all endpoints
                        break;
                    }
                    continue;
                }

                attemptedEndpoints.Add(endpoint.Url);

                try
                {
                    _logger.LogDebug($"Attempt {attempt + 1}: Using endpoint {endpoint.Url}");
                    var response = await action(endpoint.Url);

                    // Success - mark endpoint as healthy
                    MarkEndpointHealthy(endpoint);
                    return response;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, $"Request to {endpoint.Url} failed (attempt {attempt + 1}/{maxRetries})");

                    // Mark endpoint as unhealthy
                    MarkEndpointUnhealthy(endpoint);

                    // Don't delay on last attempt
                    if (attempt < maxRetries - 1)
                    {
                        await Task.Delay(100 * (attempt + 1)); // Exponential backoff
                    }
                }
            }

            throw new Exception($"All Ollama endpoints failed after {maxRetries} attempts. Tried: {string.Join(", ", attemptedEndpoints)}", lastException);
        }

        private void MarkEndpointHealthy(OllamaEndpoint endpoint)
        {
            lock (_lock)
            {
                endpoint.IsHealthy = true;
                endpoint.ConsecutiveFailures = 0;
                endpoint.LastHealthCheck = DateTime.Now;
                _logger.LogDebug($"Endpoint {endpoint.Url} marked as healthy");
            }
        }

        private void MarkEndpointUnhealthy(OllamaEndpoint endpoint)
        {
            lock (_lock)
            {
                endpoint.ConsecutiveFailures++;
                endpoint.LastFailureTime = DateTime.Now;

                // Mark as unhealthy after 2 consecutive failures
                if (endpoint.ConsecutiveFailures >= 2)
                {
                    endpoint.IsHealthy = false;
                    _logger.LogWarning($"Endpoint {endpoint.Url} marked as unhealthy after {endpoint.ConsecutiveFailures} failures");
                }
            }
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithFailoverAsync(async (endpointUrl) =>
            {
                var clonedRequest = await CloneHttpRequestMessageAsync(request);
                var relativeUrl = request.RequestUri?.ToString() ?? string.Empty;
                var fullUrl = $"{endpointUrl}/{relativeUrl.TrimStart('/')}";
                clonedRequest.RequestUri = new Uri(fullUrl);

                var client = _httpClientFactory.CreateClient("OllamaAPI");
                _logger.LogDebug($"SendAsync {clonedRequest.Method} {fullUrl}");

                return await client.SendAsync(clonedRequest, completionOption, cancellationToken);
            });
        }

        public async Task<HttpResponseMessage> PostAsJsonAsync<T>(
            string relativeUrl,
            T content,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithFailoverAsync(async (endpointUrl) =>
            {
                var fullUrl = $"{endpointUrl}/{relativeUrl.TrimStart('/')}";
                var client = _httpClientFactory.CreateClient("OllamaAPI");
                _logger.LogDebug($"POST {fullUrl}");

                return await client.PostAsJsonAsync(fullUrl, content, cancellationToken);
            });
        }

        public async Task<HttpResponseMessage> GetAsync(
            string relativeUrl,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithFailoverAsync(async (endpointUrl) =>
            {
                var fullUrl = $"{endpointUrl}/{relativeUrl.TrimStart('/')}";
                var client = _httpClientFactory.CreateClient("OllamaAPI");

                return await client.GetAsync(fullUrl, cancellationToken);
            });
        }

        // Periodic health check (runs in background)
        private async Task PerformHealthCheckAsync()
        {
            _logger.LogDebug("Starting periodic health check");
            var client = _httpClientFactory.CreateClient("OllamaAPI");

            foreach (var endpoint in _endpoints)
            {
                // Only check unhealthy endpoints or those not checked recently
                if (endpoint.IsHealthy &&
                    (DateTime.Now - endpoint.LastHealthCheck).TotalSeconds < _healthCheckInterval)
                {
                    continue;
                }

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var response = await client.GetAsync($"{endpoint.Url}/api/tags", cts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        if (!endpoint.IsHealthy)
                        {
                            _logger.LogInformation($"Endpoint {endpoint.Url} recovered and is now healthy");
                        }
                        MarkEndpointHealthy(endpoint);
                    }
                    else
                    {
                        _logger.LogWarning($"Health check for {endpoint.Url} returned {response.StatusCode}");
                        MarkEndpointUnhealthy(endpoint);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Health check failed for {endpoint.Url}");
                    MarkEndpointUnhealthy(endpoint);
                }
            }
        }

        // Manual health check - returns status of all endpoints
        public async Task<Dictionary<string, bool>> GetHealthStatusAsync()
        {
            await PerformHealthCheckAsync();

            lock (_lock)
            {
                return _endpoints.ToDictionary(e => e.Url, e => e.IsHealthy);
            }
        }

        // Get detailed statistics
        public Dictionary<string, object> GetStatistics()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>
                {
                    { "TotalEndpoints", _endpoints.Count },
                    { "HealthyEndpoints", _endpoints.Count(e => e.IsHealthy) },
                    { "UnhealthyEndpoints", _endpoints.Count(e => !e.IsHealthy) },
                    { "EndpointDetails", _endpoints.Select(e => new
                        {
                            e.Url,
                            e.IsHealthy,
                            e.ConsecutiveFailures,
                            LastHealthCheck = e.LastHealthCheck.ToString("yyyy-MM-dd HH:mm:ss"),
                            LastFailure = e.LastFailureTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never"
                        }).ToList()
                    }
                };
            }
        }

        // Helper method to clone HttpRequestMessage
        private async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            if (request.Content != null)
            {
                var originalContent = await request.Content.ReadAsStringAsync();
                clone.Content = new StringContent(
                    originalContent,
                    System.Text.Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
            }

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    clone.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        public void Dispose()
        {
            _healthCheckTimer?.Dispose();
        }
    }
}
