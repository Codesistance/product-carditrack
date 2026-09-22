using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// The table behind an admin's invitation to another adult to help watch one CardiMember.
    /// </summary>
    /// <remarks>
    /// Only the SHA-256 of each token is stored, and the unique index on it is what makes
    /// redemption a single indexed read rather than a scan — nothing whose duration could vary
    /// with how close a guess was. No backfill: nobody has been invited yet.
    /// </remarks>
    /// <inheritdoc />
    public partial class AddCaregiverInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaregiverInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CanViewHealthData = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ReceiveAlerts = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaregiverInvites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaregiverInvites_CardiMemberId",
                table: "CaregiverInvites",
                column: "CardiMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_CaregiverInvites_OrganizationId",
                table: "CaregiverInvites",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CaregiverInvites_TokenHash",
                table: "CaregiverInvites",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaregiverInvites");
        }
    }
}
