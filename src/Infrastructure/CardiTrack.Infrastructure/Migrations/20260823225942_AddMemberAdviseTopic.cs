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

            // The world this rolls back to holds one row per member, and by the time anyone rolls
            // back, topic-scoped generation may have written several. Collapse to the row the
            // pre-topic readers would have served — AdvisePicker.PickDefault's order: the General
            // row when there is one, else the most recently generated — or recreating the unique
            // index below fails on the duplicates and the rollback is impossible. Losing the other
            // topics' rows is the honest cost of undoing the split; they have no column to live in
            // on the other side.
            migrationBuilder.Sql("""
                DELETE FROM "MemberAdvises"
                WHERE "Id" NOT IN (
                    SELECT DISTINCT ON ("CardiMemberId") "Id"
                    FROM "MemberAdvises"
                    ORDER BY "CardiMemberId",
                             CASE WHEN "Topic" = 'General' THEN 0 ELSE 1 END,
                             "GeneratedAtUtc" DESC,
                             "Id"
                );
                """);

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
