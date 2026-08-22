using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PostureExposureFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostureExposureFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActionType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CurrentScore = table.Column<double>(type: "double precision", nullable: false),
                    MaxScore = table.Column<double>(type: "double precision", nullable: false),
                    Gap = table.Column<double>(type: "double precision", nullable: false),
                    SourceRank = table.Column<int>(type: "integer", nullable: true),
                    Tier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ImplementationCost = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserImpact = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remediation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RemediationImpact = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Threats = table.Column<string>(type: "jsonb", nullable: false),
                    SourceState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LifecycleState = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostureExposureFindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostureExposureFindings_TenantId_ConnectorConfigId",
                table: "PostureExposureFindings",
                columns: new[] { "TenantId", "ConnectorConfigId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureExposureFindings_TenantId_LifecycleState",
                table: "PostureExposureFindings",
                columns: new[] { "TenantId", "LifecycleState" });

            migrationBuilder.CreateIndex(
                name: "UX_PostureExposureFinding_Natural",
                table: "PostureExposureFindings",
                columns: new[] { "TenantId", "ConnectorConfigId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostureExposureFindings");
        }
    }
}
