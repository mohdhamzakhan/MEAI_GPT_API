public class ChromaDbOptions
{
    public string BaseUrl { get; set; } = "192.168.129.203:7654";
    public int TimeoutMinutes { get; set; } = 5;
    public string Tenant { get; set; } = "default_tenant";
    public string Database { get; set; } = "default_database";
    public Dictionary<string, string> Collections { get; set; } = new();
    public string PolicyFolder { get; set; } = "./policies";
    public string ContextFolder { get; set; } = "./context";
    public List<string> SupportedExtensions { get; set; } = new();
}