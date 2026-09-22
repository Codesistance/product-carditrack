using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// The Family ID a family is known by, and the requests people make to join one.
    /// </summary>
    /// <remarks>
    /// Every existing family is backfilled with a code, because a family without one cannot be
    /// joined and there would be no later moment to mint them in — <c>FamilyId</c> is minted at
    /// creation, and these rows are already created. The generator below is the SQL twin of
    /// <c>FamilyIdentifier.Mint</c>: the same 31-character alphabet with the read-aloud collisions
    /// (I/1, O/0) removed, and a retry loop because a random eight-character code can collide with
    /// one already issued and the unique index would otherwise fail the migration.
    /// </remarks>
    /// <inheritdoc />
    public partial class AddFamilyIdAndJoinRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FamilyId",
                table: "Organizations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "FamilyJoinRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyJoinRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_FamilyId",
                table: "Organizations",
                column: "FamilyId",
                unique: true,
                filter: "\"FamilyId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyJoinRequests_OrganizationId_Status",
                table: "FamilyJoinRequests",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyJoinRequests_RequestedByUserId",
                table: "FamilyJoinRequests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyJoinRequests_RequestedByUserId_OrganizationId",
                table: "FamilyJoinRequests",
                columns: new[] { "RequestedByUserId", "OrganizationId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");
            // One code per existing family. The loop retries on collision rather than trusting
            // eight random characters to be unique across the estate; at this size a second
            // attempt is already vanishingly unlikely, but a migration that can fail on a dice
            // roll is not one anybody should have to re-run.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    org RECORD;
                    candidate TEXT;
                    alphabet CONSTANT TEXT := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
                BEGIN
                    FOR org IN SELECT "Id" FROM "Organizations" WHERE "FamilyId" = '' LOOP
                        LOOP
                            candidate := '';
                            FOR _ IN 1..8 LOOP
                                candidate := candidate ||
                                    substr(alphabet, 1 + floor(random() * length(alphabet))::int, 1);
                            END LOOP;
                            EXIT WHEN NOT EXISTS (
                                SELECT 1 FROM "Organizations" WHERE "FamilyId" = candidate);
                        END LOOP;
                        UPDATE "Organizations" SET "FamilyId" = candidate WHERE "Id" = org."Id";
                    END LOOP;
                END $$;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FamilyJoinRequests");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_FamilyId",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "Organizations");
        }
    }
}
