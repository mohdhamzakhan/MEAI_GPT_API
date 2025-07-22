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
    }
}
