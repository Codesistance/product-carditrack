using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthbookUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DigestEntries_OneMonthbookPerMonth",
                table: "DigestEntries",
                columns: new[] { "CardiMemberId", "LocalDate" },
                unique: true,
                filter: "\"Audience\" = 'Monthbook'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DigestEntries_OneMonthbookPerMonth",
                table: "DigestEntries");
        }
    }
}
