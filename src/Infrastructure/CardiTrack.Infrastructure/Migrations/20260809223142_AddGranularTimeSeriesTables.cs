using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGranularTimeSeriesTables : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Raw SQL, not <c>CreateTable</c>: EF cannot express <c>PARTITION BY RANGE</c>, and these
        /// two tables are range-partitioned so retention can be a partition drop instead of a bulk
        /// delete. The column shapes below must stay in agreement with
        /// <c>GranularMetricHourConfiguration</c> / <c>MetricRollupHourlyConfiguration</c> — the
        /// model snapshot records the tables as ordinary, which is exactly how EF queries them.
        /// No partitions are created here (a migration runs once, so any date range baked into it
        /// would be stale on arrival) — <c>PartitionMaintenanceWorker</c> creates them ahead of
        /// need, hourly, and nothing writes these tables until ingestion is wired up.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "GranularMetricHours" (
                    "DeviceConnectionId" uuid NOT NULL,
                    "Metric" character varying(50) NOT NULL,
                    "HourStartUtc" timestamp with time zone NOT NULL,
                    "CardiMemberId" uuid NOT NULL,
                    "Values" real[] NOT NULL,
                    "SampleCount" smallint NOT NULL,
                    CONSTRAINT "PK_GranularMetricHours"
                        PRIMARY KEY ("DeviceConnectionId", "Metric", "HourStartUtc")
                ) PARTITION BY RANGE ("HourStartUtc");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX "IX_GranularMetricHours_CardiMemberId_HourStartUtc"
                    ON "GranularMetricHours" ("CardiMemberId", "HourStartUtc");
                """);

            migrationBuilder.Sql("""
                CREATE TABLE "MetricRollupsHourly" (
                    "CardiMemberId" uuid NOT NULL,
                    "Metric" character varying(50) NOT NULL,
                    "HourStartUtc" timestamp with time zone NOT NULL,
                    "Min" real NOT NULL,
                    "Max" real NOT NULL,
                    "Avg" real NOT NULL,
                    "Sum" real NOT NULL,
                    "SampleCount" smallint NOT NULL,
                    CONSTRAINT "PK_MetricRollupsHourly"
                        PRIMARY KEY ("CardiMemberId", "Metric", "HourStartUtc")
                ) PARTITION BY RANGE ("HourStartUtc");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GranularMetricHours");

            migrationBuilder.DropTable(
                name: "MetricRollupsHourly");
        }
    }
}
