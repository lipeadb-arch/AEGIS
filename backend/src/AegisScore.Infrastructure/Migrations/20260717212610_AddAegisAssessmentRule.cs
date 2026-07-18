using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAegisAssessmentRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssessmentRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    EvaluationMetrics = table.Column<string>(type: "jsonb", nullable: false),
                    CalculationLogic = table.Column<string>(type: "text", nullable: false),
                    EvidenceRequirements = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentRules_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentRules_SubcategoryCode",
                table: "AssessmentRules",
                column: "SubcategoryCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentRules_SubcategoryId",
                table: "AssessmentRules",
                column: "SubcategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssessmentRules");
        }
    }
}
