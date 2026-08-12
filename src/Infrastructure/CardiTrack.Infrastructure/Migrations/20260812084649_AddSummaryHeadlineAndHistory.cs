using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaryHeadlineAndHistory : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Ordinary DDL rather than the raw SQL that created the table: <c>ALTER TABLE</c> on a
        /// partitioned parent cascades to its partitions, so nothing here needs to know the
        /// partitioning scheme. The key gains <c>GeneratedAtUtc</c> because summaries are now
        /// recomputed as a member's data moves and every generation is kept — the day is a
        /// history, not a cell. It still carries <c>LocalDate</c>, which Postgres requires of a
        /// primary key on a range-partitioned table.
        /// <para>
        /// Existing rows keep their text and gain a null <c>Headline</c>; the apps fall back to
        /// their own label for those. <c>Down</c> restores the one-row-per-day key, which will be
        /// refused if any day has by then accumulated more than one generation — the data loss
        /// that would otherwise be silent, surfaced as a failed rollback.
        /// </para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DigestEntries",
                table: "DigestEntries");

            migrationBuilder.AddColumn<string>(
                name: "Headline",
                table: "DigestEntries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DigestEntries",
                table: "DigestEntries",
                columns: new[] { "CardiMemberId", "LocalDate", "Audience", "GeneratedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DigestEntries",
                table: "DigestEntries");

            migrationBuilder.DropColumn(
                name: "Headline",
                table: "DigestEntries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DigestEntries",
                table: "DigestEntries",
                columns: new[] { "CardiMemberId", "LocalDate", "Audience" });
        }
    }
}
