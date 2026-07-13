using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Identify_BlastRadius : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginExposureId",
                table: "Risks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Availability",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Confidentiality",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Financial",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Integrity",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Operational",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_RecoveryPointObjectiveMinutes",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_RecoveryTimeObjectiveMinutes",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Regulatory",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessImpact_Reputational",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Strength = table.Column<int>(type: "integer", nullable: false),
                    DiscoverySource = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetDependencies", x => x.Id);
                    table.CheckConstraint("CK_AssetDependency_NoSelfLoop", "\"SourceAssetId\" <> \"TargetAssetId\"");
                    table.ForeignKey(
                        name: "FK_AssetDependencies_Assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetDependencies_Assets_TargetAssetId",
                        column: x => x.TargetAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Threats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    BaseSeverity = table.Column<double>(type: "double precision", nullable: false),
                    Tactic = table.Column<string>(type: "text", nullable: true),
                    KnownExploited = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Threats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssetThreatExposures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Likelihood = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MitigatingSubcategoryCode = table.Column<string>(type: "text", nullable: true),
                    DiscoverySource = table.Column<int>(type: "integer", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetThreatExposures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetThreatExposures_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetThreatExposures_Threats_ThreatId",
                        column: x => x.ThreatId,
                        principalTable: "Threats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BlastRadiusAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioThreatId = table.Column<Guid>(type: "uuid", nullable: true),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    BlastRadiusScore = table.Column<double>(type: "double precision", nullable: false),
                    RiskLevel = table.Column<int>(type: "integer", nullable: false),
                    ImpactedAssetCount = table.Column<int>(type: "integer", nullable: false),
                    ImpactedProcessCount = table.Column<int>(type: "integer", nullable: false),
                    MaxDepth = table.Column<int>(type: "integer", nullable: false),
                    FactorsJson = table.Column<string>(type: "text", nullable: false),
                    ComputedBy = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlastRadiusAssessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlastRadiusAssessments_Assets_RootAssetId",
                        column: x => x.RootAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BlastRadiusAssessments_Threats_ScenarioThreatId",
                        column: x => x.ScenarioThreatId,
                        principalTable: "Threats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BlastRadiusImpactNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImpactedAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Distance = table.Column<int>(type: "integer", nullable: false),
                    PropagatedImpact = table.Column<double>(type: "double precision", nullable: false),
                    PathStrength = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlastRadiusImpactNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlastRadiusImpactNodes_Assets_ImpactedAssetId",
                        column: x => x.ImpactedAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BlastRadiusImpactNodes_BlastRadiusAssessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "BlastRadiusAssessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetDependencies_SourceAssetId",
                table: "AssetDependencies",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetDependencies_TargetAssetId",
                table: "AssetDependencies",
                column: "TargetAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetDependencies_TenantId_SourceAssetId_TargetAssetId_Type",
                table: "AssetDependencies",
                columns: new[] { "TenantId", "SourceAssetId", "TargetAssetId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetThreatExposures_AssetId",
                table: "AssetThreatExposures",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetThreatExposures_TenantId_AssetId_ThreatId",
                table: "AssetThreatExposures",
                columns: new[] { "TenantId", "AssetId", "ThreatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetThreatExposures_ThreatId",
                table: "AssetThreatExposures",
                column: "ThreatId");

            migrationBuilder.CreateIndex(
                name: "IX_BlastRadiusAssessments_RootAssetId",
                table: "BlastRadiusAssessments",
                column: "RootAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_BlastRadiusAssessments_ScenarioThreatId",
                table: "BlastRadiusAssessments",
                column: "ScenarioThreatId");

            migrationBuilder.CreateIndex(
                name: "IX_BlastRadiusAssessments_TenantId_RootAssetId",
                table: "BlastRadiusAssessments",
                columns: new[] { "TenantId", "RootAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_BlastRadiusImpactNodes_AssessmentId",
                table: "BlastRadiusImpactNodes",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_BlastRadiusImpactNodes_ImpactedAssetId",
                table: "BlastRadiusImpactNodes",
                column: "ImpactedAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_BlastRadiusImpactNodes_TenantId_AssessmentId",
                table: "BlastRadiusImpactNodes",
                columns: new[] { "TenantId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Threats_TenantId_Code_Source",
                table: "Threats",
                columns: new[] { "TenantId", "Code", "Source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetDependencies");

            migrationBuilder.DropTable(
                name: "AssetThreatExposures");

            migrationBuilder.DropTable(
                name: "BlastRadiusImpactNodes");

            migrationBuilder.DropTable(
                name: "BlastRadiusAssessments");

            migrationBuilder.DropTable(
                name: "Threats");

            migrationBuilder.DropColumn(
                name: "OriginExposureId",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Availability",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Confidentiality",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Financial",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Integrity",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Operational",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_RecoveryPointObjectiveMinutes",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_RecoveryTimeObjectiveMinutes",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Regulatory",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "BusinessImpact_Reputational",
                table: "Assets");
        }
    }
}
