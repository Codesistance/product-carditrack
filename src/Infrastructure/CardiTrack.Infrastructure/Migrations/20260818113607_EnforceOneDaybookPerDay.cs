using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// One daybook entry per member per day, enforced in the database rather than by the
    /// service's "already written?" probe — a fast path two overlapping pipeline executions can
    /// both pass before either writes, which would put the same day on the Daybook list twice.
    /// Partial, so the family series keeps its many-generations-per-day history; unique on a
    /// partitioned table is legal here because <c>LocalDate</c> is the partition key. The insert
    /// absorbs the violation with a bare <c>ON CONFLICT DO NOTHING</c> — see
    /// <c>DigestRepository.AddAsync</c>.
    /// </summary>
    /// <inheritdoc />
    public partial class EnforceOneDaybookPerDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DigestEntries_OneDaybookPerDay",
                table: "DigestEntries",
                columns: new[] { "CardiMemberId", "LocalDate" },
                unique: true,
                filter: "\"Audience\" = 'Daybook'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DigestEntries_OneDaybookPerDay",
                table: "DigestEntries");
        }
    }
}
