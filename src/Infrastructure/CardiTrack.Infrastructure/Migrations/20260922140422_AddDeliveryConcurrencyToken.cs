using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Maps Postgres's own row version (<c>xmin</c>) on <c>NotificationDeliveries</c> as the
    /// concurrency token, so an UPDATE is predicated on the row being the one that was read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately no schema operation — the same shape as <c>AddMetricAlarmConcurrencyToken</c>
    /// and <c>AddAlertPreferenceConcurrencyToken</c>. <c>xmin</c> is a system column Postgres keeps
    /// on every row; mapping it changes the model and the snapshot, not the table. EF's scaffolder
    /// emits an <c>AddColumn</c> for it, which would fail against a real database ("column name
    /// xmin conflicts with a system column name"), so the generated body is replaced by this note.
    /// </para>
    /// <para>
    /// Why this table needs one: two writers reach a delivery row by different doors — the API when
    /// a caregiver answers the alert in the app, and the Worker's escalation sweep, which loads a
    /// batch of <c>Sent</c> rows and saves them a moment later. Last write wins otherwise, and the
    /// sweep's stale entity is usually the later one, so an answered alert could be put back to
    /// <c>Sent</c> and go on paging a family about something already dealt with.
    /// </para>
    /// </remarks>
    public partial class AddDeliveryConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing to do: see the class remarks.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the column is Postgres's, not ours.
        }
    }
}
