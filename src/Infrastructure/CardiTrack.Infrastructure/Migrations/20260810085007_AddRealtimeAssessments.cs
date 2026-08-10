using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeAssessments : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Raw SQL for the same reason as the granular tables: EF cannot express
        /// <c>PARTITION BY RANGE</c>, and assessments retire by daily partition drop. The column
        /// shapes must stay in agreement with <c>RealtimeAssessmentConfiguration</c>; partitions
        /// are created ahead of need by <c>PartitionMaintenanceWorker</c>, never baked into a
        /// run-once migration.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "RealtimeAssessments" (
                    "CardiMemberId" uuid NOT NULL,
                    "WindowStartUtc" timestamp with time zone NOT NULL,
                    "WindowEndUtc" timestamp with time zone NOT NULL,
                    "HrTrendLast" double precision NOT NULL,
                    "HrDeviationScore" double precision NOT NULL,
                    "HrNoiseRms" double precision NOT NULL,
                    "StepsSum" double precision NULL,
                    "SpO2Mean" double precision NULL,
                    "ModelOutput" character varying(4000) NOT NULL,
                    "RawSeverity" character varying(50) NULL,
                    "Severity" character varying(50) NULL,
                    "GeneratedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_RealtimeAssessments"
                        PRIMARY KEY ("CardiMemberId", "WindowStartUtc")
                ) PARTITION BY RANGE ("WindowStartUtc");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RealtimeAssessments");
        }
    }
}
