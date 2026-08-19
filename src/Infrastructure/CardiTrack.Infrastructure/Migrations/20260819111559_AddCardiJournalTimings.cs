using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCardiJournalTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "DaybookLocalTime",
                table: "CardiMembers",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JournalWeekStartsOn",
                table: "CardiMembers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "MonthbookLocalTime",
                table: "CardiMembers",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WeekbookLocalTime",
                table: "CardiMembers",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DaybookLocalTime",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "JournalWeekStartsOn",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "MonthbookLocalTime",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "WeekbookLocalTime",
                table: "CardiMembers");
        }
    }
}
