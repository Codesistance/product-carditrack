using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDeliverySpine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Resolves the debt item in notification_engine.md §14: this JSON column
            // ({"sms":...,"email":...,"push":...}) was read by nothing (confirmed by grep across
            // src/ before this migration was written). No data migration accompanies the drop —
            // sms/email are permanently out of scope, and the new per-user NotificationPreferences
            // table below has no "push enabled" flag to receive the old "push" boolean into: push
            // reachability is now derived from PushDeviceTokens.OsAuthorizationStatus, not stored
            // as a preference.
            migrationBuilder.DropColumn(
                name: "NotificationPreferences",
                table: "UserCardiMembers");

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Channel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PushDeviceTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    DedupKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CollapseKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledFor = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SentDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EscalationStage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Initial"),
                    EscalatedFrom = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuietHoursStart = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    QuietHoursEnd = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    ShowDetailsOnLockScreen = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    MutedCategories = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushDeviceTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Platform = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Token = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    TokenFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OsAuthorizationStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SafetyChannelEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LastSeenDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    LastAckDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisabledDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisabledReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeviceTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_CardiMemberId",
                table: "NotificationDeliveries",
                column: "CardiMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_DedupKey",
                table: "NotificationDeliveries",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_SourceType_SourceId",
                table: "NotificationDeliveries",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_State_ExpiresAt",
                table: "NotificationDeliveries",
                columns: new[] { "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_State_NextAttemptAt",
                table: "NotificationDeliveries",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_UserId",
                table: "NotificationPreferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDeviceTokens_DisabledDate",
                table: "PushDeviceTokens",
                column: "DisabledDate");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeviceTokens_TokenFingerprint",
                table: "PushDeviceTokens",
                column: "TokenFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDeviceTokens_UserId_DeviceId",
                table: "PushDeviceTokens",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "PushDeviceTokens");

            migrationBuilder.AddColumn<string>(
                name: "NotificationPreferences",
                table: "UserCardiMembers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "{}");
        }
    }
}
