using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace MEAI_GPT_API.Services
{
    public class ModelManager : IModelManager
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ModelManager> _logger;
        private readonly ConcurrentDictionary<string, ModelConfiguration> _availableModels = new();
        private readonly SemaphoreSlim _discoveryLock = new(1, 1);
        private DateTime _lastDiscovery = DateTime.MinValue;
        private readonly TimeSpan _discoveryInterval = TimeSpan.FromMinutes(10);
        private readonly DynamicRAGConfiguration _config;

        public ModelManager(HttpClient httpClient, 
            ILogger<ModelManager> logger,
            IOptions<DynamicRAGConfiguration> config)
        {
            _httpClient = httpClient;
            _logger = logger;
            _config = config.Value;
        }

        public async Task<List<ModelConfiguration>> DiscoverAvailableModelsAsync()
        {
            await _discoveryLock.WaitAsync();
            try
            {
                // Cache discovery results for 10 minutes
                if (DateTime.Now - _lastDiscovery < _discoveryInterval && _availableModels.Any())
                {
                    return _availableModels.Values.ToList();
                }

                _logger.LogInformation("🔍 Discovering available models...");
                var models = new List<ModelConfiguration>();

                var response = await _httpClient.GetAsync("/api/tags");
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch available models from Ollama");
                    return models;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    var tasks = modelsArray.EnumerateArray()
                        .Select(async model =>
                        {
                            if (model.TryGetProperty("name", out var nameProperty))
                            {
                                var modelName = nameProperty.GetString();
                                if (!string.IsNullOrEmpty(modelName))
                                {
                                    var config = await DetectModelCapabilitiesAsync(modelName);
                                    if (config != null)
                                    {
                                        return config;
                                    }
                                }
                            }
                            return null;
                        })
                        .Where(t => t != null);

                    var results = await Task.WhenAll(tasks);
                    models = results.Where(m => m != null).Cast<ModelConfiguration>().ToList();
                }

                // Update cache
                _availableModels.Clear();
                foreach (var model in models)
                {
                    _availableModels[model.Name] = model;
                }

                _lastDiscovery = DateTime.Now;
                _logger.LogInformation($"✅ Discovered {models.Count} available models");
                return models;
            }
            finally
            {
                _discoveryLock.Release();
            }
        }

        private async Task<ModelConfiguration?> DetectModelCapabilitiesAsync(string modelName)
        {
            try
            {           
                var embeddingDimension = await TestEmbeddingCapabilityAsync(modelName);
                var canGenerate = await TestGenerationCapabilityAsync(modelName);
                _logger.LogInformation($"{modelName} , {embeddingDimension}, {canGenerate}");
                if (embeddingDimension > 0 || canGenerate)
                {
                    var type = embeddingDimension > 0 && canGenerate ? "both" :
                              embeddingDimension > 0 ? "embedding" : "generation";

                    return new ModelConfiguration
                    {
                        Name = modelName,
                        EmbeddingDimension = embeddingDimension,
                        IsAvailable = true,
                        Type = type,
                        ModelOptions = GetModelSpecificOptions(modelName),
                        MaxContextLength = DetermineContextLength(modelName),
                        Temperature = DetermineOptimalTemperature(modelName)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to detect capabilities for model {modelName}");
                return null;
            }
        }

        private async Task<int> TestEmbeddingCapabilityAsync(string modelName)
        {
            try
            {
                var request = new
                {
                    model = modelName,
                    prompt = "test",
                    options = new { num_ctx = 128, temperature = 0.0 }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("embedding", out var embeddingProperty))
                    {
                        return embeddingProperty.GetArrayLength();
                    }
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<bool> TestGenerationCapabilityAsync(string modelName)
        {
            try
            {
                var request = new
                {
                    model = modelName,
                    prompt = "Hello",
                    options = new { max_tokens = 5, temperature = 0.0 }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private Dictionary<string, object> GetModelSpecificOptions(string modelName)
        {
            var options = new Dictionary<string, object>();
            var lowerName = modelName.ToLowerInvariant();

            if (lowerName.Contains("mistral"))
            {
                options["num_ctx"] = 8192;
                options["temperature"] = 0.1;
                options["top_p"] = 0.9;
            }
            else if (lowerName.Contains("llama"))
            {
                options["num_ctx"] = 4096;
                options["temperature"] = 0.2;
                options["top_p"] = 0.95;
            }
            else if (lowerName.Contains("qwen"))
            {
                options["num_ctx"] = 6144;
                options["temperature"] = 0.15;
                options["top_p"] = 0.85;
            }
            else
            {
                // Safe fallback
                options["num_ctx"] = 2048;
                options["temperature"] = 0.2;
            }

            return options;
        }


        private int DetermineContextLength(string modelName)
        {
            var lowerName = modelName.ToLower();
            return lowerName switch
            {
                var name when name.Contains("mistral") => 8192,
                var name when name.Contains("llama") => 4096,
                var name when name.Contains("phi") => 2048,
                var name when name.Contains("qwen") => 6144,
                _ => 4096
            };
        }

        private double DetermineOptimalTemperature(string modelName)
        {
            var lowerName = modelName.ToLower();
            return lowerName switch
            {
                var name when name.Contains("mistral") => 0.1,
                var name when name.Contains("llama") => 0.2,
                var name when name.Contains("phi") => 0.1,
                var name when name.Contains("qwen") => 0.15,
                _ => 0.2
            };
        }

        public Task<ModelConfiguration?> GetModelAsync(string modelName)
        {
            // First, check in static config with exact model name
            if (_config.ModelConfigurations.TryGetValue(modelName.Replace(':','_'), out var config))
            {
                _logger.LogInformation($"✅ Loaded model config from appsettings: {modelName}");

                return Task.FromResult<ModelConfiguration?>(new ModelConfiguration
                {
                    Name = modelName,
                    IsAvailable = true,
                    Type = config.Type,
                    EmbeddingDimension = config.EmbeddingDimension,
                    MaxContextLength = config.MaxContextLength,
                    Temperature = config.Temperature,
                    ModelOptions = config.ModelOptions ?? new Dictionary<string, object>()
                });
            }

            // If exact match fails, try with base name (without version tag)
            var baseName = modelName.Split(':')[0];
            var matchingConfig = _config.ModelConfigurations.FirstOrDefault(kvp =>
                kvp.Key.Split(':')[0].Equals(baseName, StringComparison.OrdinalIgnoreCase));

            if (!matchingConfig.Equals(default(KeyValuePair<string, dynamic>)))
            {
                _logger.LogInformation($"✅ Loaded model config from appsettings (base match): {modelName} -> {matchingConfig.Key}");

                return Task.FromResult<ModelConfiguration?>(new ModelConfiguration
                {
                    Name = modelName,
                    IsAvailable = true,
                    Type = matchingConfig.Value.Type,
                    EmbeddingDimension = matchingConfig.Value.EmbeddingDimension,
                    MaxContextLength = matchingConfig.Value.MaxContextLength,
                    Temperature = matchingConfig.Value.Temperature,
                    ModelOptions = matchingConfig.Value.ModelOptions ?? new Dictionary<string, object>()
                });
            }

            // Optional fallback: run discovery if not in config
            _logger.LogWarning($"⚠️ Model '{modelName}' not found in config. Triggering discovery...");
            return GetModelFromDiscoveryAsync(modelName);
        }
        private async Task<ModelConfiguration?> GetModelFromDiscoveryAsync(string modelName)
        {
            if (_availableModels.TryGetValue(modelName, out var model))
            {
                return model;
            }

            await DiscoverAvailableModelsAsync();
            if (_availableModels.TryGetValue(modelName, out model))
            {
                _logger.LogInformation($"✅ Model found via discovery: {modelName}");
                return model;
            }

            _logger.LogError($"❌ Model '{modelName}' not found even after discovery");
            return null;
        }


        public async Task<List<ModelConfiguration>> GetEmbeddingModelsAsync()
        {
            var models = await DiscoverAvailableModelsAsync();
            return models.Where(m => m.Type == "embedding" || m.Type == "both").ToList();
        }

        public async Task<List<ModelConfiguration>> GetGenerationModelsAsync()
        {
            var models = await DiscoverAvailableModelsAsync();
            return models.Where(m => m.Type == "generation" || m.Type == "both").ToList();
        }

        public async Task<bool> ValidateModelAsync(string modelName)
        {
            var model = await GetModelAsync(modelName);
            return model?.IsAvailable == true;
        }
    }
}