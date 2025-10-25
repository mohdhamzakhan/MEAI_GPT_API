using System.Collections.Concurrent;
using MEAI_GPT_API.Models;
using Microsoft.Extensions.Logging;

namespace MEAI_GPT_API.Services
{
    /// <summary>
    /// Singleton service that holds shared state across all RAG service instances
    /// This prevents re-initialization and re-embedding on every request
    /// </summary>
    public class RagStateManager
    {
        private readonly ILogger<RagStateManager> _logger;

        // ✅ Global initialization state
        private volatile bool _isInitialized = false;
        private volatile bool _isInitializing = false;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private DateTime _initializationTime = DateTime.MinValue;

        // ✅ Shared embedding cache across all instances
        private readonly ConcurrentDictionary<string, (List<float> Embedding, DateTime Cached, int AccessCount)>
            _embeddingCache = new();

        // ✅ Shared model and collection state
        private readonly ConcurrentDictionary<string, List<ModelConfiguration>> _modelCache = new();
        private readonly ConcurrentDictionary<string, bool> _processedCollections = new();

        // ✅ Global embedding semaphore to control concurrency
        private readonly SemaphoreSlim _embeddingSemaphore;

        // ✅ Cache cleanup timer
        private readonly Timer _cacheCleanupTimer;

        public RagStateManager(ILogger<RagStateManager> logger)
        {
            _logger = logger;
            _embeddingSemaphore = new SemaphoreSlim(5, 5); // Max 5 concurrent embeddings

            // Cleanup cache every 15 minutes
            _cacheCleanupTimer = new Timer(
                CleanupCache,
                null,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15)
            );

            _logger.LogInformation("✅ RagStateManager singleton created");
        }

        // ✅ Properties
        public bool IsInitialized => _isInitialized;
        public bool IsInitializing => _isInitializing;
        public DateTime InitializationTime => _initializationTime;

        // ✅ Initialization lock management
        public async Task<bool> TryAcquireInitLockAsync(int timeoutMs = 0)
        {
            return await _initLock.WaitAsync(timeoutMs);
        }

        public void ReleaseInitLock()
        {
            _initLock.Release();
        }

        public void SetInitializing(bool value)
        {
            _isInitializing = value;
        }

        public void MarkAsInitialized()
        {
            _initializationTime = DateTime.Now;
            _isInitialized = true;
            _isInitializing = false;
            _logger.LogInformation("✅ RAG State marked as initialized at {Time}", _initializationTime);
        }

        public void MarkAsFailed()
        {
            _isInitialized = false;
            _isInitializing = false;
            _logger.LogWarning("❌ RAG State marked as failed");
        }

        // ✅ Embedding cache management
        public async Task<List<float>?> GetCachedEmbeddingAsync(string cacheKey)
        {
            if (_embeddingCache.TryGetValue(cacheKey, out var cached))
            {
                // Update access count and time
                _embeddingCache.TryUpdate(
                    cacheKey,
                    (cached.Embedding, DateTime.Now, cached.AccessCount + 1),
                    cached
                );

                _logger.LogDebug("✅ Cache HIT for embedding: {Key}", cacheKey.Substring(0, Math.Min(50, cacheKey.Length)));
                return cached.Embedding;
            }

            _logger.LogDebug("❌ Cache MISS for embedding: {Key}", cacheKey.Substring(0, Math.Min(50, cacheKey.Length)));
            return null;
        }

        public void CacheEmbedding(string cacheKey, List<float> embedding)
        {
            _embeddingCache.TryAdd(cacheKey, (embedding, DateTime.Now, 1));
            _logger.LogDebug("💾 Cached embedding: {Key} ({Size} dimensions)",
                cacheKey.Substring(0, Math.Min(50, cacheKey.Length)),
                embedding.Count);
        }

        public async Task<T> ExecuteWithEmbeddingSemaphoreAsync<T>(Func<Task<T>> action)
        {
            await _embeddingSemaphore.WaitAsync();
            try
            {
                return await action();
            }
            finally
            {
                _embeddingSemaphore.Release();
            }
        }

        // ✅ Model cache management
        public void CacheModels(string plant, List<ModelConfiguration> models)
        {
            _modelCache[plant] = models;
            _logger.LogInformation("💾 Cached {Count} models for plant: {Plant}", models.Count, plant);
        }

        public List<ModelConfiguration>? GetCachedModels(string plant)
        {
            return _modelCache.TryGetValue(plant, out var models) ? models : null;
        }

        // ✅ Collection processing state
        public bool IsCollectionProcessed(string collectionKey)
        {
            return _processedCollections.ContainsKey(collectionKey);
        }

        public void MarkCollectionAsProcessed(string collectionKey)
        {
            _processedCollections.TryAdd(collectionKey, true);
            _logger.LogInformation("✅ Marked collection as processed: {Key}", collectionKey);
        }

        // ✅ Cache cleanup
        private void CleanupCache(object? state)
        {
            try
            {
                var cutoffTime = DateTime.Now.AddHours(-1);
                var keysToRemove = _embeddingCache
                    .Where(kvp => kvp.Value.Cached < cutoffTime && kvp.Value.AccessCount < 2)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _embeddingCache.TryRemove(key, out _);
                }

                if (keysToRemove.Count > 0)
                {
                    _logger.LogInformation("🧹 Cleaned up {Count} old embeddings from cache", keysToRemove.Count);
                }

                _logger.LogDebug("📊 Cache stats - Embeddings: {Count}, Collections: {Collections}",
                    _embeddingCache.Count,
                    _processedCollections.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during cache cleanup");
            }
        }

        // ✅ Get cache statistics
        public CacheStatistics GetCacheStatistics()
        {
            return new CacheStatistics
            {
                EmbeddingCount = _embeddingCache.Count,
                ProcessedCollectionCount = _processedCollections.Count,
                ModelCacheCount = _modelCache.Count,
                IsInitialized = _isInitialized,
                InitializationTime = _initializationTime,
                TotalAccessCount = _embeddingCache.Sum(kvp => kvp.Value.AccessCount)
            };
        }

        // ✅ Reset (for testing or manual refresh)
        public void Reset()
        {
            _logger.LogWarning("⚠️ Resetting RAG State Manager");

            _isInitialized = false;
            _isInitializing = false;
            _embeddingCache.Clear();
            _modelCache.Clear();
            _processedCollections.Clear();
            _initializationTime = DateTime.MinValue;
        }
    }

    public class CacheStatistics
    {
        public int EmbeddingCount { get; set; }
        public int ProcessedCollectionCount { get; set; }
        public int ModelCacheCount { get; set; }
        public bool IsInitialized { get; set; }
        public DateTime InitializationTime { get; set; }
        public int TotalAccessCount { get; set; }
    }
}