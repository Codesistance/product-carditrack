using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// The week and month horizons of the rollup ladder (granular-storage ADR): plain views over
    /// the daily <c>ActivityLogs</c>, per the ADR's "views first; materialize only if measured
    /// slow". No EF entity maps them yet — the first consumer (trend charts, R2) will decide the
    /// query shape; until then they are the documented derivation of the two horizons.
    /// Averages ignore null days by SQL semantics, which is the pipeline's null-versus-zero rule
    /// falling out for free; <c>DaysWithData</c> is carried so a reader can judge coverage.
    /// </remarks>
    public partial class AddActivityLogHorizonViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE VIEW "ActivityLogsWeekly" AS
                SELECT "CardiMemberId",
                       (date_trunc('week', "Date"::timestamp))::date AS "WeekStart",
                       COUNT(*)                                      AS "DaysWithData",
                       AVG("Steps")                                  AS "AvgSteps",
                       AVG("RestingHeartRate")                       AS "AvgRestingHeartRate",
                       MIN("RestingHeartRate")                       AS "MinRestingHeartRate",
                       MAX("RestingHeartRate")                       AS "MaxRestingHeartRate",
                       AVG("SleepMinutes")                           AS "AvgSleepMinutes",
                       AVG("SleepEfficiency")                        AS "AvgSleepEfficiency",
                       AVG("ActiveMinutes")                          AS "AvgActiveMinutes"
                FROM "ActivityLogs"
                GROUP BY "CardiMemberId", date_trunc('week', "Date"::timestamp);
                """);

            migrationBuilder.Sql("""
                CREATE VIEW "ActivityLogsMonthly" AS
                SELECT "CardiMemberId",
                       (date_trunc('month', "Date"::timestamp))::date AS "MonthStart",
                       COUNT(*)                                       AS "DaysWithData",
                       AVG("Steps")                                   AS "AvgSteps",
                       AVG("RestingHeartRate")                        AS "AvgRestingHeartRate",
                       MIN("RestingHeartRate")                        AS "MinRestingHeartRate",
                       MAX("RestingHeartRate")                        AS "MaxRestingHeartRate",
                       AVG("SleepMinutes")                            AS "AvgSleepMinutes",
                       AVG("SleepEfficiency")                         AS "AvgSleepEfficiency",
                       AVG("ActiveMinutes")                           AS "AvgActiveMinutes"
                FROM "ActivityLogs"
                GROUP BY "CardiMemberId", date_trunc('month', "Date"::timestamp);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP VIEW IF EXISTS "ActivityLogsWeekly";""");
            migrationBuilder.Sql("""DROP VIEW IF EXISTS "ActivityLogsMonthly";""");
        }
    }
}
