using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncCadenceProfileAndPullSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveEmptyPulls",
                table: "DeviceConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextPullAt",
                table: "DeviceConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceTypeSyncProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SettleLatencyP50Hours = table.Column<double>(type: "double precision", nullable: true),
                    SettleLatencyP95Hours = table.Column<double>(type: "double precision", nullable: true),
                    RevisionTailP99Hours = table.Column<double>(type: "double precision", nullable: true),
                    PollYieldRatio = table.Column<double>(type: "double precision", nullable: true),
                    SampleSize = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RecommendedPullIntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    RecommendedLookbackDays = table.Column<int>(type: "integer", nullable: true),
                    CalculatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTypeSyncProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceConnections_NextPullAt",
                table: "DeviceConnections",
                column: "NextPullAt",
                filter: "\"NextPullAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTypeSyncProfiles_DeviceType",
                table: "DeviceTypeSyncProfiles",
                column: "DeviceType",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceTypeSyncProfiles");

            migrationBuilder.DropIndex(
                name: "IX_DeviceConnections_NextPullAt",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "ConsecutiveEmptyPulls",
                table: "DeviceConnections");

            migrationBuilder.DropColumn(
                name: "NextPullAt",
                table: "DeviceConnections");
        }
    }
}
