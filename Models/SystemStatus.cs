namespace MEAI_GPT_API.Models
{
    public class SystemStatus
    {
        public int TotalEmbeddings { get; set; }
        public int TotalCorrections { get; set; }
        public DateTime LastUpdated { get; set; }
        public bool IsHealthy { get; set; }
        public string PoliciesFolder { get; set; } = "";
        public List<string> SupportedExtensions { get; set; } = new();
        public List<ModelConfiguration> AvailableModels { get; set; } = new();
        public List<ModelConfiguration> EmbeddingModels { get; set; } = new();
        public List<ModelConfiguration> GenerationModels { get; set; } = new();
        public string DefaultEmbeddingModel { get; set; } = string.Empty;
        public string DefaultGenerationModel { get; set; } = string.Empty;
    }
}
