namespace MEAI_GPT_API.Models
{
    public class CorrectionEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Question { get; set; }
        public string Answer { get; set; }
        public List<float> Embedding { get; set; }
        public DateTime Date { get; set; } = DateTime.Now;
        public string Model { get; set; }
        public string Plant { get; set; }
    }
}
