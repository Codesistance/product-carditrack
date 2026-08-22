using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHeartRateVariability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AvgHeartRateVariabilityMs",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MadHeartRateVariability",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MedianHeartRateVariabilityMs",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StdDevHeartRateVariability",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HeartRateVariabilityMs",
                table: "DeviceActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HeartRateVariabilityMs",
                table: "ActivityLogs",
                type: "numeric(6,1)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvgHeartRateVariabilityMs",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MadHeartRateVariability",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MedianHeartRateVariabilityMs",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "StdDevHeartRateVariability",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "HeartRateVariabilityMs",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "HeartRateVariabilityMs",
                table: "ActivityLogs");
        }
    }
}
