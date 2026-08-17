using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionnaireAskValidity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AskableUntilUtc",
                table: "MemberQuestionnaires",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberQuestionnaires_Status_AskableUntilUtc",
                table: "MemberQuestionnaires",
                columns: new[] { "Status", "AskableUntilUtc" });

            // Backfill, so the questions this column was added for do not outlive it. Every
            // time-scoped question still waiting on a family was written about the day it was
            // generated, and some of those days ended long ago — a caregiver was shown "did he feel
            // tired at all today?" the morning after, which is the failure the column answers.
            // Leaving them null would leave them askable forever.
            //
            // 27 hours because this cannot see the member's timezone and must not retire anything
            // early: the end of the local day a question was generated in is at most 24 hours after
            // it (generated just past local midnight) in any zone, and the grace the pipeline gives
            // is 3 more. An upper bound, so the sweep retires only questions that are genuinely
            // past their day whatever zone the member is in.
            //
            // Permanent questions are left null on purpose — they ask after a standing fact and
            // stay answerable indefinitely, which is exactly what null means here.
            migrationBuilder.Sql(
                """
                UPDATE "MemberQuestionnaires"
                SET "AskableUntilUtc" = "GeneratedAtUtc" + INTERVAL '27 hours'
                WHERE "Status" = 'Pending' AND "Scope" <> 'Permanent';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemberQuestionnaires_Status_AskableUntilUtc",
                table: "MemberQuestionnaires");

            migrationBuilder.DropColumn(
                name: "AskableUntilUtc",
                table: "MemberQuestionnaires");
        }
    }
}
