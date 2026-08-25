// Models/GroundingFailure.cs
using System.ComponentModel.DataAnnotations;

namespace MEAI_GPT_API.Models
{
    // Every time SelfVerifier flags an answer as ungrounded and it survives
    // even the GroundingRetryModel retry (i.e. the user actually saw a
    // refusal), a row is written here. This is intentionally NOT tied to
    // ConversationEntry — a refusal never produces a "real" answer worth
    // browsing in session history, but IS worth aggregating across sessions
    // to spot recurring gaps (same document repeatedly failing, same wrong
    // document repeatedly winning retrieval, etc.) that a human should look
    // at, distinct from the correction-driven auto-fix pipeline in
    // LearnedTriggerService.
    public class GroundingFailure
    {
        [Key]
        public int Id { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public string Question { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Plant { get; set; } = string.Empty;

        // Which document(s) retrieval actually fed to generation — the
        // "wrong" set, from a grounding perspective, since the answer built
        // from them failed verification.
        public string RetrievedSourcesJson { get; set; } = "[]";

        // Verbatim reasoning string from SelfVerifier.grounding_reason —
        // this is what tells a human WHY it failed (fabricated numbers vs.
        // wrong document entirely vs. genuinely missing content), not just
        // that it failed.
        public string GroundingReason { get; set; } = string.Empty;

        public double Confidence { get; set; }

        [MaxLength(100)]
        public string GenerationModel { get; set; } = string.Empty;

        // True if this failure was later resolved by a human correction
        // (see LearnedTriggerService.PromoteCorrectionAsync) for the same
        // or a closely related question — lets the admin view distinguish
        // "still open" from "already fixed" failures without deleting the
        // historical record.
        public bool ResolvedByCorrection { get; set; } = false;
    }
}