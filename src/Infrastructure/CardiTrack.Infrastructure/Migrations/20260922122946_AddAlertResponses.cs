using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedByUserId",
                table: "Alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResponseCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertResponses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertResponses_AlertId_CreatedDate",
                table: "AlertResponses",
                columns: new[] { "AlertId", "CreatedDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertResponses");

            migrationBuilder.DropColumn(
                name: "ResolvedByUserId",
                table: "Alerts");
        }
    }
}
