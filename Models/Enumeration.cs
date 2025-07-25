namespace MEAI_GPT_API.Models
{
    public class Enumeration
    {
        public enum ResponseMode
        {
            Fast,       // Return chunks directly
            Moderate,   // Summarize chunks via LLM
            Full        // Full RAG with chat history + relevant chunks + full prompt
        }
    }
}
