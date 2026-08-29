using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDaybookComparisonTolerances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DaybookBedtimeToleranceMinutes",
                table: "CardiMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DaybookDirectionBoundMinutes",
                table: "CardiMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DaybookLevelTolerancePercent",
                table: "CardiMembers",
                type: "numeric(4,1)",
                precision: 4,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DaybookWakeToleranceMinutes",
                table: "CardiMembers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DaybookBedtimeToleranceMinutes",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "DaybookDirectionBoundMinutes",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "DaybookLevelTolerancePercent",
                table: "CardiMembers");

            migrationBuilder.DropColumn(
                name: "DaybookWakeToleranceMinutes",
                table: "CardiMembers");
        }
    }
}
