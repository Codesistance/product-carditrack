using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberAiHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberAiHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HeldUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberAiHolds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberAiHolds_CardiMemberId_Purpose",
                table: "MemberAiHolds",
                columns: new[] { "CardiMemberId", "Purpose" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberAiHolds");
        }
    }
}
