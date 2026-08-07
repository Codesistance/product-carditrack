using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCardiMemberMonitoringPauseAndAlertSensitivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "MedicalNotes",
                table: "CardiMembers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlertSensitivity",
                table: "CardiMembers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "MonitoringPauseReason",
                table: "CardiMembers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MonitoringPausedUntil",
                table: "CardiMembers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CardiMembers_MonitoringPausedUntil",
                table: "CardiMembers",
                column: "MonitoringPausedUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CardiMembers_MonitoringPausedUntil",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "AlertSensitivity",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "MonitoringPauseReason",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "MonitoringPausedUntil",
                table: "CardiMembers");

            migrationBuilder.AlterColumn<string>(
                name: "MedicalNotes",
                table: "CardiMembers",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
