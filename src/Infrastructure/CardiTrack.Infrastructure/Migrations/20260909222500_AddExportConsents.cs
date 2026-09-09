using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExportConsents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExportConsents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    DateRangeFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DateRangeTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IncludeMetrics = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeTrends = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeAlerts = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeJournals = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeNotices = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeDevices = table.Column<bool>(type: "boolean", nullable: false),
                    JournalEntryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    JournalAudience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PolicySha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportConsents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportConsents_ExpiresAt",
                table: "ExportConsents",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExportConsents_OwnerUserId_Id",
                table: "ExportConsents",
                columns: new[] { "OwnerUserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportConsents");
        }
    }
}
