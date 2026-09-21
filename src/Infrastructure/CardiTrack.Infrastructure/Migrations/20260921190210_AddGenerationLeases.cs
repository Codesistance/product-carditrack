using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerationLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenerationLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Work = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    HeldUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationLeases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationLeases_CardiMemberId_Work",
                table: "GenerationLeases",
                columns: new[] { "CardiMemberId", "Work" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationLeases");
        }
    }
}
