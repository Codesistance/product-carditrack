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

            // Restores the pre-migration state on the assumption Up() created it — which is
            // exactly true today, because nothing writes a per-connection cadence. It cannot tell
            // a row Up() moved from one deliberately set to 10 afterwards, and no rollback could
            // without recording the prior value; a table to hold that is not worth carrying for a
            // column no feature writes yet. Revisit alongside cadence calibration, which is what
            // will start writing this column.
            migrationBuilder.Sql(
                @"UPDATE ""DeviceConnections"" SET ""SyncFrequencyMinutes"" = 30
                  WHERE ""SyncFrequencyMinutes"" = 10;");
        }
    }
}
