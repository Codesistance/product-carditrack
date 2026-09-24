using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OneOpenMemberChatSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before the index can exist, at most one session per caregiver and member may be
            // open. Every open session but the most recent is closed at its own last turn — for
            // quiet ones that is when they actually stopped, and for a duplicate opened by the race
            // this index now prevents (#1119) it keeps the newer one, which is the one the app has
            // been reading as current. Ties on LastTurnAtUtc break by Id so exactly one survives.
            migrationBuilder.Sql("""
                UPDATE "MemberChatSessions" AS s
                SET "EndedAtUtc" = s."LastTurnAtUtc"
                WHERE s."EndedAtUtc" IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM "MemberChatSessions" AS n
                      WHERE n."UserId" = s."UserId"
                        AND n."CardiMemberId" = s."CardiMemberId"
                        AND n."EndedAtUtc" IS NULL
                        AND (n."LastTurnAtUtc" > s."LastTurnAtUtc"
                             OR (n."LastTurnAtUtc" = s."LastTurnAtUtc" AND n."Id" > s."Id")));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MemberChatSessions_OneOpenPerCaregiverAndMember",
                table: "MemberChatSessions",
                columns: new[] { "UserId", "CardiMemberId" },
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemberChatSessions_OneOpenPerCaregiverAndMember",
                table: "MemberChatSessions");
        }
    }
}
