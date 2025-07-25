public class ChromaCollection
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public Dictionary<string, object> Metadata { get; set; } = new();
    public int Count { get; set; }
}