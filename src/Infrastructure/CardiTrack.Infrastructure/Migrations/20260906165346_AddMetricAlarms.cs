using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricAlarms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricAlarms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    DerivedFromAlarmId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Metric = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Statistic = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Operator = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ThresholdKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ThresholdValue = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    PeriodMinutes = table.Column<int>(type: "integer", nullable: false),
                    EvaluationPeriods = table.Column<int>(type: "integer", nullable: false),
                    DatapointsToAlarm = table.Column<int>(type: "integer", nullable: false),
                    MissingDataTreatment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContextGate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricAlarms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetricAlarmStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetricAlarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardiMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StateSinceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastEvaluatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAlertId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricAlarmStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricAlarms_DerivedFromAlarmId",
                table: "MetricAlarms",
                column: "DerivedFromAlarmId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricAlarms_OrganizationId_CardiMemberId",
                table: "MetricAlarms",
                columns: new[] { "OrganizationId", "CardiMemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricAlarmStates_CardiMemberId",
                table: "MetricAlarmStates",
                column: "CardiMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricAlarmStates_MetricAlarmId_CardiMemberId",
                table: "MetricAlarmStates",
                columns: new[] { "MetricAlarmId", "CardiMemberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricAlarms");

            migrationBuilder.DropTable(
                name: "MetricAlarmStates");
        }
    }
}
