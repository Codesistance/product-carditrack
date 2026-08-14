using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Existing rows default to <c>TimeScoped</c> — the pre-existing recency-decay behaviour
    /// every row already had before this distinction existed, per
    /// <see cref="CardiTrack.Domain.Entities.MemberQuestionnaire.Scope"/>'s own default.
    /// <see cref="CardiTrack.Domain.Entities.MemberQuestionnaire.ExpiresAtUtc"/> is left null on
    /// backfill rather than computed: a question already asked has no
    /// <c>GeneratedAtUtc + TimeScopedAnswerLifetime</c> worth recomputing after the fact, and null
    /// there already means "keep reading this back by recency," which is what these rows did.
    /// </remarks>
    public partial class AddQuestionnaireScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "MemberQuestionnaires",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "MemberQuestionnaires",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "TimeScoped");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "MemberQuestionnaires");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "MemberQuestionnaires");
        }
    }
}
