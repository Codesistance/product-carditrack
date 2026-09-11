using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExportConsentRememberFor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RememberFor",
                table: "ExportConsents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "ThisExport");

            migrationBuilder.AddColumn<DateTime>(
                name: "RememberUntil",
                table: "ExportConsents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReusedFromConsentId",
                table: "ExportConsents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAt",
                table: "ExportConsents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExportConsents_OwnerUserId_RememberUntil",
                table: "ExportConsents",
                columns: new[] { "OwnerUserId", "RememberUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExportConsents_OwnerUserId_RememberUntil",
                table: "ExportConsents");

            migrationBuilder.DropColumn(
                name: "RememberFor",
                table: "ExportConsents");

            migrationBuilder.DropColumn(
                name: "RememberUntil",
                table: "ExportConsents");

            migrationBuilder.DropColumn(
                name: "ReusedFromConsentId",
                table: "ExportConsents");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "ExportConsents");
        }
    }
}
