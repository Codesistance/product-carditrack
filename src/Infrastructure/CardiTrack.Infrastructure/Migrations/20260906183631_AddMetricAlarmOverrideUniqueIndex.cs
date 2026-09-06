using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricAlarmOverrideUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MetricAlarms_OneOverridePerMemberPerDefault",
                table: "MetricAlarms",
                columns: new[] { "CardiMemberId", "DerivedFromAlarmId" },
                unique: true,
                filter: "\"DerivedFromAlarmId\" IS NOT NULL AND \"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricAlarms_OneOverridePerMemberPerDefault",
                table: "MetricAlarms");
        }
    }
}
