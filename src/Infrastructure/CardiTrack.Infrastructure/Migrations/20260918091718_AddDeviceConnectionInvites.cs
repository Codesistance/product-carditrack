using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceConnectionInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceConnectionInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeviceConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceConnectionInvites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceConnectionInvites_ExpiresAt",
                table: "DeviceConnectionInvites",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceConnectionInvites_OneLivePerMemberDevice",
                table: "DeviceConnectionInvites",
                columns: new[] { "CardiMemberId", "DeviceType" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Opened')");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceConnectionInvites_TokenHash",
                table: "DeviceConnectionInvites",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceConnectionInvites");
        }
    }
}
