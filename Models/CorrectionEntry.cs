namespace MEAI_GPT_API.Models
{
    public class CorrectionEntry
    {
        public string Id { get; set; }
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.Now;
    }
}
