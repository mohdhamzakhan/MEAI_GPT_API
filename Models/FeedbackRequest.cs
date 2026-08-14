namespace MEAI_GPT_API.Models
{
    public class FeedbackRequest
    {
        public string Question { get; set; } = "";
        public string CorrectAnswer { get; set; } = "";
        public string model { get; set; }
        public string sessionId { get; set; }
        /// <summary>
        /// Username of whoever is submitting this feedback. Required for
        /// corrections (checked against AccessControl:CorrectionAllowedUsers
        /// in appsettings.json) — not required for appreciation/"like".
        /// </summary>
        public string? UserId { get; set; }
    }
}
