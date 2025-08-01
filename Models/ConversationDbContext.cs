// Data/ConversationDbContext.cs
using MEAI_GPT_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace MEAI_GPT_API.Data
{
    public class ConversationDbContext : DbContext
    {
        public ConversationDbContext(DbContextOptions<ConversationDbContext> options) : base(options)
        {
        }

        public DbSet<ConversationEntry> ConversationEntries { get; set; }
        public DbSet<ConversationSession> ConversationSessions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ConversationEntry configuration
            modelBuilder.Entity<ConversationEntry>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.SessionId)
                      .HasDatabaseName("IX_ConversationEntry_SessionId");

                entity.HasIndex(e => new { e.SessionId, e.CreatedAt })
                      .HasDatabaseName("IX_ConversationEntry_Session_Time");

                entity.HasIndex(e => e.WasAppreciated)
                      .HasDatabaseName("IX_ConversationEntry_Appreciated");

                entity.HasIndex(e => e.TopicTag)
                      .HasDatabaseName("IX_ConversationEntry_Topic");

                entity.HasIndex(e => e.CreatedAt)
                      .HasDatabaseName("IX_ConversationEntry_CreatedAt");

                entity.HasIndex(e => new { e.GenerationModel, e.EmbeddingModel })
                      .HasDatabaseName("IX_ConversationEntry_Models");

                // Self-referencing relationship for follow-ups
                entity.HasOne(e => e.ParentConversation)
                      .WithMany(e => e.FollowUps)
                      .HasForeignKey(e => e.FollowUpToId)
                      .OnDelete(DeleteBehavior.SetNull);

                // JSON column configurations
                entity.Property(e => e.QuestionEmbeddingJson)
                      .HasColumnType("TEXT");

                entity.Property(e => e.AnswerEmbeddingJson)
                      .HasColumnType("TEXT");

                entity.Property(e => e.NamedEntitiesJson)
                      .HasColumnType("TEXT")
                      .HasDefaultValue("[]");

                entity.Property(e => e.SourcesJson)
                      .HasColumnType("TEXT")
                      .HasDefaultValue("[]");

                entity.Property(e => e.Question)
                      .HasColumnType("TEXT");

                entity.Property(e => e.Answer)
                      .HasColumnType("TEXT");

                entity.Property(e => e.CorrectedAnswer)
                      .HasColumnType("TEXT");
            });

            // ConversationSession configuration
            modelBuilder.Entity<ConversationSession>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.SessionId)
                      .IsUnique()
                      .HasDatabaseName("IX_ConversationSession_SessionId");

                entity.HasIndex(e => e.LastAccessedAt)
                      .HasDatabaseName("IX_ConversationSession_LastAccessed");

                entity.HasIndex(e => e.UserId)
                      .HasDatabaseName("IX_ConversationSession_UserId");

                entity.Property(e => e.MetadataJson)
                      .HasColumnType("TEXT")
                      .HasDefaultValue("{}");

                // Relationship with ConversationEntry
                entity.HasMany(e => e.Conversations)
                      .WithOne()
                      .HasForeignKey(ce => ce.SessionId)
                      .HasPrincipalKey(cs => cs.SessionId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlite("Data Source=conversations.db");
            }
        }
    }
}