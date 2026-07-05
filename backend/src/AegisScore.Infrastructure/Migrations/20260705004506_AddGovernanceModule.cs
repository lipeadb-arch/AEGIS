using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GovernancePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    StorageUri = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OwnerArea = table.Column<string>(type: "text", nullable: true),
                    OwnerPerson = table.Column<string>(type: "text", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextReviewDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresAt = table.Column<DateOnly>(type: "date", nullable: true),
                    MappedSubcategoryCodes = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorName = table.Column<string>(type: "text", nullable: false),
                    VendorType = table.Column<int>(type: "integer", nullable: false),
                    ServiceDescription = table.Column<string>(type: "text", nullable: true),
                    ContractReference = table.Column<string>(type: "text", nullable: true),
                    ContractUri = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    VendorCriticality = table.Column<int>(type: "integer", nullable: false),
                    DataClassificationShared = table.Column<int>(type: "integer", nullable: false),
                    HasDataProcessingAgreement = table.Column<bool>(type: "boolean", nullable: false),
                    HasSecurityAssessment = table.Column<bool>(type: "boolean", nullable: false),
                    LastAssessmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ModelOrServiceName = table.Column<string>(type: "text", nullable: true),
                    ProcessesSensitiveData = table.Column<bool>(type: "boolean", nullable: false),
                    TrainsOnTenantData = table.Column<bool>(type: "boolean", nullable: false),
                    SlaAvailabilityTarget = table.Column<double>(type: "double precision", nullable: true),
                    SlaResponseTimeHours = table.Column<int>(type: "integer", nullable: true),
                    SlaNotes = table.Column<string>(type: "text", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RenewalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresAt = table.Column<DateOnly>(type: "date", nullable: true),
                    OwnerArea = table.Column<string>(type: "text", nullable: true),
                    OwnerPerson = table.Column<string>(type: "text", nullable: true),
                    VendorContactName = table.Column<string>(type: "text", nullable: true),
                    VendorContactEmail = table.Column<string>(type: "text", nullable: true),
                    MappedSubcategoryCodes = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GovernancePolicies");

            migrationBuilder.DropTable(
                name: "SupplyChainContracts");
        }
    }
}
