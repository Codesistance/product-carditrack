using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOneActiveAdminIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizations_OneActiveAdmin",
                table: "UserOrganizations",
                columns: new[] { "OrganizationId", "Role", "IsActive" },
                unique: true,
                filter: "\"Role\" = 'Admin' AND \"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserOrganizations_OneActiveAdmin",
                table: "UserOrganizations");
        }
    }
}
