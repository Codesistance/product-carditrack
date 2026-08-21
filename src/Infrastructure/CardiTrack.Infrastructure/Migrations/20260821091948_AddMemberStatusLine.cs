using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberStatusLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberStatusLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Headline = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberStatusLines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberStatusLines_CardiMemberId",
                table: "MemberStatusLines",
                column: "CardiMemberId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberStatusLines");
        }
    }
}
