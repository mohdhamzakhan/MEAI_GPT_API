using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MEAI_GPT_API.Migrations
{
    /// <inheritdoc />
    public partial class GroundingFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAccessedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConversationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTopicTag = table.Column<string>(type: "TEXT", nullable: true),
                    LastTopicAnchor = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UserPlant = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationSessions", x => x.Id);
                    table.UniqueConstraint("AK_ConversationSessions_SessionId", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "GroundingFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Plant = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RetrievedSourcesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    GroundingReason = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    GenerationModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ResolvedByCorrection = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroundingFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConversationEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuestionEmbeddingJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerEmbeddingJson = table.Column<string>(type: "TEXT", nullable: false),
                    NamedEntitiesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    WasAppreciated = table.Column<bool>(type: "INTEGER", nullable: false),
                    CorrectedAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    TopicTag = table.Column<string>(type: "TEXT", nullable: true),
                    FollowUpToId = table.Column<int>(type: "INTEGER", nullable: true),
                    Plant = table.Column<string>(type: "TEXT", nullable: true),
                    GenerationModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    ProcessingTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RelevantChunksCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    IsFromCorrection = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_ConversationEntries_FollowUpToId",
                        column: x => x.FollowUpToId,
                        principalTable: "ConversationEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_ConversationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ConversationSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_FollowUpToId",
                table: "ConversationEntries",
                column: "FollowUpToId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntry_Appreciated",
                table: "ConversationEntries",
                column: "WasAppreciated");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntry_CreatedAt",
                table: "ConversationEntries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntry_Models",
                table: "ConversationEntries",
                columns: new[] { "GenerationModel", "EmbeddingModel" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntry_Session_Time",
                table: "ConversationEntries",
                columns: new[] { "SessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntry_SessionId",
                table: "ConversationEntries",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntry_Topic",
                table: "ConversationEntries",
                column: "TopicTag");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSession_LastAccessed",
                table: "ConversationSessions",
                column: "LastAccessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSession_SessionId",
                table: "ConversationSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSession_UserId",
                table: "ConversationSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroundingFailure_CreatedAt",
                table: "GroundingFailures",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GroundingFailure_Plant",
                table: "GroundingFailures",
                column: "Plant");

            migrationBuilder.CreateIndex(
                name: "IX_GroundingFailure_Resolved",
                table: "GroundingFailures",
                column: "ResolvedByCorrection");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationEntries");

            migrationBuilder.DropTable(
                name: "GroundingFailures");

            migrationBuilder.DropTable(
                name: "ConversationSessions");
        }
    }
}
