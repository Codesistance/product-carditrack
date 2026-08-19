using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWeekbookUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DigestEntries_OneWeekbookPerWeek",
                table: "DigestEntries",
                columns: new[] { "CardiMemberId", "LocalDate" },
                unique: true,
                filter: "\"Audience\" = 'Weekbook'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DigestEntries_OneWeekbookPerWeek",
                table: "DigestEntries");
        }
    }
}
