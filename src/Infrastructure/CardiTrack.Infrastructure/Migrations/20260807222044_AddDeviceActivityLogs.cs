using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceActivityLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSource = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Steps = table.Column<int>(type: "integer", nullable: true),
                    Distance = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    ActiveMinutes = table.Column<int>(type: "integer", nullable: true),
                    SedentaryMinutes = table.Column<int>(type: "integer", nullable: true),
                    Floors = table.Column<int>(type: "integer", nullable: true),
                    CaloriesBurned = table.Column<int>(type: "integer", nullable: true),
                    RestingHeartRate = table.Column<int>(type: "integer", nullable: true),
                    AvgHeartRate = table.Column<int>(type: "integer", nullable: true),
                    MaxHeartRate = table.Column<int>(type: "integer", nullable: true),
                    MinHeartRate = table.Column<int>(type: "integer", nullable: true),
                    SleepMinutes = table.Column<int>(type: "integer", nullable: true),
                    SleepStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SleepEndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SleepEfficiency = table.Column<int>(type: "integer", nullable: true),
                    DeepSleepMinutes = table.Column<int>(type: "integer", nullable: true),
                    LightSleepMinutes = table.Column<int>(type: "integer", nullable: true),
                    RemSleepMinutes = table.Column<int>(type: "integer", nullable: true),
                    AwakeMinutes = table.Column<int>(type: "integer", nullable: true),
                    SpO2Average = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    SpO2Min = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    SpO2Max = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    VO2Max = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    StressScore = table.Column<int>(type: "integer", nullable: true),
                    BreathingRate = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    Temperature = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceActivityLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceActivityLogs_CardiMemberId_Date",
                table: "DeviceActivityLogs",
                columns: new[] { "CardiMemberId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceActivityLogs_DeviceConnectionId_Date",
                table: "DeviceActivityLogs",
                columns: new[] { "DeviceConnectionId", "Date" },
                unique: true);

            // Backfill the raw table from the rows already in ActivityLogs. Until now every
            // ActivityLog row was written by exactly one device and carries its
            // DeviceConnectionId, so each becomes that device's raw row one-for-one and the
            // existing derived rows stay valid. Without this the first post-deploy sync would
            // merge from an empty raw set and lose the history for any day it does not re-fetch.
            migrationBuilder.Sql("""
                INSERT INTO "DeviceActivityLogs" (
                    "Id", "CardiMemberId", "DeviceConnectionId", "DataSource", "Date",
                    "Steps", "Distance", "ActiveMinutes", "SedentaryMinutes", "Floors", "CaloriesBurned",
                    "RestingHeartRate", "AvgHeartRate", "MaxHeartRate", "MinHeartRate",
                    "SleepMinutes", "SleepStartTime", "SleepEndTime", "SleepEfficiency",
                    "DeepSleepMinutes", "LightSleepMinutes", "RemSleepMinutes", "AwakeMinutes",
                    "SpO2Average", "SpO2Min", "SpO2Max", "VO2Max", "StressScore",
                    "BreathingRate", "Temperature", "CreatedDate", "UpdatedDate")
                SELECT
                    gen_random_uuid(), "CardiMemberId", "DeviceConnectionId", "DataSource", "Date",
                    "Steps", "Distance", "ActiveMinutes", "SedentaryMinutes", "Floors", "CaloriesBurned",
                    "RestingHeartRate", "AvgHeartRate", "MaxHeartRate", "MinHeartRate",
                    "SleepMinutes", "SleepStartTime", "SleepEndTime", "SleepEfficiency",
                    "DeepSleepMinutes", "LightSleepMinutes", "RemSleepMinutes", "AwakeMinutes",
                    "SpO2Average", "SpO2Min", "SpO2Max", "VO2Max", "StressScore",
                    "BreathingRate", "Temperature", "CreatedDate", "UpdatedDate"
                FROM "ActivityLogs"
                ON CONFLICT ("DeviceConnectionId", "Date") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceActivityLogs");
        }
    }
}
