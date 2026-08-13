using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceAuthRecoveryBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthRecoveryAttempts",
                table: "DeviceConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAuthRecoveryAt",
                table: "DeviceConnections",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthRecoveryAttempts",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "NextAuthRecoveryAt",
                table: "DeviceConnections");
        }
    }
}
