using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DigestSuggestionToSingleMessage : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Drop and add rather than an in-place type change: the digest section moved from three
        /// bullets to one supportive message, so the old array has nothing worth carrying forward
        /// — same "nothing is backfilled" stance as the column it replaces (see
        /// AddDigestSuggestions). The next generation for a member writes a row with the new one.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Suggestions",
                table: "DigestEntries");

            migrationBuilder.AddColumn<string>(
                name: "Suggestion",
                table: "DigestEntries",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Suggestion",
                table: "DigestEntries");

            migrationBuilder.AddColumn<List<string>>(
                name: "Suggestions",
                table: "DigestEntries",
                type: "character varying(200)[]",
                nullable: true);
        }
    }
}
