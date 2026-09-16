using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Existing rows default to <c>Digest</c> — they were all questions the service asked,
    /// per <see cref="CardiTrack.Domain.Entities.MemberQuestionnaire.Origin"/>'s own default.
    /// A volunteered standing fact is a new write path, so there is nothing to backfill as
    /// <c>Family</c>.
    /// </remarks>
    public partial class AddQuestionnaireOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "MemberQuestionnaires",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Digest");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "MemberQuestionnaires");
        }
    }
}
