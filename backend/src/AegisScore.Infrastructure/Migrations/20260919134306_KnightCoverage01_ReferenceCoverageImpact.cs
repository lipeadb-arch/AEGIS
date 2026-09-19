using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnightCoverage01_ReferenceCoverageImpact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferenceCoverageJson",
                table: "PostureSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Impact",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "PostureSnapshotIndicators",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceCoverageJson",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "PostureSnapshotIndicators");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "PostureSnapshotIndicators");
        }
    }
}
