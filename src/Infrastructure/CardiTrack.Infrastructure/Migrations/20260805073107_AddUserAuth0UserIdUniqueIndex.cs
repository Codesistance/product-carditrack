using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Backs onboarding's idempotent-retry lookup: unique (filtered to non-empty, for
    /// rows not yet linked to Auth0) so concurrent setup retries cannot create
    /// duplicate accounts. Deliberately no automatic dedupe — if duplicate non-empty
    /// Auth0UserIds somehow exist, this migration fails and the accounts must be
    /// resolved by hand rather than deleted blindly.
    /// </summary>
    public partial class AddUserAuth0UserIdUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Users_Auth0UserId",
                table: "Users",
                column: "Auth0UserId",
                unique: true,
                filter: "\"Auth0UserId\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Auth0UserId",
                table: "Users");
        }
    }
}
