using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Data fix: onboarding's CreateOrganization used to succeed server-side while the
    /// client saw a malformed envelope and never reached the CreateUser step, leaving
    /// organizations (each with a trial subscription) that no user or CardiMember
    /// references. Subscriptions.OrganizationId has no FK constraint, so the
    /// subscription rows must be removed explicitly before their organizations.
    /// The one-hour age guard keeps an onboarding that is in flight while the
    /// migration runs (org created, user not yet) out of scope.
    /// </summary>
    public partial class CleanupOrphanedOnboardingOrganizations : Migration
    {
        private const string OrphanedOrganizationPredicate = """
            o."CreatedDate" < NOW() - INTERVAL '1 hour'
            AND NOT EXISTS (SELECT 1 FROM "Users" u WHERE u."OrganizationId" = o."Id")
            AND NOT EXISTS (SELECT 1 FROM "CardiMembers" c WHERE c."OrganizationId" = o."Id")
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "Subscriptions" s
                USING "Organizations" o
                WHERE s."OrganizationId" = o."Id"
                AND {OrphanedOrganizationPredicate};
                """);

            migrationBuilder.Sql($"""
                DELETE FROM "Organizations" o
                WHERE {OrphanedOrganizationPredicate};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted rows are unrecoverable orphans; nothing to restore.
        }
    }
}
