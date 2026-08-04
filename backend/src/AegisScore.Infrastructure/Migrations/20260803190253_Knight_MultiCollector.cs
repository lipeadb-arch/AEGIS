using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Knight_MultiCollector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotEvaluatedReason",
                table: "KnightIndicatorResults",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceType",
                table: "KnightIndicatorResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "KnightAssessmentRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceState",
                table: "KnightAssessmentRuns",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "SourceType",
                table: "KnightAssessmentRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotEvaluatedReason",
                table: "KnightIndicatorResults");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "KnightIndicatorResults");

            migrationBuilder.DropColumn(
                name: "CapabilitiesJson",
                table: "KnightAssessmentRuns");

            migrationBuilder.DropColumn(
                name: "SourceState",
                table: "KnightAssessmentRuns");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "KnightAssessmentRuns");
        }
    }
}
