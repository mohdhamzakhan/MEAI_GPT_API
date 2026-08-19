namespace MEAI_GPT_API.Models
{
    public class DynamicRAGConfiguration
    {
        public string DefaultGenerationModel { get; set; } = string.Empty;
        public string GroundingRetryModel { get; set; } = string.Empty;
        public string DefaultEmbeddingModel { get; set; } = string.Empty;
        public string DefaultCodingModel { get; set; } = string.Empty;
        public string VerifierModel { get; set; } = string.Empty;
        public bool AutoDiscoverModels { get; set; } = true;
        public int ModelDiscoveryTimeoutMs { get; set; } = 30000;
        public string PolicyFolder { get; set; } = "./policies";
        public string ContextFolder { get; set; } = "./context";
        public List<string> SupportedExtensions { get; set; } = new();
        public PreferredModels PreferredModels { get; set; } = new();
        public Dictionary<string, ModelConfigurationSettings>? ModelConfigurations { get; set; }

        /// <summary>
        /// Extra document folders to index, beyond the per-plant folders and
        /// "Centralized". Each entry is a subfolder name under PolicyFolder.
        /// Add a new folder here and it's picked up automatically — no code
        /// change needed. If RestrictedToPlants is empty, the folder's
        /// documents are visible to every plant's queries; if non-empty,
        /// they're only visible to the listed plants.
        /// </summary>
        public List<DocumentSourceOptions> AdditionalDocumentSources { get; set; } = new();
        /// <summary>
        /// Numbered-reference types (Annexure, Section, Clause, Form, etc.)
        /// that get exact-match extraction at index time and exact-match
        /// lookup/boosting at query time — the same treatment originally
        /// built specifically for Annexure references, generalized so a new
        /// reference type can be added here without any code change.
        /// </summary>
        public List<ReferenceTypeOptions> ReferenceTypes { get; set; } = new();
    }

    public class ReferenceTypeOptions
    {
        /// <summary>Human-readable name, e.g. "Annexure", "Clause", "Form".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Regex used both to detect this reference type in a user's
        /// question and to extract matching numbers from document text at
        /// index time. Must contain exactly one capture group for the
        /// reference number, e.g. @"annex(?:ure)?\s*(?:no\.?)?\s*(\d)".
        /// </summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>
        /// The metadata field name used to store extracted numbers for this
        /// reference type on each chunk, e.g. "annexure_refs". Should be
        /// unique per reference type.
        /// </summary>
        public string MetadataKey { get; set; } = string.Empty;
    }

    public class DocumentSourceOptions
    {
        /// <summary>Subfolder name under PolicyFolder, e.g. "Technical" or "Customers".</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>
        /// Plant names (matching your Plant config) this source is limited
        /// to. Leave empty for "visible to all plants" (like Centralized).
        /// </summary>
        public List<string> RestrictedToPlants { get; set; } = new();
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