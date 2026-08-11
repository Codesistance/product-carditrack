using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemperatureBaselineAndVariation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureBaseline",
                table: "DeviceActivityLogs",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureVariation",
                table: "DeviceActivityLogs",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureBaseline",
                table: "ActivityLogs",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureVariation",
                table: "ActivityLogs",
                type: "numeric(5,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemperatureBaseline",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "TemperatureVariation",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "TemperatureBaseline",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "TemperatureVariation",
                table: "ActivityLogs");
        }
    }
}
