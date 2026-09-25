using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Splits the single CardiMember name into a required first name and an optional last name,
    /// so the app can greet a member by the name the family actually uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The existing column is renamed rather than copied, then split in place: first
    /// whitespace-delimited token stays as the first name, the trimmed remainder becomes the last
    /// name, NULL when there is none. The split rule is the same one
    /// <c>CardiTrack.Domain.Common.PersonName.Split</c> applies to a legacy client still sending a
    /// single name — the regexes below use its whitespace set, <c>[ \t\r\n]</c> — so a backfilled
    /// member and one created today by an old app build come out alike.
    /// </para>
    /// <para>
    /// A backfilled split is a guess for multi-word first names ("Mary Ann Smith" becomes
    /// "Mary" / "Ann Smith"); caregivers correct it on the edit screen, which now shows both.
    /// Both columns keep the old column's width, so no stored name can be truncated.
    /// </para>
    /// </remarks>
    public partial class SplitCardiMemberName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Name",
                table: "CardiMembers",
                newName: "FirstName");

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "CardiMembers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Both SET expressions read the pre-update row, so each sees the original full name.
            // COALESCE keeps a blank legacy name (never valid, but not impossible) from turning
            // into a NULL the NOT NULL first-name column would reject.
            migrationBuilder.Sql("""
                UPDATE "CardiMembers"
                SET "FirstName" = COALESCE(
                        substring(btrim("FirstName", E' \t\r\n') from '^[^ \t\r\n]+'), ''),
                    "LastName" = NULLIF(
                        btrim(substring(btrim("FirstName", E' \t\r\n') from '^[^ \t\r\n]+[ \t\r\n]+(.*)$'), E' \t\r\n'),
                        '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rejoin before the last name is dropped. left() because two full-width parts can
            // exceed the single column's 200 characters; a rollback must not fail on one member.
            migrationBuilder.Sql("""
                UPDATE "CardiMembers"
                SET "FirstName" = left("FirstName" || COALESCE(' ' || "LastName", ''), 200);
                """);

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "CardiMembers");

            migrationBuilder.RenameColumn(
                name: "FirstName",
                table: "CardiMembers",
                newName: "Name");
        }
    }
}
