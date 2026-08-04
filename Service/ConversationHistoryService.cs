// ============================================================
// FILE 1: ConversationHistoryService.cs  (NEW FILE)
// Centralizes all history management so both MEAI and 
// non-MEAI paths share one source of truth.
// ============================================================
using System.Collections.Concurrent;
using MEAI_GPT_API.Models;
using static MEAI_GPT_API.Models.Conversation;

namespace MEAI_GPT_API.Services
{
    /// <summary>
    /// Single source of truth for in-memory conversation history.
    /// Registered as Singleton — safe because it only holds plain data,
    /// not a DbContext.
    /// </summary>
    public class ConversationHistoryService
    {
        // Bounded LRU-style store: max 1000 sessions
        private const int MaxSessions = 1000;
        private const int MaxTurnsPerSession = 15;

        private readonly ConcurrentDictionary<string, LinkedList<ConversationTurn>> _sessions = new();
        private readonly ConcurrentQueue<string> _insertionOrder = new();
        private readonly ILogger<ConversationHistoryService> _logger;

        public ConversationHistoryService(ILogger<ConversationHistoryService> logger)
        {
            _logger = logger;
        }

        // -------------------------------------------------------
        // READ
        // -------------------------------------------------------

        /// <summary>Returns a snapshot (newest-last) of the last N turns.</summary>
        public List<ConversationTurn> GetHistory(string sessionId, int limit = MaxTurnsPerSession)
        {
            if (_sessions.TryGetValue(sessionId, out var list))
                return list.TakeLast(limit).ToList();
            return new List<ConversationTurn>();
        }

        // -------------------------------------------------------
        // WRITE
        // -------------------------------------------------------

        /// <summary>
        /// Adds a turn. Trims to MaxTurnsPerSession automatically.
        /// Evicts oldest session when MaxSessions is reached.
        /// </summary>
        public void AddTurn(string sessionId, string question, string answer, List<string>? sources = null)
        {
            var turn = new ConversationTurn
            {
                Question = question,
                Answer   = answer,
                Timestamp = DateTime.UtcNow,
                Sources  = sources ?? new List<string>()
            };

            _sessions.AddOrUpdate(
                sessionId,
                _ =>
                {
                    TrackInsertion(sessionId);
                    var ll = new LinkedList<ConversationTurn>();
                    ll.AddLast(turn);
                    return ll;
                },
                (_, existing) =>
                {
                    existing.AddLast(turn);
                    while (existing.Count > MaxTurnsPerSession)
                        existing.RemoveFirst();
                    return existing;
                });

            _logger.LogDebug("History updated for {SessionId}: {Count} turns",
                sessionId, _sessions[sessionId].Count);
        }

        /// <summary>
        /// Bulk-loads turns from DB on first access.
        /// Skips if the session already has in-memory turns to avoid duplicates.
        /// </summary>
        public void SeedFromDatabase(string sessionId, IEnumerable<ConversationTurn> dbTurns)
        {
            if (_sessions.ContainsKey(sessionId))
                return; // already warm

            var ordered = dbTurns.OrderBy(t => t.Timestamp).TakeLast(MaxTurnsPerSession).ToList();
            if (!ordered.Any()) return;

            var ll = new LinkedList<ConversationTurn>(ordered);
            if (_sessions.TryAdd(sessionId, ll))
                TrackInsertion(sessionId);
        }

        public void Clear(string sessionId) =>
            _sessions.TryRemove(sessionId, out _);

        // -------------------------------------------------------
        // PRIVATE
        // -------------------------------------------------------

        private void TrackInsertion(string sessionId)
        {
            _insertionOrder.Enqueue(sessionId);
            while (_sessions.Count > MaxSessions && _insertionOrder.TryDequeue(out var oldest))
                _sessions.TryRemove(oldest, out _);
        }
    }
}
