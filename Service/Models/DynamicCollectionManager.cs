// Services/DynamicCollectionManager.cs
using MEAI_GPT_API.Models;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace MEAI_GPT_API.Services
{
      public class DynamicCollectionManager
    {
        private readonly HttpClient _chromaClient;
        private readonly ChromaDbOptions _options;
        private readonly ILogger<DynamicCollectionManager> _logger;
        private readonly ConcurrentDictionary<string, string> _modelCollections = new();

        public DynamicCollectionManager(HttpClient chromaClient, ChromaDbOptions options, ILogger<DynamicCollectionManager> logger)
        {
            _chromaClient = chromaClient;
            _options = options;
            _logger = logger;
        }

        public async Task<string> GetOrCreateCollectionAsync(ModelConfiguration model)
        {
            var cacheKey = model.Name;
            if (_modelCollections.TryGetValue(cacheKey, out var existingId))
            {
                return existingId;
            }

            var collectionName = GenerateCollectionName(model.Name);
            var collectionId = await CreateCollectionAsync(collectionName, model);

            _modelCollections[cacheKey] = collectionId;
            return collectionId;
        }

        private async Task<string> CreateCollectionAsync(string collectionName, ModelConfiguration model)
        {
            try
            {
                var collectionData = new
                {
                    name = collectionName,
                    metadata = new
                    {
                        description = $"MEAI HR Policy documents - {model.Name}",
                        model = model.Name,
                        embedding_dimension = model.EmbeddingDimension,
                        created_at = DateTime.UtcNow.ToString("O")
                    },
                    dimension = model.EmbeddingDimension, // Dynamic dimension!
                    configuration = new
                    {
                        hnsw = new
                        {
                            space = "cosine",
                            ef_construction = 100,
                            ef_search = 100,
                            max_neighbors = 16,
                            resize_factor = 1.2,
                            sync_threshold = 1000
                        }
                    }
                };

                var response = await _chromaClient.PostAsJsonAsync(
                    $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections",
                    collectionData);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ChromaCollectionResponse>();
                    var collectionId = result?.Id ?? throw new ChromaDBException("Failed to get collection ID");

                    _logger.LogInformation($"✅ Created collection for model {model.Name}: {collectionId}");
                    return collectionId;
                }

                // Collection might exist, try to get existing ID
                var existingId = await GetExistingCollectionIdAsync(collectionName);
                if (existingId != null)
                {
                    _logger.LogInformation($"📋 Using existing collection for model {model.Name}: {existingId}");
                    return existingId;
                }

                throw new ChromaDBException($"Failed to create or get collection for model {model.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create collection for model {model.Name}");
                throw;
            }
        }

        private string GenerateCollectionName(string modelName)
        {
            // Sanitize model name for collection naming
            var sanitized = modelName.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            return $"{_options.Collections["policies"]}_{sanitized}";
        }

        private async Task<string?> GetExistingCollectionIdAsync(string collectionName)
        {
            try
            {
                var response = await _chromaClient.GetAsync(
                    $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections");

                if (response.IsSuccessStatusCode)
                {
                    var collections = await response.Content.ReadFromJsonAsync<List<ChromaCollectionResponse>>();
                    return collections?.FirstOrDefault(c =>
                        c.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase))?.Id;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get existing collection ID for {collectionName}");
                return null;
            }
        }

        public async Task<List<string>> GetAllCollectionIdsAsync()
        {
            return _modelCollections.Values.ToList();
        }

        public string? GetCollectionId(string modelName)
        {
            return _modelCollections.TryGetValue(modelName, out var id) ? id : null;
        }

        public async Task DeleteModelCollectionAsync(string modelName)
        {
            try
            {
                if (_modelCollections.TryGetValue(modelName, out var collectionId))
                {
                    var collectionName = GenerateCollectionName(modelName);
                    // Delete collection content first
                    var deleteRequest = new { };
                    var response = await _chromaClient.PostAsJsonAsync(
                        $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{collectionId}/delete",
                        deleteRequest);

                    if (response.IsSuccessStatusCode)
                    {
                        // Delete collection structure
                        await _chromaClient.DeleteAsync(
                            $"/api/v2/tenants/{_options.Tenant}/databases/{_options.Database}/collections/{collectionName}");

                        _modelCollections.TryRemove(modelName, out _);
                        _logger.LogInformation($"Deleted collection for model {modelName}");
                    }
                    
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete collection for model {modelName}");
            }
        }
    }
}
