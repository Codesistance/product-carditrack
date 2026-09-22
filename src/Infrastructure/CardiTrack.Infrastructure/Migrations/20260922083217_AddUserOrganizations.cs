using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Family membership becomes a relation. <c>Users.OrganizationId</c> stays but narrows to
    /// "the family this person started and pays for" — nullable, because a guest who only joined
    /// someone else's family has none — and <c>UserOrganizations</c> now says which families a
    /// person is actually in, and at what role.
    /// </summary>
    /// <remarks>
    /// The backfill gives every existing user a membership of their current organization, and
    /// makes the <strong>earliest-created user in each organization its one Admin</strong>. Role
    /// could not be copied from <c>Users.Role</c>: that column defaults to Member and no code has
    /// ever written Admin to it, so copying it would leave every existing family with no admin at
    /// all — nobody able to invite, approve or hand the family on. Earliest-created is the person
    /// who ran onboarding, which is exactly who the new rule makes admin going forward.
    /// </remarks>
    /// <inheritdoc />
    public partial class AddUserOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                table: "Users",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "UserOrganizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    JoinedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOrganizations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizations_OrganizationId",
                table: "UserOrganizations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizations_UserId",
                table: "UserOrganizations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizations_UserId_OrganizationId",
                table: "UserOrganizations",
                columns: new[] { "UserId", "OrganizationId" },
                unique: true);

            // One membership per existing user, Admin to whoever created each organization.
            // Idempotent: the unique index makes a re-run a no-op rather than a duplicate.
            migrationBuilder.Sql("""
                INSERT INTO "UserOrganizations"
                    ("Id", "UserId", "OrganizationId", "Role", "JoinedDate", "IsActive", "CreatedDate")
                SELECT
                    gen_random_uuid(),
                    u."Id",
                    u."OrganizationId",
                    CASE WHEN u."Id" = (
                        SELECT first."Id" FROM "Users" first
                        WHERE first."OrganizationId" = u."OrganizationId"
                        ORDER BY first."CreatedDate", first."Id"
                        LIMIT 1
                    ) THEN 'Admin' ELSE 'Member' END,
                    u."CreatedDate",
                    u."IsActive",
                    NOW()
                FROM "Users" u
                WHERE u."OrganizationId" IS NOT NULL
                ON CONFLICT ("UserId", "OrganizationId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserOrganizations");

            // A guest has no home organization, so reverting the column to NOT NULL would need a
            // value invented for them. Adopting the first family they joined is the only reading
            // that keeps them reachable; a guest in no family at all gets the all-zero guid, which
            // is what the column held for "unset" before it could be null.
            migrationBuilder.Sql("""
                UPDATE "Users" u
                SET "OrganizationId" = COALESCE((
                    SELECT uo."OrganizationId" FROM "UserOrganizations" uo
                    WHERE uo."UserId" = u."Id" AND uo."IsActive"
                    ORDER BY uo."JoinedDate", uo."Id"
                    LIMIT 1
                ), '00000000-0000-0000-0000-000000000000'::uuid)
                WHERE u."OrganizationId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
