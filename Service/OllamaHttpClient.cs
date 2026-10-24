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

        // 🆕 Concurrency gate for BACKGROUND work only (indexing: trigger
        // generation, eligibility extraction, document embeddings). Live
        // request-path calls (chat generation, query embeddings) never pass
        // isBackgroundTask: true, so they're never throttled by this — their
        // concurrency is naturally bounded by real simultaneous users, not
        // something we need to artificially cap.
        //
        // This exists because retries and fresh per-attempt timeouts (see
        // ExecuteWithFailoverAsync) only help a slow response fail faster and
        // recover — they can't create GPU throughput that isn't there. A
        // bulk refresh firing trigger-gen + eligibility-extraction +
        // embedding calls for many documents concurrently, especially
        // alongside live traffic, can genuinely exceed real usable
        // concurrency even with OLLAMA_NUM_PARALLEL configured generously —
        // the requests don't fail, they just all get slower at once, and
        // enough of them slow down past a 45-60s budget to look like
        // failures. Capping background concurrency app-side means indexing
        // self-limits to a rate the box can actually sustain without ever
        // degrading live query latency, instead of hoping Ollama's own
        // queueing sorts out fairness after the fact.
        //
        // 2 is a starting point, not a measured optimum -- raise it if
        // indexing feels too slow and headroom checks (nvidia-smi, /api/ps)
        // during a real bulk refresh confirm the box can sustain more.
        private static readonly SemaphoreSlim _backgroundConcurrencyGate = new SemaphoreSlim(2, 2);

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
                TimeSpan.FromSeconds(1), // Initial delay
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

        private OllamaEndpoint? GetNextHealthyEndpoint(
    HashSet<string> attemptedEndpoints)
        {
            lock (_lock)
            {
                // Prefer healthy endpoints not yet attempted.
                var healthyUntried = _endpoints
                    .Where(e => e.IsHealthy &&
                                !attemptedEndpoints.Contains(e.Url))
                    .ToList();

                if (healthyUntried.Count > 0)
                {
                    var endpoint =
                        healthyUntried[_currentIndex % healthyUntried.Count];

                    _currentIndex++;

                    return endpoint;
                }

                // Try an untried unhealthy endpoint before repeating an endpoint.
                var unhealthyUntried = _endpoints
                    .Where(e => !attemptedEndpoints.Contains(e.Url))
                    .OrderBy(e => e.LastFailureTime ?? DateTime.MinValue)
                    .ToList();

                if (unhealthyUntried.Count > 0)
                {
                    return unhealthyUntried[0];
                }

                // All endpoints have been attempted. Retry the least recently
                // failed endpoint, or a healthy endpoint if one remains.
                return _endpoints
                    .OrderBy(e => e.IsHealthy ? 0 : 1)
                    .ThenBy(e => e.LastFailureTime ?? DateTime.MinValue)
                    .FirstOrDefault();
            }
        }

        // ✅ CHANGED: action now takes the per-attempt CancellationToken instead of
        // closing over a single token supplied by the caller. Previously, callers
        // that wanted a request-level timeout (GradeEligibilityService,
        // PolicyTriggerService, DynamicRagService's clarifying-question call) did
        // `cts.CancelAfter(45s)` THEMSELVES and passed that single token in. That
        // token's deadline is absolute wall-clock time, not "45s per try" — so if
        // attempt 1 used up the whole 45s (e.g. Ollama slow/overloaded) and got
        // cancelled, attempts 2 and 3 inherited an ALREADY-EXPIRED token and failed
        // near-instantly without ever really being retried. Symptom in the logs:
        // "attempt 1/3" through "attempt 3/3" all failing within ~100ms of each
        // other with TaskCanceledException / socket-aborted errors, even though
        // each retry is supposed to get a fresh shot.
        //
        // Fix: `outerCancellationToken` is the caller's REAL cancellation signal
        // (request aborted, app shutdown, or CancellationToken.None if the caller
        // doesn't have one) and is honored immediately — if it fires, we stop
        // retrying right away rather than burning through remaining attempts.
        // `perAttemptTimeout`, if supplied, is applied FRESH on every single
        // attempt via its own linked CancellationTokenSource, so a slow attempt
        // failing doesn't poison the budget for the next one.
        private async Task<HttpResponseMessage> ExecuteWithFailoverAsync(
            Func<string, CancellationToken, Task<HttpResponseMessage>> action,
            int maxRetries = 3,
            TimeSpan? perAttemptTimeout = null,
            CancellationToken outerCancellationToken = default,
            bool isBackgroundTask = false)
        {
            if (isBackgroundTask)
            {
                await _backgroundConcurrencyGate.WaitAsync(outerCancellationToken);
            }

            try
            {
                return await ExecuteWithFailoverCoreAsync(action, maxRetries, perAttemptTimeout, outerCancellationToken);
            }
            finally
            {
                if (isBackgroundTask)
                {
                    _backgroundConcurrencyGate.Release();
                }
            }
        }

        private async Task<HttpResponseMessage> ExecuteWithFailoverCoreAsync(
    Func<string, CancellationToken, Task<HttpResponseMessage>> action,
    int maxRetries = 3,
    TimeSpan? perAttemptTimeout = null,
    CancellationToken outerCancellationToken = default)
        {
            int maxAttempts = Math.Max(1, maxRetries);

            Exception? lastException = null;

            // Tracks endpoints already attempted during this request.
            // Prefer untried endpoints before retrying an existing one.
            var attemptedEndpoints = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // Do not retry if the caller cancelled the request.
                outerCancellationToken.ThrowIfCancellationRequested();

                // Prefer a healthy endpoint that has not yet been attempted.
                // If all endpoints have been tried, allow a genuine retry.
                var endpoint = GetNextHealthyEndpoint(attemptedEndpoints);

                if (endpoint == null)
                {
                    throw new HttpRequestException(
                        "No Ollama endpoints are configured or available.",
                        lastException);
                }

                attemptedEndpoints.Add(endpoint.Url);

                // Each attempt receives its own timeout.
                using var attemptCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        outerCancellationToken);

                if (perAttemptTimeout.HasValue)
                {
                    attemptCts.CancelAfter(perAttemptTimeout.Value);
                }

                try
                {
                    _logger.LogInformation(
                        "Ollama request attempt {Attempt}/{MaxAttempts} " +
                        "using endpoint {Endpoint}",
                        attempt,
                        maxAttempts,
                        endpoint.Url);

                    var response = await action(
                        endpoint.Url,
                        attemptCts.Token);

                    // Retry transient HTTP failures only.
                    bool isRetryableStatus =
                        (int)response.StatusCode == 429 ||
                        (int)response.StatusCode >= 500;

                    if (isRetryableStatus && attempt < maxAttempts)
                    {
                        _logger.LogWarning(
                            "Ollama returned HTTP {StatusCode} from {Endpoint} " +
                            "on attempt {Attempt}/{MaxAttempts}. Retrying.",
                            (int)response.StatusCode,
                            endpoint.Url,
                            attempt,
                            maxAttempts);

                        response.Dispose();
                        MarkEndpointUnhealthy(endpoint);

                        await Task.Delay(
                            TimeSpan.FromMilliseconds(200 * attempt),
                            outerCancellationToken);

                        continue;
                    }

                    // Keep the endpoint's health status consistent with
                    // the result of this request.
                    if (response.IsSuccessStatusCode)
                    {
                        MarkEndpointHealthy(endpoint);
                    }
                    else if (isRetryableStatus)
                    {
                        MarkEndpointUnhealthy(endpoint);
                    }

                    // Return the final HTTP response to the caller.
                    // Existing callers can handle non-success status codes.
                    return response;
                }
                catch (OperationCanceledException)
                    when (outerCancellationToken.IsCancellationRequested)
                {
                    // Actual caller cancellation must not trigger a retry.
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    bool timedOut =
                        attemptCts.IsCancellationRequested &&
                        perAttemptTimeout.HasValue;

                    _logger.LogWarning(
                        ex,
                        "Ollama request {FailureType} at {Endpoint}. " +
                        "Attempt {Attempt}/{MaxAttempts}; timeout {Timeout}.",
                        timedOut ? "timed out" : "failed",
                        endpoint.Url,
                        attempt,
                        maxAttempts,
                        perAttemptTimeout);

                    MarkEndpointUnhealthy(endpoint);

                    if (attempt < maxAttempts)
                    {
                        // Bounded backoff between attempts.
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(200 * attempt),
                            outerCancellationToken);
                    }
                }
            }

            throw new HttpRequestException(
                $"Ollama request failed after {maxAttempts} attempts. " +
                $"Endpoints attempted: {string.Join(", ", attemptedEndpoints)}",
                lastException);
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

        // ✅ CHANGED: added maxRetries / perAttemptTimeout. Pass perAttemptTimeout
        // when you want a bounded-latency call (e.g. a live-request-path or
        // best-effort background call) — each retry gets that full budget fresh,
        // instead of the caller pre-cancelling a shared token (see
        // ExecuteWithFailoverAsync for why that was wrong). Leave it null to keep
        // the old "only the caller's own cancellationToken governs it" behavior.
        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default,
            int maxRetries = 3,
            TimeSpan? perAttemptTimeout = null,
            bool isBackgroundTask = false)
        {
            return await ExecuteWithFailoverAsync(async (endpointUrl, attemptToken) =>
            {
                var clonedRequest = await CloneHttpRequestMessageAsync(request);
                var relativeUrl = request.RequestUri?.ToString() ?? string.Empty;
                var fullUrl = $"{endpointUrl}/{relativeUrl.TrimStart('/')}";
                clonedRequest.RequestUri = new Uri(fullUrl);

                var client = _httpClientFactory.CreateClient("OllamaAPI");
                _logger.LogDebug($"SendAsync {clonedRequest.Method} {fullUrl}");

                return await client.SendAsync(clonedRequest, completionOption, attemptToken);
            }, maxRetries, perAttemptTimeout, cancellationToken, isBackgroundTask);
        }

        public async Task<HttpResponseMessage> PostAsJsonAsync<T>(
            string relativeUrl,
            T content,
            CancellationToken cancellationToken = default,
            int maxRetries = 3,
            TimeSpan? perAttemptTimeout = null,
            bool isBackgroundTask = false)
        {
            return await ExecuteWithFailoverAsync(async (endpointUrl, attemptToken) =>
            {
                var fullUrl = $"{endpointUrl}/{relativeUrl.TrimStart('/')}";
                var client = _httpClientFactory.CreateClient("OllamaAPI");
                _logger.LogDebug($"POST {fullUrl}");

                return await client.PostAsJsonAsync(fullUrl, content, attemptToken);
            }, maxRetries, perAttemptTimeout, cancellationToken, isBackgroundTask);
        }

        public async Task<HttpResponseMessage> GetAsync(
            string relativeUrl,
            CancellationToken cancellationToken = default,
            int maxRetries = 3,
            TimeSpan? perAttemptTimeout = null)
        {
            return await ExecuteWithFailoverAsync(async (endpointUrl, attemptToken) =>
            {
                var fullUrl = $"{endpointUrl}/{relativeUrl.TrimStart('/')}";
                var client = _httpClientFactory.CreateClient("OllamaAPI");

                return await client.GetAsync(fullUrl, attemptToken);
            }, maxRetries, perAttemptTimeout, cancellationToken);
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