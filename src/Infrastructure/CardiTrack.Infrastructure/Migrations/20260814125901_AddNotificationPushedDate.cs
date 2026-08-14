using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPushedDate : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Existing rows land with <c>PushedDate</c> null, so every already-open DEVICE_BATTERY_LOW
        /// and DEVICE_AUTH_BROKEN nudge pushes on the first dispatch tick after deploy. That is
        /// deliberate and not backfilled away: those are precisely the warnings that should have
        /// been sent and were not, the burst is bounded by how many such gaps are open at once, and
        /// stamping them as already-pushed would mean a currently-broken grant stays silent until
        /// it heals and breaks again.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PushedDate",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_PendingPush",
                table: "Notifications",
                column: "RuleCode",
                filter: "\"PushedDate\" IS NULL AND \"IsActive\" AND \"IsOwner\" AND \"State\" = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_PendingPush",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "PushedDate",
                table: "Notifications");
        }
    }
}
