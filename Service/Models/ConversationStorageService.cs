// Services/ConversationStorageService.cs
using MEAI_GPT_API.Data;
using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service.Interface;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MEAI_GPT_API.Services
{
    public class ConversationStorageService : IConversationStorageService
    {
        private readonly ConversationDbContext _context;
        private readonly ILogger<ConversationStorageService> _logger;
        private readonly IModelManager _modelManager;

        public ConversationStorageService(
            ConversationDbContext context,
            ILogger<ConversationStorageService> logger,
            IModelManager modelManager)
        {
            _context = context;
            _logger = logger;
            _modelManager = modelManager;
        }

        public async Task SaveConversationAsync(ConversationEntry entry)
        {
            try
            {
                _context.ConversationEntries.Add(entry);
                await _context.SaveChangesAsync();

                // Update session statistics
                var session = await GetOrCreateSessionAsync(entry.SessionId);
                session.ConversationCount++;
                session.LastAccessedAt = DateTime.UtcNow;
                session.LastTopicTag = entry.TopicTag;

                await UpdateSessionAsync(session);

                _logger.LogInformation($"💾 Saved conversation {entry.Id} for session {entry.SessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save conversation for session {entry.SessionId}");
                throw;
            }
        }

        public async Task<ConversationEntry?> GetConversationAsync(int id)
        {
            return await _context.ConversationEntries
                .Include(c => c.ParentConversation)
                .Include(c => c.FollowUps)
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<List<ConversationEntry>> GetSessionConversationsAsync(string sessionId, int limit = 50)
        {
            return await _context.ConversationEntries
                .Where(c => c.SessionId == sessionId)
                .OrderBy(c => c.CreatedAt)
                .Take(limit)
                .Include(c => c.ParentConversation)
                .ToListAsync();
        }

        public async Task<ConversationSession> GetOrCreateSessionAsync(string sessionId, string? userId = null, string? plant = null)
        {
            var session = await _context.ConversationSessions
                .FirstOrDefaultAsync(s => s.SessionId == sessionId);

            if (session == null)
            {
                session = new ConversationSession
                {
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessedAt = DateTime.UtcNow,
                    UserId = userId,
                    UserPlant = plant
                };

                _context.ConversationSessions.Add(session);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"🆕 Created new session: {sessionId}");
            }
            else
            {
                session.LastAccessedAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(userId)) session.UserId = userId;
                if (!string.IsNullOrEmpty(plant)) session.UserPlant = plant;
            }

            return session;
        }

        public async Task UpdateSessionAsync(ConversationSession session)
        {
            _context.ConversationSessions.Update(session);
            await _context.SaveChangesAsync();
        }

        public async Task MarkAsAppreciatedAsync(int conversationId)
        {
            var conversation = await _context.ConversationEntries.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.WasAppreciated = true;
                await _context.SaveChangesAsync();

                _logger.LogInformation($"⭐ Marked conversation {conversationId} as appreciated");
            }
        }

        public async Task SaveCorrectionAsync(int conversationId, string correctedAnswer)
        {
            var conversation = await _context.ConversationEntries.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.CorrectedAnswer = correctedAnswer;
                await _context.SaveChangesAsync();

                _logger.LogInformation($"✏️ Saved correction for conversation {conversationId}");
            }
        }

        public async Task<List<ConversationSearchResult>> SearchSimilarConversationsAsync(
            List<float> queryEmbedding,
            string plant,
            double threshold = 0.7,
            int limit = 10)
        {
            var results = new List<ConversationSearchResult>();

            try
            {
                // Get all conversations with embeddings
                var conversations = await _context.ConversationEntries
                    .Where(c => (c.QuestionEmbeddingJson != string.Empty || c.AnswerEmbeddingJson != string.Empty) && c.Plant == plant)
                    .ToListAsync();

                foreach (var conv in conversations)
                {
                    double maxSimilarity = 0;
                    string matchType = "";

                    // Check question similarity
                    if (!string.IsNullOrEmpty(conv.QuestionEmbeddingJson))
                    {
                        var questionSim = CosineSimilarity(queryEmbedding, conv.QuestionEmbedding);
                        if (questionSim > maxSimilarity)
                        {
                            maxSimilarity = questionSim;
                            matchType = "question";
                        }
                    }

                    // Check answer similarity
                    if (!string.IsNullOrEmpty(conv.AnswerEmbeddingJson))
                    {
                        var answerSim = CosineSimilarity(queryEmbedding, conv.AnswerEmbedding);
                        if (answerSim > maxSimilarity)
                        {
                            maxSimilarity = answerSim;
                            matchType = "answer";
                        }
                    }

                    if (maxSimilarity >= threshold)
                    {
                        results.Add(new ConversationSearchResult
                        {
                            Entry = conv,
                            Similarity = maxSimilarity,
                            MatchType = matchType
                        });
                    }
                }

                return results
                    .OrderByDescending(r => r.Similarity)
                    .Take(limit)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search similar conversations");
                return new List<ConversationSearchResult>();
            }
        }

        public async Task<List<ConversationEntry>> GetAppreciatedAnswersAsync(string? topicTag = null, int limit = 100)
        {
            var query = _context.ConversationEntries
                .Where(c => c.WasAppreciated);

            if (!string.IsNullOrEmpty(topicTag))
            {
                query = query.Where(c => c.TopicTag == topicTag);
            }

            return await query
                .OrderByDescending(c => c.CreatedAt)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<ConversationStats> GetConversationStatsAsync()
        {
            var totalConversations = await _context.ConversationEntries.CountAsync();
            var totalSessions = await _context.ConversationSessions.CountAsync();
            var appreciatedAnswers = await _context.ConversationEntries.CountAsync(c => c.WasAppreciated);
            var correctedAnswers = await _context.ConversationEntries.CountAsync(c => c.CorrectedAnswer != null);

            var topicDistribution = await _context.ConversationEntries
                .Where(c => c.TopicTag != null)
                .GroupBy(c => c.TopicTag)
                .ToDictionaryAsync(g => g.Key!, g => g.Count());

            var modelUsage = await _context.ConversationEntries
                .GroupBy(c => c.GenerationModel)
                .ToDictionaryAsync(g => g.Key, g => g.Count());

            var avgConfidence = await _context.ConversationEntries.AverageAsync(c => c.Confidence);
            var avgProcessingTime = await _context.ConversationEntries.AverageAsync(c => (double)c.ProcessingTimeMs);

            var oldestConv = await _context.ConversationEntries.MinAsync(c => c.CreatedAt);
            var newestConv = await _context.ConversationEntries.MaxAsync(c => c.CreatedAt);

            return new ConversationStats
            {
                TotalConversations = totalConversations,
                TotalSessions = totalSessions,
                AppreciatedAnswers = appreciatedAnswers,
                CorrectedAnswers = correctedAnswers,
                TopicDistribution = topicDistribution,
                ModelUsage = modelUsage,
                AverageConfidence = avgConfidence,
                AverageProcessingTime = (long)avgProcessingTime,
                OldestConversation = oldestConv,
                NewestConversation = newestConv
            };
        }

        public async Task CleanupOldSessionsAsync(TimeSpan maxAge)
        {
            var cutoffDate = DateTime.UtcNow - maxAge;
            var oldSessions = await _context.ConversationSessions
                .Where(s => s.LastAccessedAt < cutoffDate)
                .ToListAsync();

            _context.ConversationSessions.RemoveRange(oldSessions);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"🧹 Cleaned up {oldSessions.Count} old sessions");
        }

        public async Task<List<ConversationEntry>> GetFollowUpChainAsync(int parentId)
        {
            var chain = new List<ConversationEntry>();
            var current = await GetConversationAsync(parentId);

            while (current != null)
            {
                chain.Add(current);
                current = await _context.ConversationEntries
                    .FirstOrDefaultAsync(c => c.FollowUpToId == current.Id);
            }

            return chain;
        }

        public async Task AssignTopicTagAsync(int conversationId, string topicTag)
        {
            var conversation = await _context.ConversationEntries.FindAsync(conversationId);
            if (conversation != null)
            {
                conversation.TopicTag = topicTag;
                await _context.SaveChangesAsync();

                _logger.LogInformation($"🏷️ Assigned topic tag '{topicTag}' to conversation {conversationId}");
            }
        }

        public async Task<Dictionary<string, List<ConversationEntry>>> GroupConversationsByTopicAsync(string sessionId)
        {
            var conversations = await GetSessionConversationsAsync(sessionId);

            return conversations
                .Where(c => !string.IsNullOrEmpty(c.TopicTag))
                .GroupBy(c => c.TopicTag!)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        private double CosineSimilarity(List<float> a, List<float> b)
        {
            if (a.Count != b.Count) return 0;

            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Count; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }
    }
}