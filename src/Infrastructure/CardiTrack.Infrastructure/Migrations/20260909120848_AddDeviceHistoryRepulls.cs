using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceHistoryRepulls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceHistoryRepulls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedTo = table.Column<DateOnly>(type: "date", nullable: true),
                    DaysWithData = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHistoryRepulls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHistoryRepulls_CardiMemberId",
                table: "DeviceHistoryRepulls",
                column: "CardiMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHistoryRepulls_DeviceConnectionId_RequestedAt",
                table: "DeviceHistoryRepulls",
                columns: new[] { "DeviceConnectionId", "RequestedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHistoryRepulls_Due",
                table: "DeviceHistoryRepulls",
                column: "RequestedAt",
                filter: "\"Status\" IN ('Pending', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHistoryRepulls_OneOpenPerConnection",
                table: "DeviceHistoryRepulls",
                column: "DeviceConnectionId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'InProgress')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceHistoryRepulls");
        }
    }
}
