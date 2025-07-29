namespace MEAI_GPT_API.Models
{
    public class DynamicRAGConfiguration
    {
        public string? DefaultGenerationModel { get; set; }
        public string? DefaultEmbeddingModel { get; set; }
        public bool AutoDiscoverModels { get; set; } = true;
        public int ModelDiscoveryTimeoutMs { get; set; } = 30000;
        public Dictionary<string, ModelConfiguration> PreConfiguredModels { get; set; } = new();
    }
}
