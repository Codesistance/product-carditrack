using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberAdviseTopic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemberAdvises_CardiMemberId",
                table: "MemberAdvises");

            // "General", not "": every pre-topic row was generated with no topic constraint, so
            // General is what it honestly is — and an empty string would fail enum materialization
            // on the first read of any existing row.
            migrationBuilder.AddColumn<string>(
                name: "Topic",
                table: "MemberAdvises",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "General");

            migrationBuilder.CreateIndex(
                name: "IX_MemberAdvises_CardiMemberId_Topic",
                table: "MemberAdvises",
                columns: new[] { "CardiMemberId", "Topic" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemberAdvises_CardiMemberId_Topic",
                table: "MemberAdvises");

            migrationBuilder.DropColumn(
                name: "Topic",
                table: "MemberAdvises");

            migrationBuilder.CreateIndex(
                name: "IX_MemberAdvises_CardiMemberId",
                table: "MemberAdvises",
                column: "CardiMemberId",
                unique: true);
        }
    }
}
