using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestEntries : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Raw SQL for the same reason as the granular tables: EF cannot express
        /// <c>PARTITION BY RANGE</c>, and digests retire by monthly partition drop. The column
        /// shapes must stay in agreement with <c>DigestEntryConfiguration</c>; partitions are
        /// created ahead of need by <c>PartitionMaintenanceWorker</c>, never baked into a
        /// run-once migration.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "DigestEntries" (
                    "CardiMemberId" uuid NOT NULL,
                    "LocalDate" date NOT NULL,
                    "Audience" character varying(50) NOT NULL,
                    "Text" character varying(4000) NOT NULL,
                    "GeneratedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_DigestEntries"
                        PRIMARY KEY ("CardiMemberId", "LocalDate", "Audience")
                ) PARTITION BY RANGE ("LocalDate");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DigestEntries");
        }
    }
}
