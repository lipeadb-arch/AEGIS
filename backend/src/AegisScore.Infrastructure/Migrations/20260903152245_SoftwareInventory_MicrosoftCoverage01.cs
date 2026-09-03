using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SoftwareInventory_MicrosoftCoverage01 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SoftwareInventorySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CollectionState = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptState = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCollectionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TotalProducts = table.Column<int>(type: "integer", nullable: false),
                    ProductsWithWeaknesses = table.Column<int>(type: "integer", nullable: false),
                    ProductsWithPublicExploit = table.Column<int>(type: "integer", nullable: false),
                    ProductsWithActiveAlert = table.Column<int>(type: "integer", nullable: false),
                    ExposedInstallations = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventorySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareInventorySnapshots_Connectors_ConnectorConfigId_Ten~",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    VendorKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WeaknessesCount = table.Column<int>(type: "integer", nullable: false),
                    HasPublicExploit = table.Column<bool>(type: "boolean", nullable: false),
                    HasActiveAlert = table.Column<bool>(type: "boolean", nullable: false),
                    ExposedMachinesCount = table.Column<int>(type: "integer", nullable: false),
                    ImpactScore = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareProducts", x => x.Id);
                    table.UniqueConstraint("AK_SoftwareProducts_Id_TenantId", x => new { x.Id, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInstallations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoftwareProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LifecycleState = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInstallations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareInstallations_Asset_Tenant",
                        columns: x => new { x.AssetId, x.TenantId },
                        principalTable: "Assets",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SoftwareInstallations_Connector_Tenant",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SoftwareInstallations_Product_Tenant",
                        columns: x => new { x.SoftwareProductId, x.TenantId },
                        principalTable: "SoftwareProducts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareProductSourceBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoftwareProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalProductId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    VendorObserved = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NameObserved = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Weaknesses = table.Column<int>(type: "integer", nullable: false),
                    PublicExploit = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveAlert = table.Column<bool>(type: "boolean", nullable: false),
                    ExposedMachines = table.Column<int>(type: "integer", nullable: false),
                    ImpactScore = table.Column<double>(type: "double precision", nullable: true),
                    FirstObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareProductSourceBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareProductSourceBindings_Connector_Tenant",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SoftwareProductSourceBindings_Product_Tenant",
                        columns: x => new { x.SoftwareProductId, x.TenantId },
                        principalTable: "SoftwareProducts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_AssetId_TenantId",
                table: "SoftwareInstallations",
                columns: new[] { "AssetId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_ConnectorConfigId_TenantId",
                table: "SoftwareInstallations",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_SoftwareProductId_TenantId",
                table: "SoftwareInstallations",
                columns: new[] { "SoftwareProductId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_TenantId_AssetId",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_TenantId_ConnectorConfigId",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "ConnectorConfigId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_TenantId_SoftwareProductId",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "SoftwareProductId" });

            migrationBuilder.CreateIndex(
                name: "UX_SoftwareInstallation_Natural",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "ConnectorConfigId", "AssetId", "SoftwareProductId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventorySnapshots_ConnectorConfigId_TenantId",
                table: "SoftwareInventorySnapshots",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_SoftwareInventorySnapshot_Natural",
                table: "SoftwareInventorySnapshots",
                columns: new[] { "TenantId", "ConnectorConfigId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareProducts_TenantId_IsActive",
                table: "SoftwareProducts",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_SoftwareProduct_Natural",
                table: "SoftwareProducts",
                columns: new[] { "TenantId", "VendorKey", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareProductSourceBindings_ConnectorConfigId_TenantId",
                table: "SoftwareProductSourceBindings",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareProductSourceBindings_SoftwareProductId_TenantId",
                table: "SoftwareProductSourceBindings",
                columns: new[] { "SoftwareProductId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareProductSourceBindings_TenantId_ConnectorConfigId",
                table: "SoftwareProductSourceBindings",
                columns: new[] { "TenantId", "ConnectorConfigId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareProductSourceBindings_TenantId_SoftwareProductId",
                table: "SoftwareProductSourceBindings",
                columns: new[] { "TenantId", "SoftwareProductId" });

            migrationBuilder.CreateIndex(
                name: "UX_SoftwareProductSourceBinding_Natural",
                table: "SoftwareProductSourceBindings",
                columns: new[] { "TenantId", "ConnectorConfigId", "ExternalProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SoftwareInstallations");

            migrationBuilder.DropTable(
                name: "SoftwareInventorySnapshots");

            migrationBuilder.DropTable(
                name: "SoftwareProductSourceBindings");

            migrationBuilder.DropTable(
                name: "SoftwareProducts");
        }
    }
}
