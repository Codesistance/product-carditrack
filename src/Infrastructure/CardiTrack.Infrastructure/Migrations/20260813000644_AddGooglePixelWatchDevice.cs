using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGooglePixelWatchDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Devices",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0001-0000-0000-000000000001"),
                column: "ApiEndpoint",
                value: "https://health.googleapis.com");

            migrationBuilder.UpdateData(
                table: "Devices",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0004-0000-0000-000000000004"),
                column: "DeviceType",
                value: "GalaxyWatch");

            migrationBuilder.InsertData(
                table: "Devices",
                columns: new[] { "Id", "ApiEndpoint", "Capabilities", "CreatedDate", "DeviceType", "DisplayName", "IconUrl", "IsActive", "Manufacturer", "ModelName", "OAuthConfig", "SortOrder", "UpdatedDate" },
                values: new object[] { new Guid("a1b2c3d4-0008-0000-0000-000000000008"), "https://health.googleapis.com", "{\"hasHeartRate\":true,\"hasSleep\":true,\"hasActivity\":true,\"hasSpO2\":true,\"hasECG\":true}", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "GooglePixelWatch", "Google Pixel Watch", null, true, "Google", "Pixel Watch", null, 8, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Devices",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0008-0000-0000-000000000008"));

            migrationBuilder.UpdateData(
                table: "Devices",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0001-0000-0000-000000000001"),
                column: "ApiEndpoint",
                value: "https://api.fitbit.com");

            migrationBuilder.UpdateData(
                table: "Devices",
                keyColumn: "Id",
                keyValue: new Guid("a1b2c3d4-0004-0000-0000-000000000004"),
                column: "DeviceType",
                value: "Samsung");
        }
    }
}
