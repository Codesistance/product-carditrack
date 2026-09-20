using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberInsights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecommendedAction = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    KeyFindings = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsLearning = table.Column<bool>(type: "boolean", nullable: false),
                    IsProvisional = table.Column<bool>(type: "boolean", nullable: false),
                    BaselinePeriodDays = table.Column<int>(type: "integer", nullable: true),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberInsights", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberInsights_AlertId",
                table: "MemberInsights",
                column: "AlertId",
                unique: true,
                filter: "\"AlertId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MemberInsights_CardiMemberId_Scope",
                table: "MemberInsights",
                columns: new[] { "CardiMemberId", "Scope" },
                unique: true,
                filter: "\"AlertId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MemberInsights_GeneratedAtUtc",
                table: "MemberInsights",
                column: "GeneratedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberInsights");
        }
    }
}
