using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOvernightVitalsExertionAndRest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AvgElevatedZoneMinutes",
                table: "PatternBaselines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AvgHeartRateVariabilityMs",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AvgLongestSedentaryStretchMinutes",
                table: "PatternBaselines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AvgOvernightBreathingRate",
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
                name: "StdDevOvernightBreathingRate",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HeartRateVariabilityMs",
                table: "DeviceActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LightZoneMinutes",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LongestSedentaryStretchMinutes",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LongestSedentaryStretchStartUtc",
                table: "DeviceActivityLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModerateZoneFloorBpm",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModerateZoneMinutes",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OvernightBreathingRate",
                table: "DeviceActivityLogs",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeakZoneMinutes",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VigorousZoneMinutes",
                table: "DeviceActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HeartRateVariabilityMs",
                table: "ActivityLogs",
                type: "numeric(6,1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LightZoneMinutes",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LongestSedentaryStretchMinutes",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LongestSedentaryStretchStartUtc",
                table: "ActivityLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModerateZoneFloorBpm",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModerateZoneMinutes",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OvernightBreathingRate",
                table: "ActivityLogs",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeakZoneMinutes",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VigorousZoneMinutes",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvgElevatedZoneMinutes",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "AvgHeartRateVariabilityMs",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "AvgLongestSedentaryStretchMinutes",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "AvgOvernightBreathingRate",
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
                name: "StdDevOvernightBreathingRate",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "HeartRateVariabilityMs",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "LightZoneMinutes",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "LongestSedentaryStretchMinutes",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "LongestSedentaryStretchStartUtc",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "ModerateZoneFloorBpm",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "ModerateZoneMinutes",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "OvernightBreathingRate",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "PeakZoneMinutes",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "VigorousZoneMinutes",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "HeartRateVariabilityMs",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "LightZoneMinutes",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "LongestSedentaryStretchMinutes",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "LongestSedentaryStretchStartUtc",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "ModerateZoneFloorBpm",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "ModerateZoneMinutes",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "OvernightBreathingRate",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "PeakZoneMinutes",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "VigorousZoneMinutes",
                table: "ActivityLogs");
        }
    }
}
