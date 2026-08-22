using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceRhythmHrvWeightGlucose : Migration
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
                name: "AvgWeightKg",
                table: "PatternBaselines",
                type: "numeric(6,2)",
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
                name: "BloodGlucoseAverage",
                table: "DeviceActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BloodGlucoseMax",
                table: "DeviceActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BloodGlucoseMin",
                table: "DeviceActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EcgAtrialFibrillationReadings",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EcgReadings",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HeartRateVariabilityMs",
                table: "DeviceActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IrregularRhythmNotifications",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightKg",
                table: "DeviceActivityLogs",
                type: "numeric(6,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BloodGlucoseAverage",
                table: "ActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BloodGlucoseMax",
                table: "ActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BloodGlucoseMin",
                table: "ActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EcgAtrialFibrillationReadings",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EcgReadings",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HeartRateVariabilityMs",
                table: "ActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IrregularRhythmNotifications",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightKg",
                table: "ActivityLogs",
                type: "numeric(6,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvgHeartRateVariabilityMs",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "AvgWeightKg",
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
                name: "BloodGlucoseAverage",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "BloodGlucoseMax",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "BloodGlucoseMin",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgAtrialFibrillationReadings",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgReadings",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "HeartRateVariabilityMs",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "IrregularRhythmNotifications",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "WeightKg",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "BloodGlucoseAverage",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "BloodGlucoseMax",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "BloodGlucoseMin",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgAtrialFibrillationReadings",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgReadings",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "HeartRateVariabilityMs",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "IrregularRhythmNotifications",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "WeightKg",
                table: "ActivityLogs");
        }
    }
}
