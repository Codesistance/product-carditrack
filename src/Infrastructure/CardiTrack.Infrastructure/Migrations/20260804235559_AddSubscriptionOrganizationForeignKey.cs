using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the previously missing FK from Subscriptions.OrganizationId to
    /// Organizations with ON DELETE CASCADE, so a subscription can never outlive
    /// its organization (the gap that made orphan cleanup a two-table job).
    /// </summary>
    public partial class AddSubscriptionOrganizationForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Subscriptions predate this constraint, so rows whose organization is
            // already gone would make AddForeignKey fail — purge them first.
            migrationBuilder.Sql("""
                DELETE FROM "Subscriptions" s
                WHERE NOT EXISTS (SELECT 1 FROM "Organizations" o WHERE o."Id" = s."OrganizationId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Organizations_OrganizationId",
                table: "Subscriptions",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Organizations_OrganizationId",
                table: "Subscriptions");
        }
    }
}
