using MEAI_GPT_API.Data;
using MEAI_GPT_API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service
{
    /// <summary>
    /// Builds a prompt-ready block of a user's own earlier questions/answers
    /// (across all their sessions) for personalisation. Controlled by the
    /// "UserHistory" section of appsettings.json; Enabled=false makes every
    /// call a no-op. Read via IOptionsMonitor, so changes apply without restart.
    /// </summary>
    public class UserHistoryService
    {
        private readonly IDbContextFactory<ConversationDbContext> _dbFactory;
        private readonly IOptionsMonitor<UserHistoryOptions> _options;
        private readonly ILogger<UserHistoryService> _logger;

        // Answers that are refusals / clarifying questions carry no useful facts about the user.
        private static readonly string[] NonAnswerPrefixes =
        {
            "I want to make sure I give you accurate information",
            "I found some relevant information, but I'm not confident",
            "I found related policy text but couldn't",
            "I couldn't generate an answer",
            "I still need a few more details",
            "CLARIFY:"
        };

        public UserHistoryService(
            IDbContextFactory<ConversationDbContext> dbFactory,
            IOptionsMonitor<UserHistoryOptions> options,
            ILogger<UserHistoryService> logger)
        {
            _dbFactory = dbFactory;
            _options = options;
            _logger = logger;
        }

        /// <summary>Read live on every call, so flipping appsettings.json takes effect immediately.</summary>
        public bool IsEnabled => _options.CurrentValue.Enabled;

        /// <summary>
        /// Returns a prompt-ready block of the user's relevant past activity,
        /// or null when disabled / nothing relevant / anything fails.
        /// Never throws: history is an enhancement, not a dependency.
        /// </summary>
        public async Task<string?> BuildHistoryContextAsync(
            string userId,
            string currentQuestion,
            string? currentSessionId,
            float[]? questionEmbedding,
            string? embeddingModel,
            CancellationToken ct = default)
        {
            var opt = _options.CurrentValue;
            if (!opt.Enabled) return null;
            if (string.IsNullOrWhiteSpace(userId) || userId == "system") return null;

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var cutoff = DateTime.UtcNow.AddDays(-opt.MaxAgeDays);
                var uid = userId.ToLower();   // AD usernames can differ in case

                var userSessions = db.ConversationSessions
                    .Where(s => s.UserId != null && s.UserId.ToLower() == uid)
                    .Select(s => s.SessionId);

                var query = db.ConversationEntries.AsNoTracking()
                    .Where(e => userSessions.Contains(e.SessionId)
                             && e.CreatedAt >= cutoff
                             && e.Confidence >= opt.MinConfidence);

                if (opt.ExcludeCurrentSession && !string.IsNullOrEmpty(currentSessionId))
                    query = query.Where(e => e.SessionId != currentSessionId);

                var candidates = (await query
                        .OrderByDescending(e => e.CreatedAt)
                        .Take(opt.CandidatePool)
                        .ToListAsync(ct))
                    .Where(e => IsUsableAnswer(
                        !string.IsNullOrWhiteSpace(e.CorrectedAnswer) ? e.CorrectedAnswer! : e.Answer))
                    .ToList();

                if (candidates.Count == 0) return null;

                // Pick the most relevant entries (semantic) or, without an embedding, by keyword overlap.
                List<ConversationEntry> picked;
                if (opt.UseSemanticSelection && questionEmbedding is { Length: > 0 })
                {
                    picked = candidates
                        .Where(e => embeddingModel == null || e.EmbeddingModel == embeddingModel)
                        .Select(e => (Entry: e, Sim: Cosine(questionEmbedding, e.QuestionEmbedding)))
                        .Where(x => x.Sim >= opt.MinSimilarity)
                        .OrderByDescending(x => x.Sim)
                        .Take(opt.MaxEntries)
                        .Select(x => x.Entry)
                        .ToList();
                }
                else
                {
                    // Avoids injecting unrelated "most recent" items when no embedding is available.
                    var qt = Tokens(currentQuestion);
                    picked = candidates
                        .Select(e => (Entry: e, Score: Tokens(e.Question).Intersect(qt).Count()))
                        .Where(x => x.Score >= 2)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Entry.CreatedAt)
                        .Take(opt.MaxEntries)
                        .Select(x => x.Entry)
                        .ToList();
                }

                if (picked.Count == 0) return null;

                var sb = new StringBuilder();
                sb.AppendLine("=== USER'S PREVIOUS ACTIVITY (personalisation context only) ===");
                sb.AppendLine("These are this user's own earlier questions and the answers given.");
                sb.AppendLine("Use them ONLY to understand what the user means (e.g. resolve a follow-up) and what they care about.");
                sb.AppendLine("Never present anything from this block as policy, and never state facts from it as if they were in the policy context.");
                sb.AppendLine("If it conflicts with the POLICY CONTEXT, the policy context wins.");
                sb.AppendLine();

                foreach (var e in picked.OrderBy(e => e.CreatedAt))
                {
                    var answer = !string.IsNullOrWhiteSpace(e.CorrectedAnswer) ? e.CorrectedAnswer! : e.Answer;
                    answer = answer.Replace("\r", " ").Replace("\n", " ").Trim();
                    if (answer.Length > opt.MaxCharsPerAnswer)
                        answer = answer[..opt.MaxCharsPerAnswer] + "…";

                    sb.AppendLine($"[{e.CreatedAt:yyyy-MM-dd}] Q: {e.Question.Trim()}");
                    sb.AppendLine($"A: {answer}");
                    sb.AppendLine();
                }
                sb.AppendLine("=== END USER'S PREVIOUS ACTIVITY ===");

                _logger.LogInformation("👤 User history: injected {N} entries for {User}", picked.Count, userId);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "User history lookup failed for {User}; continuing without it", userId);
                return null;
            }
        }

        private static bool IsUsableAnswer(string answer) =>
            !string.IsNullOrWhiteSpace(answer)
            && !NonAnswerPrefixes.Any(p => answer.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase))
            // short answer ending in "?" is almost certainly a clarifying question
            && !(answer.TrimEnd().EndsWith("?") && answer.Length < 300);

        private static HashSet<string> Tokens(string s) =>
            Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]{4,}")
                 .Select(m => m.Value)
                 .ToHashSet();

        private static double Cosine(float[] a, List<float> b)
        {
            if (b.Count == 0 || a.Length != b.Count) return 0;
            double dot = 0, ma = 0, mb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                ma += a[i] * a[i];
                mb += b[i] * b[i];
            }
            return ma == 0 || mb == 0 ? 0 : dot / (Math.Sqrt(ma) * Math.Sqrt(mb));
        }
    }
}