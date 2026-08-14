namespace MEAI_GPT_API.Models
{
    /// <summary>
    /// Access control for features that shouldn't be open to every user.
    /// Bound from the "AccessControl" section of appsettings.json.
    /// </summary>
    public class AccessControlOptions
    {
        /// <summary>
        /// Usernames (case-insensitive) allowed to submit corrections via
        /// POST /api/rag/feedback. Appreciation ("like") is intentionally
        /// NOT gated by this list — only corrections, since a bad correction
        /// can poison future answers for everyone, while a "like" can't.
        /// </summary>
        public List<string> CorrectionAllowedUsers { get; set; } = new();
    }
}