using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Additive, nullable, unbackfilled. Existing <c>PatternBaseline</c> rows keep mean/σ and
    /// gain null median/MAD until the next daily job; live R1 alerts still read the mean.
    /// <c>ALTER TABLE</c> on the partitioned <c>RealtimeAssessments</c> parent cascades to
    /// partitions — same stance as <c>AddSummaryHeadlineAndHistory</c>. Pre-column assessment
    /// rows have a null <c>SsaEngine</c>; new writes stamp <c>MathNet.Numerics.Evd</c>.
    /// </remarks>
    public partial class AddRobustBaselineStatsAndSsaEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SsaEngine",
                table: "RealtimeAssessments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MadHeartRate",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MadSleepMinutes",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MadSteps",
                table: "PatternBaselines",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MedianRestingHeartRate",
                table: "PatternBaselines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MedianSleepMinutes",
                table: "PatternBaselines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MedianSteps",
                table: "PatternBaselines",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SsaEngine",
                table: "RealtimeAssessments");

            migrationBuilder.DropColumn(
                name: "MadHeartRate",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MadSleepMinutes",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MadSteps",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MedianRestingHeartRate",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MedianSleepMinutes",
                table: "PatternBaselines");

            migrationBuilder.DropColumn(
                name: "MedianSteps",
                table: "PatternBaselines");
        }
    }
}
