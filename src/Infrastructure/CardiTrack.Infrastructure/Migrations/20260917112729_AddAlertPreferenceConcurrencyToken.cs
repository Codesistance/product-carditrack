using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Maps Postgres's own row version (<c>xmin</c>) on <c>AlertPreferences</c> as the
    /// concurrency token: the disabled-rule list is one JSON value written whole, and it now has
    /// two writers — the settings page and a chat-confirmed change.
    /// </summary>
    /// <remarks>
    /// Deliberately no schema operation, for the reason <c>AddMetricAlarmConcurrencyToken</c>
    /// gives: the column is Postgres's, and only the model and the snapshot change.
    /// </remarks>
    public partial class AddAlertPreferenceConcurrencyToken : Migration
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
