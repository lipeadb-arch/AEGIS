using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorGovernToDocumentHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GovernancePolicies");

            migrationBuilder.DropTable(
                name: "SupplyChainContracts");

            migrationBuilder.CreateTable(
                name: "GovernanceDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceReference = table.Column<string>(type: "text", nullable: true),
                    FileName = table.Column<string>(type: "text", nullable: true),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    StorageUri = table.Column<string>(type: "text", nullable: true),
                    Sha256 = table.Column<string>(type: "text", nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AnalysisStatus = table.Column<int>(type: "integer", nullable: false),
                    AnalysisQueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnalysisSummary = table.Column<string>(type: "text", nullable: true),
                    AnalysisError = table.Column<string>(type: "text", nullable: true),
                    ModelUsed = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernanceDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GrcInterviewSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TargetSubcategoryCodes = table.Column<string>(type: "jsonb", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrcInterviewSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentifiedRisks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Cause = table.Column<string>(type: "text", nullable: true),
                    Consequence = table.Column<string>(type: "text", nullable: true),
                    SubcategoryCode = table.Column<string>(type: "text", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginInterviewSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PromotedToRisk = table.Column<bool>(type: "boolean", nullable: false),
                    RiskId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdentifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentifiedRisks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubcategoryCoverages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EvidenceSource = table.Column<int>(type: "integer", nullable: false),
                    OriginDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginInterviewSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcategoryCoverages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentControlMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GovernanceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: true),
                    AnalystConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentControlMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentControlMappings_GovernanceDocuments_GovernanceDocum~",
                        column: x => x.GovernanceDocumentId,
                        principalTable: "GovernanceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GrcInterviewMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    TargetSubcategoryCode = table.Column<string>(type: "text", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrcInterviewSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrcInterviewMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrcInterviewMessages_GrcInterviewSessions_GrcInterviewSessi~",
                        column: x => x.GrcInterviewSessionId,
                        principalTable: "GrcInterviewSessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentControlMappings_GovernanceDocumentId",
                table: "DocumentControlMappings",
                column: "GovernanceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentControlMappings_TenantId_GovernanceDocumentId",
                table: "DocumentControlMappings",
                columns: new[] { "TenantId", "GovernanceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentControlMappings_TenantId_SubcategoryCode",
                table: "DocumentControlMappings",
                columns: new[] { "TenantId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceDocuments_TenantId",
                table: "GovernanceDocuments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceDocuments_TenantId_Sha256",
                table: "GovernanceDocuments",
                columns: new[] { "TenantId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_GrcInterviewMessages_GrcInterviewSessionId",
                table: "GrcInterviewMessages",
                column: "GrcInterviewSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GrcInterviewMessages_TenantId_SessionId",
                table: "GrcInterviewMessages",
                columns: new[] { "TenantId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_GrcInterviewSessions_TenantId",
                table: "GrcInterviewSessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentifiedRisks_TenantId_SubcategoryCode",
                table: "IdentifiedRisks",
                columns: new[] { "TenantId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_SubcategoryCoverages_TenantId_SubcategoryCode",
                table: "SubcategoryCoverages",
                columns: new[] { "TenantId", "SubcategoryCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentControlMappings");

            migrationBuilder.DropTable(
                name: "GrcInterviewMessages");

            migrationBuilder.DropTable(
                name: "IdentifiedRisks");

            migrationBuilder.DropTable(
                name: "SubcategoryCoverages");

            migrationBuilder.DropTable(
                name: "GovernanceDocuments");

            migrationBuilder.DropTable(
                name: "GrcInterviewSessions");

            migrationBuilder.CreateTable(
                name: "GovernancePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresAt = table.Column<DateOnly>(type: "date", nullable: true),
                    MappedSubcategoryCodes = table.Column<string>(type: "jsonb", nullable: false),
                    NextReviewDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OwnerArea = table.Column<string>(type: "text", nullable: true),
                    OwnerPerson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StorageUri = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernancePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplyChainContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractReference = table.Column<string>(type: "text", nullable: true),
                    ContractUri = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DataClassificationShared = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateOnly>(type: "date", nullable: true),
                    HasDataProcessingAgreement = table.Column<bool>(type: "boolean", nullable: false),
                    HasSecurityAssessment = table.Column<bool>(type: "boolean", nullable: false),
                    LastAssessmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MappedSubcategoryCodes = table.Column<string>(type: "jsonb", nullable: false),
                    ModelOrServiceName = table.Column<string>(type: "text", nullable: true),
                    OwnerArea = table.Column<string>(type: "text", nullable: true),
                    OwnerPerson = table.Column<string>(type: "text", nullable: true),
                    ProcessesSensitiveData = table.Column<bool>(type: "boolean", nullable: false),
                    RenewalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ServiceDescription = table.Column<string>(type: "text", nullable: true),
                    SlaAvailabilityTarget = table.Column<double>(type: "double precision", nullable: true),
                    SlaNotes = table.Column<string>(type: "text", nullable: true),
                    SlaResponseTimeHours = table.Column<int>(type: "integer", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrainsOnTenantData = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VendorContactEmail = table.Column<string>(type: "text", nullable: true),
                    VendorContactName = table.Column<string>(type: "text", nullable: true),
                    VendorCriticality = table.Column<int>(type: "integer", nullable: false),
                    VendorName = table.Column<string>(type: "text", nullable: false),
                    VendorType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplyChainContracts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GovernancePolicies_TenantId",
                table: "GovernancePolicies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplyChainContracts_TenantId",
                table: "SupplyChainContracts",
                column: "TenantId");
        }
    }
}
