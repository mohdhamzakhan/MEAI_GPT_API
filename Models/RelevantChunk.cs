namespace MEAI_GPT_API.Models
{
    public class RelevantChunk
    {
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public double Similarity { get; set; }
        public string PolicyType { get; set; } = "";
    }
}
