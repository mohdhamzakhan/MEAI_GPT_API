using System.Text.Json.Serialization;

namespace MEAI_GPT_API.Models
{
    public class ChromaCollectionResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new();

        [JsonPropertyName("dimension")]
        public int? Dimension { get; set; }

        [JsonPropertyName("database")]
        public string Database { get; set; } = string.Empty;

        [JsonPropertyName("tenant")]
        public string Tenant { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("version")]
        public long Version { get; set; }

        [JsonPropertyName("configuration")]
        public Dictionary<string, object> Configuration { get; set; } = new();
    }

}
