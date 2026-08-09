using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReduceDefaultSyncFrequencyToTenMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SyncFrequencyMinutes",
                table: "DeviceConnections",
                type: "integer",
                nullable: false,
                defaultValue: 10,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 30);

            // The column default only reaches rows inserted from here on, and every existing
            // connection was written under the old one — so without this the change would apply
            // to no member anyone is currently monitoring. Guarded on the old default rather than
            // updating unconditionally: nothing writes a per-connection cadence today, but cadence
            // calibration is designed to, and this must not stamp over a considered value.
            migrationBuilder.Sql(
                @"UPDATE ""DeviceConnections"" SET ""SyncFrequencyMinutes"" = 10
                  WHERE ""SyncFrequencyMinutes"" = 30;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SyncFrequencyMinutes",
                table: "DeviceConnections",
                type: "integer",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 10);

            migrationBuilder.Sql(
                @"UPDATE ""DeviceConnections"" SET ""SyncFrequencyMinutes"" = 30
                  WHERE ""SyncFrequencyMinutes"" = 10;");
        }
    }
}
