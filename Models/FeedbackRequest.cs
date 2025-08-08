namespace MEAI_GPT_API.Models
{
    public class FeedbackRequest
    {
        public string Question { get; set; } = "";
        public string CorrectAnswer { get; set; } = "";
        public string model { get; set; }
        public string sessionId { get; set; }
        public string Plant { get; set; }
    }
}
