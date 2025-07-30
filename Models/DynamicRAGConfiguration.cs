namespace MEAI_GPT_API.Models
{
    public class DynamicRAGConfiguration
    {
        public string DefaultGenerationModel { get; set; } = string.Empty;
        public string DefaultEmbeddingModel { get; set; } = string.Empty;
        public bool AutoDiscoverModels { get; set; } = true;
        public int ModelDiscoveryTimeoutMs { get; set; } = 30000;
        public string PolicyFolder { get; set; } = "./policies";
        public string ContextFolder { get; set; } = "./context";
        public List<string> SupportedExtensions { get; set; } = new();
        public PreferredModels PreferredModels { get; set; } = new();
        public Dictionary<string, ModelConfigurationSettings> ModelConfigurations { get; set; } = new();
    }
    public class PreferredModels
    {
        public List<string> Embedding { get; set; } = new();
        public List<string> Generation { get; set; } = new();
    }
    public class ModelConfigurationSettings
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int MaxContextLength { get; set; }
        public double Temperature { get; set; }
        public int EmbeddingDimension { get; set; }
        public Dictionary<string, object> ModelOptions { get; set; } = new();
    }
}
