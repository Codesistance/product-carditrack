using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationMutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    MutedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    MutedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedConsequence = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationMutes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRunLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrganizationsScanned = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Created = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Resolved = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Suppressed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    DurationMs = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRunLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Priority = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TitleKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BodyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BenefitKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TemplateData = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ActionDeepLink = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SnoozedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FirstDetectedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    LastEvaluatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    FirstSeenDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsOwner = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMutes_UserId",
                table: "NotificationMutes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMutes_UserId_Category",
                table: "NotificationMutes",
                columns: new[] { "UserId", "Category" },
                unique: true,
                filter: "\"Category\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMutes_UserId_RuleCode",
                table: "NotificationMutes",
                columns: new[] { "UserId", "RuleCode" },
                unique: true,
                filter: "\"RuleCode\" IS NOT NULL AND \"CardiMemberId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationMutes_UserId_RuleCode_CardiMemberId",
                table: "NotificationMutes",
                columns: new[] { "UserId", "RuleCode", "CardiMemberId" },
                unique: true,
                filter: "\"RuleCode\" IS NOT NULL AND \"CardiMemberId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRunLogs_StartedAt",
                table: "NotificationRunLogs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CardiMemberId",
                table: "Notifications",
                column: "CardiMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Fingerprint",
                table: "Notifications",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_OrganizationId",
                table: "Notifications",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_State_ResolvedDate",
                table: "Notifications",
                columns: new[] { "State", "ResolvedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_RuleCode",
                table: "Notifications",
                columns: new[] { "UserId", "RuleCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_State_Priority",
                table: "Notifications",
                columns: new[] { "UserId", "State", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationMutes");

            migrationBuilder.DropTable(
                name: "NotificationRunLogs");

            migrationBuilder.DropTable(
                name: "Notifications");
        }
    }
}
