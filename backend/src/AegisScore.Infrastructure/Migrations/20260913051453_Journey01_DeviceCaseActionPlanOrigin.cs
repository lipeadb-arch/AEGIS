using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Journey01_DeviceCaseActionPlanOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginAssetId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginContextJson",
                table: "ActionPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginCveId",
                table: "ActionPlans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginExposureId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginKind",
                table: "ActionPlans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginThreatId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlans_TenantId_OriginKind_OriginAssetId_OriginCveId_S~",
                table: "ActionPlans",
                columns: new[] { "TenantId", "OriginKind", "OriginAssetId", "OriginCveId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_ActionPlans_ActiveByDeviceCase",
                table: "ActionPlans",
                columns: new[] { "TenantId", "OriginAssetId", "OriginCveId" },
                unique: true,
                filter: "\"OriginKind\" = 2 AND \"Status\" IN (0, 1, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActionPlans_TenantId_OriginKind_OriginAssetId_OriginCveId_S~",
                table: "ActionPlans");

            migrationBuilder.DropIndex(
                name: "UX_ActionPlans_ActiveByDeviceCase",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginAssetId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginContextJson",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginCveId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginExposureId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginThreatId",
                table: "ActionPlans");
        }
    }
}
