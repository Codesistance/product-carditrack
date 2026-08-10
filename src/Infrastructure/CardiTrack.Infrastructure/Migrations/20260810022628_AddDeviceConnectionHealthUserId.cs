using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceConnectionHealthUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HealthUserId",
                table: "DeviceConnections",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceConnections_HealthUserId",
                table: "DeviceConnections",
                column: "HealthUserId",
                filter: "\"HealthUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceConnections_HealthUserId",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "HealthUserId",
                table: "DeviceConnections");
        }
    }
}
