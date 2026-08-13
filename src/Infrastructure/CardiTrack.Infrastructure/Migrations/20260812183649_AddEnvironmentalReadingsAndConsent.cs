using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// The table is raw SQL for the same reason as <c>AddRealtimeAssessments</c>: EF cannot
    /// express <c>PARTITION BY RANGE</c>, and readings retire by daily partition drop. The
    /// column shapes must stay in agreement with <c>EnvironmentalReadingConfiguration</c>;
    /// partitions are created ahead of need by <c>PartitionMaintenanceWorker</c>, never baked
    /// into a run-once migration.
    /// </remarks>
    public partial class AddEnvironmentalReadingsAndConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnvironmentalContextConsentGranted",
                table: "CardiMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                CREATE TABLE "EnvironmentalReadings" (
                    "CardiMemberId" uuid NOT NULL,
                    "SessionStartUtc" timestamp with time zone NOT NULL,
                    "SessionEndUtc" timestamp with time zone NOT NULL,
                    "DeviceConnectionId" uuid NOT NULL,
                    "TemperatureCelsius" double precision NULL,
                    "AirQualityIndex" integer NULL,
                    "AirQualityCategory" character varying(50) NULL,
                    "GeneratedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_EnvironmentalReadings"
                        PRIMARY KEY ("CardiMemberId", "SessionStartUtc")
                ) PARTITION BY RANGE ("SessionStartUtc");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnvironmentalReadings");

            migrationBuilder.DropColumn(
                name: "EnvironmentalContextConsentGranted",
                table: "CardiMembers");
        }
    }
}
