namespace MEAI_GPT_API.Models
{
    public class EmbeddingEntry
    {
        public string Text { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public string SourceFile { get; set; } = "";
        public DateTime LastModified { get; set; }
        public string Model { get; set; } = "";
    }
}
