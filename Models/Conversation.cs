using System.Collections.Concurrent;

namespace MEAI_GPT_API.Models
{
    public class Conversation
    {
        private readonly ConcurrentDictionary<string, ConversationContext> _sessionContexts = new();

        public ConversationContext GetOrCreateConversationContext(
    string? sessionId,
    Func<string, Task<List<ConversationTurn>>>? historyLoader = null)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = $"temp_{Guid.NewGuid():N}";
            }

            var context = _sessionContexts.GetOrAdd(sessionId, _ => new ConversationContext
            {
                SessionId = sessionId,
                CreatedAt = DateTime.Now,
                LastAccessed = DateTime.Now
            });

            context.LastAccessed = DateTime.Now;

            // ✅ Load history if provided and not already loaded
            if (historyLoader != null && context.History.Count == 0)
            {
                var history = historyLoader(sessionId).GetAwaiter().GetResult();
                context.History.AddRange(history);
            }

            return context;
        }

        public class ConversationContext
        {
            public List<List<ConversationTurn>> TopicHistory { get; set; } = new();
            public List<ConversationTurn> CurrentTopic { get; set; } = new();//
            public List<EmbeddingData> RelevantChunks { get; set; } = new();
            public List<ConversationTurn> History { get; set; } = new();
            public List<string> NamedEntities { get; set; } = new(); // NEW
            public DateTime LastAccessed { get; set; }
            public string SessionId { get; set; } = Guid.NewGuid().ToString();
            public string Plant { get; set; } = "Centralized";
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public string LastTopicAnchor { get; set; } = ""; // 🆕 track root of current topic

            // 🆕 Supervisor-and-above vs. below-Supervisor clarification flow.
            // Set when a policy answer genuinely depends on the user's grade
            // and we've asked them to specify it instead of guessing or
            // blending both grades' provisions together.
            public bool AwaitingGradeClarification { get; set; } = false;
            public string? PendingClarificationQuestion { get; set; }
            /// <summary>
            /// Once known for this session, applied to every subsequent
            /// question so the user isn't re-asked every turn. "Supervisor
            /// and above" or "Below Supervisor" (see PolicyAnalysisService.
            /// TryResolveGradeAnswer) -- null until resolved.
            /// </summary>
            public string? EmployeeGrade { get; set; }

            // 🆕 General-purpose clarification flow, not restricted to grade.
            // Triggered when the self-verification pipeline can't ground an
            // answer confidently even after retrying with a stronger model —
            // instead of always falling back to "contact HR", the system
            // gets one shot at asking the user a specific question that
            // might resolve the ambiguity (which policy, which plant,
            // which benefit type, etc.), then re-answers using their reply.
            public bool AwaitingGeneralClarification { get; set; } = false;
            public string? PendingGeneralClarificationQuestion { get; set; }
            /// <summary>The original question being clarified, so the next
            /// turn's answer can be recombined with it rather than treated
            /// as a standalone question.</summary>
            public string? PendingGeneralClarificationOriginalQuestion { get; set; }
            /// <summary>
            /// Caps how many clarification rounds a single original question
            /// can go through before falling back to the flat refusal
            /// regardless — prevents an unresolvable ambiguity from turning
            /// into an endless back-and-forth.
            /// </summary>
            public int ClarificationAttemptCount { get; set; } = 0;

            // 🆕 Designation ask — separate from the general clarification
            // flow above and from AwaitingGradeClarification below. Triggered
            // once, the first time an employee with NO record at all in the
            // persistent employee directory asks something, rather than only
            // when a specific policy turns out to be grade-specific — the
            // goal is to populate the directory (used for retrieval-time
            // filtering) so this employee is never asked again, not just to
            // unblock the current answer. See DynamicRagService's
            // ProcessQueryStreamAsync for where this is resolved.
            public bool AwaitingDesignationClarification { get; set; } = false;
            public string? PendingDesignationClarificationOriginalQuestion { get; set; }
        }

        public class ConversationTurn
        {
            public string Question { get; set; }
            public string Answer { get; set; }
            public DateTime Timestamp { get; set; }
            public List<string> Sources { get; set; }
            public string SessionId { get; set; }
        }
    }
}