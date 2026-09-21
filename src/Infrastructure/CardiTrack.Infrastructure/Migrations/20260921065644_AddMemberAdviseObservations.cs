using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberAdviseObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberAdviseObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Suggestion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    GuidelineCited = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberAdviseObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberAdviseObservations_CardiMemberId_ObservedAtUtc",
                table: "MemberAdviseObservations",
                columns: new[] { "CardiMemberId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MemberAdviseObservations_ObservedAtUtc",
                table: "MemberAdviseObservations",
                column: "ObservedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberAdviseObservations");
        }
    }
}
