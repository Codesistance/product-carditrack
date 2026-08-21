using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionnaireReminderTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRemindedAtUtc",
                table: "MemberQuestionnaires",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderCount",
                table: "MemberQuestionnaires",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MemberQuestionnaires_Status_LastRemindedAtUtc",
                table: "MemberQuestionnaires",
                columns: new[] { "Status", "LastRemindedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemberQuestionnaires_Status_LastRemindedAtUtc",
                table: "MemberQuestionnaires");

            migrationBuilder.DropColumn(
                name: "LastRemindedAtUtc",
                table: "MemberQuestionnaires");

            migrationBuilder.DropColumn(
                name: "ReminderCount",
                table: "MemberQuestionnaires");
        }
    }
}
