namespace MEAI_GPT_API.Models
{
    public class UserHistoryOptions
    {
        public bool Enabled { get; set; } = false;
        public int MaxEntries { get; set; } = 4;
        public int MaxAgeDays { get; set; } = 30;
        public int CandidatePool { get; set; } = 40;
        public int MaxCharsPerAnswer { get; set; } = 350;
        public double MinConfidence { get; set; } = 0.6;
        public bool UseSemanticSelection { get; set; } = true;
        public double MinSimilarity { get; set; } = 0.5;
        public bool ExcludeCurrentSession { get; set; } = true;
    }
}