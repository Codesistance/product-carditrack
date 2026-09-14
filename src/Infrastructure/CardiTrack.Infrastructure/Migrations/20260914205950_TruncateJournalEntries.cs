using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Removes every CardiJournal entry written so far — Daybook, Weekbook and Monthbook — so
    /// the books start again under the repaired register.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data only; no schema changes. Until now a reply that used a precise term without
    /// explaining it was discarded and the half-hourly pass tried again, all day. The model
    /// names its terms whenever it says anything specific, so the reply that finally survived
    /// was the one that said least — a one-line "unremarkable week" under charts that disagreed
    /// with it. Those entries are what a caregiver reads today, and each book is written once,
    /// so nothing would ever replace them. <c>JournalRegisterGuards.Gloss</c> now writes the
    /// explanation in instead of discarding, and a Weekbook or Monthbook under three sentences is
    /// refused; the pass rewrites every period it can still read on its own schedule — a Daybook
    /// for yesterday, the Weekbook on the member's next week start, the Monthbook on the first of
    /// next month. Earlier periods are not rewritten: each book reads its own period's
    /// measurements, and only the current period is due.
    /// </para>
    /// <para>
    /// Deleted rather than marked stale: the entries are read by date and audience, and a stale
    /// flag would need every reader to learn it. Only the journal audiences go — the family
    /// digest (<c>DigestAudience.Family</c>) is the dashboard's hero card, is regenerated as data
    /// lands, and was never held to this guard. The predicate matches the audience as the table
    /// stores it, a plain string through <c>HasConversion&lt;string&gt;()</c>, so the names here
    /// are the enum's names verbatim. A DELETE on the month-partitioned parent reaches every
    /// partition.
    /// </para>
    /// </remarks>
    public partial class TruncateJournalEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "DigestEntries"
                WHERE "Audience" IN ('Daybook', 'Weekbook', 'Monthbook');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. The deleted entries are not recoverable from anything the
            // database still holds, and every period they covered is one the pass rewrites on
            // its own schedule. Throwing would make this a wall no later rollback could get past.
        }
    }
}
