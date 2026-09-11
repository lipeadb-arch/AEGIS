using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ENTITY-RESOLUTION-01] Migration ADITIVA da resolução de dispositivos entre fontes: a tabela da chave
    /// forte (dois índices únicos nomeados + FK composta tenant-safe + CHECK contra GUID vazio), colunas anuláveis ou
    /// com default 0 (= NotEvaluated/None/Unspecified) em AssetSourceBindings e Assets. SEM backfill: nenhum
    /// identificador é inventado para bindings legados — eles ficam "não avaliados" até a próxima coleta real da fonte.
    /// Nenhuma migration anterior é reescrita; nenhum dado é apagado ou movido.
    /// </summary>
    public partial class DeviceResolution01_StrongIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConflictAssetId",
                table: "AssetSourceBindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictDirectoryDeviceId",
                table: "AssetSourceBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConflictKind",
                table: "AssetSourceBindings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryDeviceId",
                table: "AssetSourceBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DirectoryIdStatus",
                table: "AssetSourceBindings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryNamespace",
                table: "AssetSourceBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LinkedAt",
                table: "AssetSourceBindings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionEvaluatedAt",
                table: "AssetSourceBindings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolutionState",
                table: "AssetSourceBindings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SourceCompliance",
                table: "AssetSourceBindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceEncryption",
                table: "AssetSourceBindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLabel",
                table: "AssetSourceBindings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NameOrigin",
                table: "Assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AssetStrongIdentifiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectoryNamespace = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdentifierType = table.Column<int>(type: "integer", nullable: false),
                    IdentifierValue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EstablishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EstablishedByConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstablishedBySource = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetStrongIdentifiers", x => x.Id);
                    table.CheckConstraint("CK_AssetStrongIdentifiers_Valid", "\"IdentifierType\" IN (1) AND \"IdentifierValue\" <> '00000000-0000-0000-0000-000000000000' AND \"IdentifierValue\" <> '' AND \"DirectoryNamespace\" <> ''");
                    table.ForeignKey(
                        name: "FK_AssetStrongIdentifiers_Assets_Asset_Tenant",
                        columns: x => new { x.AssetId, x.TenantId },
                        principalTable: "Assets",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetStrongIdentifiers_AssetId_TenantId",
                table: "AssetStrongIdentifiers",
                columns: new[] { "AssetId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_AssetStrongIdentifier_AssetScope",
                table: "AssetStrongIdentifiers",
                columns: new[] { "TenantId", "AssetId", "DirectoryNamespace", "IdentifierType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AssetStrongIdentifier_Natural",
                table: "AssetStrongIdentifiers",
                columns: new[] { "TenantId", "DirectoryNamespace", "IdentifierType", "IdentifierValue" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetStrongIdentifiers");

            migrationBuilder.DropColumn(
                name: "ConflictAssetId",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "ConflictDirectoryDeviceId",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "ConflictKind",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "DirectoryDeviceId",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "DirectoryIdStatus",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "DirectoryNamespace",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "LinkedAt",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "ResolutionEvaluatedAt",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "ResolutionState",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "SourceCompliance",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "SourceEncryption",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "SourceLabel",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "NameOrigin",
                table: "Assets");
        }
    }
}
