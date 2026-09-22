using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRhythmEpisodesAndDeviceRhythmCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IrnEnrolled",
                table: "DeviceConnections",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IrnOnboarded",
                table: "DeviceConnections",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IrnProfileUpdatedAt",
                table: "DeviceConnections",
                type: "timestamp with time zone",
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

            migrationBuilder.AddColumn<int>(
                name: "IrregularRhythmNotifications",
                table: "DeviceActivityLogs",
                type: "integer",
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

            migrationBuilder.AddColumn<int>(
                name: "IrregularRhythmNotifications",
                table: "ActivityLogs",
                type: "integer",
                nullable: true);

            // Raw SQL rather than the scaffolded CreateTable: EF cannot express PARTITION BY, and
            // this table is range-partitioned on WindowStartUtc so retention is a partition drop —
            // the same arrangement as RealtimeAssessments and EnvironmentalReadings.
            // PartitionMaintenanceWorker creates the children; a day without one rejects its
            // inserts, which is why that worker runs hourly and pre-creates a fortnight ahead.
            migrationBuilder.Sql("""
                CREATE TABLE "RhythmEpisodes" (
                    "CardiMemberId" uuid NOT NULL,
                    "WindowStartUtc" timestamp with time zone NOT NULL,
                    "WindowEndUtc" timestamp with time zone NOT NULL,
                    "DeviceConnectionId" uuid NOT NULL,
                    "NotificationStartUtc" timestamp with time zone NOT NULL,
                    "Positive" boolean NOT NULL,
                    "BeatCount" integer NOT NULL,
                    "RrMilliseconds" integer[] NOT NULL,
                    "OffsetMillisFromStart" integer[] NOT NULL,
                    "MeanRrMs" integer NOT NULL,
                    "MinRrMs" integer NOT NULL,
                    "MaxRrMs" integer NOT NULL,
                    "RmssdMs" integer NULL,
                    "IngestedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_RhythmEpisodes"
                        PRIMARY KEY ("CardiMemberId", "WindowStartUtc")
                ) PARTITION BY RANGE ("WindowStartUtc");
                """);

            // Declared on the parent so PostgreSQL propagates it to every child, existing and
            // future — the episode-list read is by member and notification, not by primary key.
            migrationBuilder.Sql("""
                CREATE INDEX "IX_RhythmEpisodes_CardiMemberId_NotificationStartUtc"
                ON "RhythmEpisodes" ("CardiMemberId", "NotificationStartUtc");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RhythmEpisodes");

            migrationBuilder.DropColumn(
                name: "IrnEnrolled",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "IrnOnboarded",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "IrnProfileUpdatedAt",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "EcgAtrialFibrillationReadings",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgReadings",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "IrregularRhythmNotifications",
                table: "DeviceActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgAtrialFibrillationReadings",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "EcgReadings",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "IrregularRhythmNotifications",
                table: "ActivityLogs");
        }
    }
}
