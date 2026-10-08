using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NistAi01_ContextualAssistance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NistAiAssistances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Focus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ContextFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContextSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourcesJson = table.Column<string>(type: "text", nullable: false),
                    OutputJson = table.Column<string>(type: "text", nullable: false),
                    ApplicableJson = table.Column<string>(type: "text", nullable: true),
                    ValidationNotesJson = table.Column<string>(type: "text", nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Availability = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MethodologyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StaleOnArrival = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistAiAssistances", x => x.Id);
                    table.UniqueConstraint("AK_NistAiAssistances_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_NistAiAssistances_NistCycles_CycleId_TenantId",
                        columns: x => new { x.CycleId, x.TenantId },
                        principalTable: "NistCycles",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NistExecutiveSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectionsJson = table.Column<string>(type: "text", nullable: false),
                    AssistanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    BasisFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Edited = table.Column<bool>(type: "boolean", nullable: false),
                    StaleAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcceptedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReviewedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistExecutiveSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NistExecutiveSummaries_NistCycles_CycleId_TenantId",
                        columns: x => new { x.CycleId, x.TenantId },
                        principalTable: "NistCycles",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NistAiIncorporations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    AssistanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false),
                    StaleAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    IncorporatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    IncorporatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IncorporatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistAiIncorporations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NistAiIncorporations_NistAiAssistances_AssistanceId_TenantId",
                        columns: x => new { x.AssistanceId, x.TenantId },
                        principalTable: "NistAiAssistances",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NistAiAssistances_CycleId_TenantId",
                table: "NistAiAssistances",
                columns: new[] { "CycleId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistAiAssistances_TenantId_CycleId_AssessmentScopeId_Kind_S~",
                table: "NistAiAssistances",
                columns: new[] { "TenantId", "CycleId", "AssessmentScopeId", "Kind", "SubcategoryCode", "FindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistAiIncorporations_AssistanceId_TenantId",
                table: "NistAiIncorporations",
                columns: new[] { "AssistanceId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistAiIncorporations_TenantId_CycleId_AssessmentScopeId_Sub~",
                table: "NistAiIncorporations",
                columns: new[] { "TenantId", "CycleId", "AssessmentScopeId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_NistAiIncorporations_TenantId_TargetKind_TargetId",
                table: "NistAiIncorporations",
                columns: new[] { "TenantId", "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistExecutiveSummaries_CycleId_TenantId",
                table: "NistExecutiveSummaries",
                columns: new[] { "CycleId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistExecutiveSummaries_TenantId",
                table: "NistExecutiveSummaries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_NistExecutiveSummaries_CycleScope",
                table: "NistExecutiveSummaries",
                columns: new[] { "CycleId", "AssessmentScopeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NistAiIncorporations");

            migrationBuilder.DropTable(
                name: "NistExecutiveSummaries");

            migrationBuilder.DropTable(
                name: "NistAiAssistances");
        }
    }
}
