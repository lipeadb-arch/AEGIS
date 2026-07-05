using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Identify_AssetInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assets_TenantId",
                table: "Assets");

            // Greenfield: descartamos o antigo Type (texto livre) — não migramos para RiskRationaleJson.
            migrationBuilder.DropColumn(
                name: "Type",
                table: "Assets");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalRef",
                table: "Assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "Assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Assets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoverySource",
                table: "Assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Assets",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskLevel",
                table: "Assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RiskScore",
                table: "Assets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RiskScoredAt",
                table: "Assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubType",
                table: "Assets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskRationaleJson",
                table: "Assets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_TenantId_Category",
                table: "Assets",
                columns: new[] { "TenantId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_TenantId_Criticality",
                table: "Assets",
                columns: new[] { "TenantId", "Criticality" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_TenantId_ExternalRef",
                table: "Assets",
                columns: new[] { "TenantId", "ExternalRef" },
                unique: true,
                filter: "\"ExternalRef\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_TenantId_RiskLevel",
                table: "Assets",
                columns: new[] { "TenantId", "RiskLevel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assets_TenantId_Category",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_TenantId_Criticality",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_TenantId_ExternalRef",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_TenantId_RiskLevel",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "DiscoverySource",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "RiskScore",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "RiskScoredAt",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "SubType",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "RiskRationaleJson",
                table: "Assets");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Assets",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Assets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalRef",
                table: "Assets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_TenantId",
                table: "Assets",
                column: "TenantId");
        }
    }
}
