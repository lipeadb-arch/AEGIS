using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Knight_Foundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnightAssessmentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CatalogVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    Coverage = table.Column<double>(type: "double precision", nullable: false),
                    ScoreFormulaVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PassedCount = table.Column<int>(type: "integer", nullable: false),
                    ExposedCount = table.Column<int>(type: "integer", nullable: false),
                    MitigatedCount = table.Column<int>(type: "integer", nullable: false),
                    NotEvaluatedCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    NotApplicableCount = table.Column<int>(type: "integer", nullable: false),
                    AdvisoryJson = table.Column<string>(type: "text", nullable: true),
                    AdvisoryFromAi = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnightAssessmentRuns", x => x.Id);
                    table.UniqueConstraint("AK_KnightAssessmentRuns_Id_TenantId", x => new { x.Id, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "KnightIndicatorResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    AffectedObjectCount = table.Column<int>(type: "integer", nullable: false),
                    NistCodes = table.Column<string>(type: "jsonb", nullable: false),
                    MitreTechniques = table.Column<string>(type: "jsonb", nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnightIndicatorResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnightIndicatorResults_KnightAssessmentRuns_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "KnightAssessmentRuns",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnightAssessmentRuns_TenantId_StartedAt",
                table: "KnightAssessmentRuns",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnightIndicatorResults_RunId_TenantId",
                table: "KnightIndicatorResults",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnightIndicatorResults_TenantId_RunId_IndicatorId",
                table: "KnightIndicatorResults",
                columns: new[] { "TenantId", "RunId", "IndicatorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnightIndicatorResults");

            migrationBuilder.DropTable(
                name: "KnightAssessmentRuns");
        }
    }
}
