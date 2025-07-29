public class ChromaDbOptions
{
    public string BaseUrl { get; set; } = "192.168.129.203:7654";
    public string Tenant { get; set; } = "default_tenant";
    public string Database { get; set; } = "default_database";
    public Dictionary<string, string> Collections { get; set; } = new();
    public int TimeoutMinutes { get; set; } = 5;
    public string PolicyFolder { get; set; } = "D:\\Code\\MEAIGPT\\MEAIRAG\\policies";
    public List<string> SupportedExtensions { get; set; } = new() { ".txt", ".md", ".pdf", ".docx" };
    public string DefaultEmbeddingModel { get; set; } = "mistral:latest";
    public string ContextFolder { get; set; } = "D:\\Code\\MEAIGPT\\MEAIRAG\\Context";
}