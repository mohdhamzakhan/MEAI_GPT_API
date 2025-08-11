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
            try
            {
                var response = await _httpClient.GetAsync("/api/tags");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to discover models: {response.StatusCode}");
                    return GetHardcodedModels(); // Fallback
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Ollama models response: {json}");

                using var doc = JsonDocument.Parse(json);
                var models = new List<ModelConfiguration>();

                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    foreach (var modelElement in modelsArray.EnumerateArray())
                    {
                        if (modelElement.TryGetProperty("name", out var nameProperty))
                        {
                            var modelName = nameProperty.GetString();
                            if (string.IsNullOrEmpty(modelName)) continue;

                            var config = new ModelConfiguration
                            {
                                Name = modelName,
                                Type = DetermineModelType(modelName),
                                MaxContextLength = GetMaxContextLength(modelName),
                                EmbeddingDimension = GetEmbeddingDimension(modelName),
                                ModelOptions = new Dictionary<string, object>
                        {
                            { "num_ctx", 2048 },
                            { "temperature", 0.1 }
                        }
                            };

                            models.Add(config);
                            _logger.LogInformation($"Discovered model: {modelName} (Type: {config.Type})");
                        }
                    }
                }

                if (!models.Any())
                {
                    _logger.LogWarning("No models discovered from Ollama, using hardcoded models");
                    return GetHardcodedModels();
                }

                return models;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover available models");
                return GetHardcodedModels();
            }
        }
        private List<ModelConfiguration> GetHardcodedModels()
        {
            return new List<ModelConfiguration>
    {
        new ModelConfiguration
        {
            Name = "nomic-embed-text:v1.5",
            Type = "embedding",
            MaxContextLength = 2048,
            EmbeddingDimension = 768, // Nomic embedding dimension
            ModelOptions = new Dictionary<string, object>
            {
                { "num_ctx", 2048 }
            }
        },
        new ModelConfiguration
        {
            Name = "mistral:latest",
            Type = "generation",
            MaxContextLength = 4096,
            EmbeddingDimension = 0,
            ModelOptions = new Dictionary<string, object>
            {
                { "num_ctx", 4096 },
                { "temperature", 0.7 }
            }
        },
        new ModelConfiguration
        {
            Name = "llama3.1:8b",
            Type = "generation",
            MaxContextLength = 4096,
            EmbeddingDimension = 0,
            ModelOptions = new Dictionary<string, object>
            {
                { "num_ctx", 4096 },
                { "temperature", 0.7 }
            }
        }
    };
        }

        private string DetermineModelType(string modelName)
        {
            if (modelName.Contains("embed") || modelName.Contains("nomic"))
                return "embedding";
            if (modelName.Contains("mistral") || modelName.Contains("llama") || modelName.Contains("qwen"))
                return "generation";
            return "generation"; // Default
        }
        private int GetMaxContextLength(string modelName)
        {
            if (modelName.Contains("nomic")) return 2048;
            if (modelName.Contains("mistral")) return 4096;
            return 2048; // Default
        }

        private int GetEmbeddingDimension(string modelName)
        {
            if (modelName.Contains("nomic-embed-text")) return 768;
            return 0; // Not an embedding model
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
            if (_config.ModelConfigurations!.TryGetValue(modelName.Replace(':','_'), out var config))
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