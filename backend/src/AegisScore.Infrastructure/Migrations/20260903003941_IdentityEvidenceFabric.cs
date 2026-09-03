using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IdentityEvidenceFabric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityEvidenceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DataState = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptState = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCollectionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityEvidenceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityEvidenceSnapshots_Connectors_ConnectorConfigId_Tena~",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityEvidenceSnapshots_ConnectorConfigId_TenantId",
                table: "IdentityEvidenceSnapshots",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentityEvidenceSnapshot_Natural",
                table: "IdentityEvidenceSnapshots",
                columns: new[] { "TenantId", "ConnectorConfigId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityEvidenceSnapshots");
        }
    }
}
