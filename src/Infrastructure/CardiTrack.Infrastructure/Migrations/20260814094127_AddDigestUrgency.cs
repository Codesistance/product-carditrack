using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Nullable and unbackfilled, same stance as every other derived field on this table: existing
    /// rows simply have no urgency read, and the apps already treat a missing value as "nothing to
    /// show" rather than a tier of its own.
    /// </remarks>
    public partial class AddDigestUrgency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Urgency",
                table: "DigestEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Urgency",
                table: "DigestEntries");
        }
    }
}
