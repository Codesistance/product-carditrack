using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentalWeatherAndMemberQuestionnaires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RelativeHumidityPercent",
                table: "EnvironmentalReadings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WeatherCondition",
                table: "EnvironmentalReadings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemberQuestionnaires",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionText = table.Column<string>(type: "text", nullable: false),
                    AnswerText = table.Column<string>(type: "text", nullable: true),
                    TriggerContext = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberQuestionnaires", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberQuestionnaires_CardiMemberId_GeneratedAtUtc",
                table: "MemberQuestionnaires",
                columns: new[] { "CardiMemberId", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MemberQuestionnaires_CardiMemberId_Status",
                table: "MemberQuestionnaires",
                columns: new[] { "CardiMemberId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberQuestionnaires");

            migrationBuilder.DropColumn(
                name: "RelativeHumidityPercent",
                table: "EnvironmentalReadings");

            migrationBuilder.DropColumn(
                name: "WeatherCondition",
                table: "EnvironmentalReadings");
        }
    }
}
