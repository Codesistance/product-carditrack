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
            // Without the member's timezone this cannot land on their local midnight, so the
            // approximation is the UTC day's end plus the same three-hour grace the live path
            // uses, floored at the same six-hour minimum window. A flat "+ 27 hours from
            // GeneratedAtUtc" would leave an evening question askable through the following
            // evening — past the morning the screenshot failed on. New rows get a proper
            // zone-aware deadline from DigestGenerationService; this only cleans up what is
            // already pending when the column lands.
            //
            // Permanent questions are left null on purpose — they ask after a standing fact and
            // stay answerable indefinitely, which is exactly what null means here.
            migrationBuilder.Sql(
                """
                UPDATE "MemberQuestionnaires"
                SET "AskableUntilUtc" = GREATEST(
                    date_trunc('day', "GeneratedAtUtc") + INTERVAL '1 day 3 hours',
                    "GeneratedAtUtc" + INTERVAL '6 hours')
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
