using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceBatteryLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BatteryLevel",
                table: "DeviceConnections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BatteryStatus",
                table: "DeviceConnections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BatteryUpdatedAt",
                table: "DeviceConnections",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatteryLevel",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "BatteryStatus",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "BatteryUpdatedAt",
                table: "DeviceConnections");
        }
    }
}
