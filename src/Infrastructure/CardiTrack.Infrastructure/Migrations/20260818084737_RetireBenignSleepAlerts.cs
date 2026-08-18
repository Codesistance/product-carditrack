using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Closes the alerts raised by the retired benign branch of <c>irregular_sleep</c> — a longer
    /// night than usual that never overshot the recommended band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data only — there is no schema change to make. <c>StatisticalAlertRules.IrregularSleep</c>
    /// now returns null for that shape instead of grading it <c>Green</c>, so no further rows can
    /// be written; these are the ones already raised. Left alone they would stand for good: this
    /// engine deliberately never auto-resolves (see <c>StatisticalAlertService</c>), so every one
    /// of them would sit in the dashboard's Recent Alerts strip and count against the unread badge
    /// forever, about a night that ended months ago. That is the complaint the retirement answers,
    /// and retiring the producer without closing its output would only stop it getting worse.
    /// </para>
    /// <para>
    /// Resolved rather than deleted, and resolved rather than acknowledged. Resolution is the
    /// truthful statement — the condition has passed — and it is what the cooldown design means by
    /// an episode being over. Acknowledging would instead record that a caregiver looked at it,
    /// which none of them did, and deleting would discard a record that a daybook entry of that same
    /// night may well refer back to.
    /// </para>
    /// <para>
    /// The predicate matches the substring the code itself matches on (<c>AlertRuleMarkers.HasRule</c>),
    /// so the two cannot drift: <c>Severity</c> and <c>MetricValues</c> are both plain
    /// <c>character varying</c> here, the former through <c>HasConversion&lt;string&gt;()</c> and
    /// the latter holding the producer's JSON verbatim. Severity is part of the predicate because
    /// <c>Green</c> is exactly what the benign branch produced — a <c>Yellow</c> irregular_sleep
    /// alert is a short night or an overshoot, both of which the rule still raises and neither of
    /// which this may touch.
    /// </para>
    /// </remarks>
    public partial class RetireBenignSleepAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "Alerts"
                SET "IsResolved" = TRUE,
                    "UpdatedDate" = NOW()
                WHERE "IsResolved" = FALSE
                  AND "Severity" = 'Green'
                  AND "MetricValues" LIKE '%"rule":"irregular_sleep"%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Which alerts this closed is not recoverable — the rows carry no
            // record of having been resolved by a migration rather than by a producer, and
            // reopening every Green irregular_sleep alert would also reopen any that were already
            // resolved before this ran. Throwing instead would make this a wall no later schema
            // rollback could get past, to restore a state the producer can no longer create.
        }
    }
}
