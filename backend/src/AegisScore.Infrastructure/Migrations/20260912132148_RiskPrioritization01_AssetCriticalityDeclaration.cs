using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RiskPrioritization01_AssetCriticalityDeclaration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CriticalityDeclarationNote",
                table: "Assets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CriticalityDeclaredAt",
                table: "Assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CriticalityDeclaredByAccountId",
                table: "Assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CriticalityDeclaredByName",
                table: "Assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CriticalityDeclaredValue",
                table: "Assets",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CriticalityDeclarationNote",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "CriticalityDeclaredAt",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "CriticalityDeclaredByAccountId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "CriticalityDeclaredByName",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "CriticalityDeclaredValue",
                table: "Assets");
        }
    }
}
