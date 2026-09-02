using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DetectionCoverage_GoogleSecOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DetectionCoverageSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AttackVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CollectionState = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptState = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCollectionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalActiveRules = table.Column<int>(type: "integer", nullable: false),
                    RulesWithMitre = table.Column<int>(type: "integer", nullable: false),
                    RulesWithoutMitre = table.Column<int>(type: "integer", nullable: false),
                    RulesInLiveMode = table.Column<int>(type: "integer", nullable: false),
                    RulesInNormalExecution = table.Column<int>(type: "integer", nullable: false),
                    RulesInLimitedExecution = table.Column<int>(type: "integer", nullable: false),
                    RulesInPausedExecution = table.Column<int>(type: "integer", nullable: false),
                    RulesInUnknownExecution = table.Column<int>(type: "integer", nullable: false),
                    RulesWithAlerting = table.Column<int>(type: "integer", nullable: false),
                    TechniquesObserved = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectionCoverageSnapshots", x => x.Id);
                    table.UniqueConstraint("AK_DetectionCoverageSnapshots_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_DetectionCoverageSnapshots_Connectors_ConnectorConfigId_Ten~",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DetectionCoverageTechniques",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectionCoverageSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    TechniqueId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RuleCount = table.Column<int>(type: "integer", nullable: false),
                    LiveRuleCount = table.Column<int>(type: "integer", nullable: false),
                    NormalExecutionRuleCount = table.Column<int>(type: "integer", nullable: false),
                    LimitedExecutionRuleCount = table.Column<int>(type: "integer", nullable: false),
                    PausedExecutionRuleCount = table.Column<int>(type: "integer", nullable: false),
                    UnknownExecutionRuleCount = table.Column<int>(type: "integer", nullable: false),
                    AlertingRuleCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectionCoverageTechniques", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetectionCoverageTechniques_DetectionCoverageSnapshots_Dete~",
                        columns: x => new { x.DetectionCoverageSnapshotId, x.TenantId },
                        principalTable: "DetectionCoverageSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DetectionCoverageSnapshots_ConnectorConfigId_TenantId",
                table: "DetectionCoverageSnapshots",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_DetectionCoverageSnapshot_Natural",
                table: "DetectionCoverageSnapshots",
                columns: new[] { "TenantId", "ConnectorConfigId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DetectionCoverageTechniques_DetectionCoverageSnapshotId_Ten~",
                table: "DetectionCoverageTechniques",
                columns: new[] { "DetectionCoverageSnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DetectionCoverageTechniques_TenantId_DetectionCoverageSnaps~",
                table: "DetectionCoverageTechniques",
                columns: new[] { "TenantId", "DetectionCoverageSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "UX_DetectionCoverageTechnique_Natural",
                table: "DetectionCoverageTechniques",
                columns: new[] { "TenantId", "DetectionCoverageSnapshotId", "TechniqueId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetectionCoverageTechniques");

            migrationBuilder.DropTable(
                name: "DetectionCoverageSnapshots");
        }
    }
}
