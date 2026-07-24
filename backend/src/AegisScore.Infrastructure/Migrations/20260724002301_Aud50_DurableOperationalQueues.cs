using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Aud50_DurableOperationalQueues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnalysisAttempts",
                table: "GovernanceDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnalysisLeaseExpiresAt",
                table: "GovernanceDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AnalysisLeaseId",
                table: "GovernanceDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnalysisNextAttemptAt",
                table: "GovernanceDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PolicySyncRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicySyncRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceDocuments_AnalysisStatus_AnalysisLeaseExpiresAt",
                table: "GovernanceDocuments",
                columns: new[] { "AnalysisStatus", "AnalysisLeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceDocuments_AnalysisStatus_AnalysisQueuedAt",
                table: "GovernanceDocuments",
                columns: new[] { "AnalysisStatus", "AnalysisQueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicySyncRequests_Status_AvailableAt",
                table: "PolicySyncRequests",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicySyncRequests_Status_LeaseExpiresAt",
                table: "PolicySyncRequests",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicySyncRequests_TenantId",
                table: "PolicySyncRequests",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicySyncRequests");

            migrationBuilder.DropIndex(
                name: "IX_GovernanceDocuments_AnalysisStatus_AnalysisLeaseExpiresAt",
                table: "GovernanceDocuments");

            migrationBuilder.DropIndex(
                name: "IX_GovernanceDocuments_AnalysisStatus_AnalysisQueuedAt",
                table: "GovernanceDocuments");

            migrationBuilder.DropColumn(
                name: "AnalysisAttempts",
                table: "GovernanceDocuments");

            migrationBuilder.DropColumn(
                name: "AnalysisLeaseExpiresAt",
                table: "GovernanceDocuments");

            migrationBuilder.DropColumn(
                name: "AnalysisLeaseId",
                table: "GovernanceDocuments");

            migrationBuilder.DropColumn(
                name: "AnalysisNextAttemptAt",
                table: "GovernanceDocuments");
        }
    }
}
