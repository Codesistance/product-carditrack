using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestSuggestions : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Ordinary DDL, like AddSummaryHeadlineAndHistory before it: <c>ALTER TABLE</c> on a
        /// partitioned parent cascades to its partitions, so nothing here needs to know the
        /// partitioning scheme. A <c>varchar(200)[]</c> rather than jsonb — this is a short
        /// ordered list of plain strings with no shape of its own.
        /// <para>
        /// Nullable, and existing rows keep their text and gain a null: a summary written before
        /// suggestions existed has none, and the apps hide the section rather than rendering an
        /// empty one. Nothing is backfilled — summaries are derived data, and the next generation
        /// for a member writes a row that has them.
        /// </para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Suggestions",
                table: "DigestEntries",
                type: "character varying(200)[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Suggestions",
                table: "DigestEntries");
        }
    }
}
