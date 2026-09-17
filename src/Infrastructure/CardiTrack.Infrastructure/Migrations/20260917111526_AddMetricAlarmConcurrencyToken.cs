using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Maps Postgres's own row version (<c>xmin</c>) on <c>MetricAlarms</c> as the concurrency
    /// token, so an UPDATE is predicated on the row being the one that was read.
    /// </summary>
    /// <remarks>
    /// Deliberately no schema operation. <c>xmin</c> is a system column Postgres keeps on every
    /// row; mapping it changes the model and the snapshot, not the table. Npgsql's SQL generator
    /// already emits nothing for it, and this migration says so rather than leaving an AddColumn
    /// a reader would expect to fail.
    /// </remarks>
    public partial class AddMetricAlarmConcurrencyToken : Migration
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
