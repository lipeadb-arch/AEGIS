using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnightMulticloud01_ConfigurationEvidenceSyncReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdvisoryFromAi",
                table: "PostureSnapshots",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdvisoryJson",
                table: "PostureSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "PostureSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileCatalogVersion",
                table: "PostureSnapshots",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AffectedDetailComplete",
                table: "PostureSnapshotIndicators",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AffectedDetailLimitation",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Criterion",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DoesNotProve",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "PostureSnapshotIndicators",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedConfiguration",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasAffectedDetail",
                table: "PostureSnapshotIndicators",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotEvaluatedReason",
                table: "PostureSnapshotIndicators",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "PostureSnapshotIndicators",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rationale",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recommendation",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "References",
                table: "PostureSnapshotIndicators",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "RequiredCapabilities",
                table: "PostureSnapshotIndicators",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "PostureSnapshotIndicators",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObservedConfiguration",
                table: "KnightAffectedObjects",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Relation",
                table: "KnightAffectedObjects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "IdentityConfigurationObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcquisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityConfigurationObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityConfigurationObservations_IdentityAcquisitions_Acqu~",
                        columns: x => new { x.AcquisitionId, x.TenantId },
                        principalTable: "IdentityAcquisitions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnightSyncRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultSourceState = table.Column<int>(type: "integer", nullable: true),
                    FailureCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnightSyncRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnightSyncRequests_Connectors_ConnectorConfigId_TenantId",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostureSnapshotObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Relation = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Roles = table.Column<string>(type: "jsonb", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ObservedConfiguration = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostureSnapshotObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostureSnapshotObjects_PostureSnapshots_SnapshotId_TenantId",
                        columns: x => new { x.SnapshotId, x.TenantId },
                        principalTable: "PostureSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityConfigurationObservations_AcquisitionId_TenantId",
                table: "IdentityConfigurationObservations",
                columns: new[] { "AcquisitionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentityConfigurationObservation_Natural",
                table: "IdentityConfigurationObservations",
                columns: new[] { "TenantId", "AcquisitionId", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnightSyncRequests_ConnectorConfigId_TenantId",
                table: "KnightSyncRequests",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnightSyncRequests_Status_AvailableAt",
                table: "KnightSyncRequests",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnightSyncRequests_Status_LeaseExpiresAt",
                table: "KnightSyncRequests",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnightSyncRequests_TenantId_ConnectorConfigId_RequestedAt",
                table: "KnightSyncRequests",
                columns: new[] { "TenantId", "ConnectorConfigId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_KnightSyncRequest_ActivePerConnector",
                table: "KnightSyncRequests",
                columns: new[] { "TenantId", "ConnectorConfigId" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotObjects_SnapshotId_TenantId",
                table: "PostureSnapshotObjects",
                columns: new[] { "SnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotObjects_TenantId_SnapshotId",
                table: "PostureSnapshotObjects",
                columns: new[] { "TenantId", "SnapshotId" });

            // [AEGIS-KNIGHT-MULTICLOUD-01] Os objetos congelados fazem parte da fotografia: mesma imutabilidade no
            // banco que o restante do agregado (a função já existe desde a migration das fotografias auditáveis).
            migrationBuilder.Sql(@"
CREATE TRIGGER trg_posture_snapshot_objects_immutable
    BEFORE UPDATE OR DELETE ON ""PostureSnapshotObjects""
    FOR EACH ROW EXECUTE FUNCTION aegis_block_posture_snapshot_mutation();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_posture_snapshot_objects_immutable ON ""PostureSnapshotObjects"";");

            migrationBuilder.DropTable(
                name: "IdentityConfigurationObservations");

            migrationBuilder.DropTable(
                name: "KnightSyncRequests");

            migrationBuilder.DropTable(
                name: "PostureSnapshotObjects");

            migrationBuilder.DropColumn(
                name: "AdvisoryFromAi",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "AdvisoryJson",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "CapabilitiesJson",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "ProfileCatalogVersion",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "AffectedDetailComplete",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "AffectedDetailLimitation",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Criterion",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "DoesNotProve",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "ExpectedConfiguration",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "HasAffectedDetail",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "NotEvaluatedReason",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Rationale",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Recommendation",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "References",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "RequiredCapabilities",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "ObservedConfiguration",
                table: "KnightAffectedObjects");

            migrationBuilder.DropColumn(
                name: "Relation",
                table: "KnightAffectedObjects");
        }
    }
}
