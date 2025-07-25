using Microsoft.Extensions.Caching.Distributed;
using System.Diagnostics;
using System.Text.Json;

public class CacheManager : ICacheManager
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<CacheManager> _logger;
    private readonly IMetricsCollector _metrics;
    private readonly JsonSerializerOptions _jsonOptions;

    public CacheManager(
        IDistributedCache cache,
        ILogger<CacheManager> logger,
        IMetricsCollector metrics)
    {
        _cache = cache;
        _logger = logger;
        _metrics = metrics;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var value = await _cache.GetStringAsync(key, cancellationToken);
            stopwatch.Stop();

            _metrics.RecordCacheOperation("get", stopwatch.ElapsedMilliseconds, value != null);

            if (string.IsNullOrEmpty(value))
                return default;

            return JsonSerializer.Deserialize<T>(value, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get value from cache for key: {Key}", key);
            _metrics.RecordCacheError("get");
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromHours(1)
            };

            var jsonValue = JsonSerializer.Serialize(value, _jsonOptions);
            await _cache.SetStringAsync(key, jsonValue, options, cancellationToken);
            stopwatch.Stop();

            _metrics.RecordCacheOperation("set", stopwatch.ElapsedMilliseconds, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set value in cache for key: {Key}", key);
            _metrics.RecordCacheError("set");
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await _cache.RemoveAsync(key, cancellationToken);
            stopwatch.Stop();

            _metrics.RecordCacheOperation("remove", stopwatch.ElapsedMilliseconds, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove value from cache for key: {Key}", key);
            _metrics.RecordCacheError("remove");
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var value = await _cache.GetAsync(key, cancellationToken);
            stopwatch.Stop();

            var exists = value != null;
            _metrics.RecordCacheOperation("exists", stopwatch.ElapsedMilliseconds, exists);
            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check existence in cache for key: {Key}", key);
            _metrics.RecordCacheError("exists");
            return false;
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        var value = await GetAsync<T>(key, cancellationToken);
        if (value != null)
            return value;

        value = await factory();
        await SetAsync(key, value, expiration, cancellationToken);
        return value;
    }
}