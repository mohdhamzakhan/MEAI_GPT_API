using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MEAI_GPT_API.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConversationEntryId",
                table: "GroundingFailures",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "GroundingFailures",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnswerQualityJson",
                table: "ConversationEntries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversationEntryId",
                table: "GroundingFailures");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "GroundingFailures");

            migrationBuilder.DropColumn(
                name: "AnswerQualityJson",
                table: "ConversationEntries");
        }
    }
}
