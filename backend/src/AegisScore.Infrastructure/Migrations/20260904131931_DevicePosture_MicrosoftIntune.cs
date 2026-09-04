using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DevicePosture_MicrosoftIntune : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevicePostureSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConfigurationState = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationAttemptState = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfigurationCollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompliancePolicyCount = table.Column<int>(type: "integer", nullable: false),
                    DeviceConfigurationCount = table.Column<int>(type: "integer", nullable: false),
                    AssignmentState = table.Column<int>(type: "integer", nullable: false),
                    PoliciesAssigned = table.Column<int>(type: "integer", nullable: false),
                    PoliciesUnassigned = table.Column<int>(type: "integer", nullable: false),
                    PoliciesAssignmentUnknown = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceState = table.Column<int>(type: "integer", nullable: false),
                    DeviceAttemptState = table.Column<int>(type: "integer", nullable: false),
                    DeviceAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeviceCollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalDevices = table.Column<int>(type: "integer", nullable: false),
                    CompliantDevices = table.Column<int>(type: "integer", nullable: false),
                    NoncompliantDevices = table.Column<int>(type: "integer", nullable: false),
                    InGracePeriodDevices = table.Column<int>(type: "integer", nullable: false),
                    ConflictDevices = table.Column<int>(type: "integer", nullable: false),
                    ErrorDevices = table.Column<int>(type: "integer", nullable: false),
                    ManagedExternallyDevices = table.Column<int>(type: "integer", nullable: false),
                    UnknownComplianceDevices = table.Column<int>(type: "integer", nullable: false),
                    EncryptedDevices = table.Column<int>(type: "integer", nullable: false),
                    NotEncryptedDevices = table.Column<int>(type: "integer", nullable: false),
                    UnknownEncryptionDevices = table.Column<int>(type: "integer", nullable: false),
                    ActiveDevices = table.Column<int>(type: "integer", nullable: false),
                    StaleDevices = table.Column<int>(type: "integer", nullable: false),
                    UnknownActivityDevices = table.Column<int>(type: "integer", nullable: false),
                    StaleThresholdDays = table.Column<int>(type: "integer", nullable: false),
                    DevicesWithDirectoryId = table.Column<int>(type: "integer", nullable: false),
                    DeviceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePostureSnapshots", x => x.Id);
                    table.UniqueConstraint("AK_DevicePostureSnapshots_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_DevicePostureSnapshots_Connectors_ConnectorConfigId_TenantId",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevicePostureDeviceGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevicePostureSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatingSystem = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Compliance = table.Column<int>(type: "integer", nullable: false),
                    Encryption = table.Column<int>(type: "integer", nullable: false),
                    Activity = table.Column<int>(type: "integer", nullable: false),
                    DeviceCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePostureDeviceGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevicePostureDeviceGroups_DevicePostureSnapshots_DevicePost~",
                        columns: x => new { x.DevicePostureSnapshotId, x.TenantId },
                        principalTable: "DevicePostureSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevicePosturePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevicePostureSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlatformLabel = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    AssignmentState = table.Column<int>(type: "integer", nullable: false),
                    AssignmentCount = table.Column<int>(type: "integer", nullable: true),
                    SourceLastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePosturePolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevicePosturePolicies_DevicePostureSnapshots_DevicePostureS~",
                        columns: x => new { x.DevicePostureSnapshotId, x.TenantId },
                        principalTable: "DevicePostureSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevicePostureDeviceGroups_DevicePostureSnapshotId_TenantId",
                table: "DevicePostureDeviceGroups",
                columns: new[] { "DevicePostureSnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DevicePostureDeviceGroups_TenantId_DevicePostureSnapshotId",
                table: "DevicePostureDeviceGroups",
                columns: new[] { "TenantId", "DevicePostureSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "UX_DevicePostureDeviceGroup_Natural",
                table: "DevicePostureDeviceGroups",
                columns: new[] { "TenantId", "DevicePostureSnapshotId", "OperatingSystem", "Compliance", "Encryption", "Activity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevicePosturePolicies_DevicePostureSnapshotId_TenantId",
                table: "DevicePosturePolicies",
                columns: new[] { "DevicePostureSnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DevicePosturePolicies_TenantId_DevicePostureSnapshotId",
                table: "DevicePosturePolicies",
                columns: new[] { "TenantId", "DevicePostureSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "UX_DevicePosturePolicy_Natural",
                table: "DevicePosturePolicies",
                columns: new[] { "TenantId", "DevicePostureSnapshotId", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevicePostureSnapshots_ConnectorConfigId_TenantId",
                table: "DevicePostureSnapshots",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_DevicePostureSnapshot_Natural",
                table: "DevicePostureSnapshots",
                columns: new[] { "TenantId", "ConnectorConfigId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevicePostureDeviceGroups");

            migrationBuilder.DropTable(
                name: "DevicePosturePolicies");

            migrationBuilder.DropTable(
                name: "DevicePostureSnapshots");
        }
    }
}
