using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBenignJudgements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BenignJudgements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rule = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    JudgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenignJudgements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BenignJudgements_CardiMemberId_Rule_LocalDate",
                table: "BenignJudgements",
                columns: new[] { "CardiMemberId", "Rule", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenignJudgements_JudgedAtUtc",
                table: "BenignJudgements",
                column: "JudgedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BenignJudgements");
        }
    }
}
