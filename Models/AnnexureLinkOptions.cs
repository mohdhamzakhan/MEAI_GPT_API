namespace MEAI_GPT_API.Models
{
    /// <summary>
    /// Configuration for linking retrieved annexures to files on a network
    /// share. Bound from the "AnnexureLinks" section of appsettings.json.
    /// Disabled by default so nothing changes until BaseServerPath is set
    /// and Enabled is flipped to true.
    /// </summary>
    public class AnnexureLinkOptions
    {
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Base folder under which each policy has its own subfolder of
        /// annexure files, e.g. "\\SERVERNAME\policies". Combined at runtime
        /// as {BaseServerPath}\{exact source document filename}\Annexure {N}.*
        /// </summary>
        public string BaseServerPath { get; set; } = "";

        /// <summary>
        /// Extensions considered valid annexure files. The actual extension
        /// on disk is used as-is — this list only controls which files are
        /// eligible to match, it does not get appended blindly.
        /// </summary>
        public List<string> AllowedExtensions { get; set; } = new()
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx"
        };

        /// <summary>
        /// How long a resolved (or not-found) lookup is cached before being
        /// re-checked against the file system. Keeps repeated queries about
        /// the same annexure from hitting the network share every time.
        /// </summary>
        public int CacheMinutes { get; set; } = 10;
    }
}