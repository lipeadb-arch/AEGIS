using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnightCoverage02_TeamsSyncPerSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_KnightSyncRequest_ActivePerConnector",
                table: "KnightSyncRequests");

            migrationBuilder.CreateIndex(
                name: "UX_KnightSyncRequest_ActivePerConnectorSource",
                table: "KnightSyncRequests",
                columns: new[] { "TenantId", "ConnectorConfigId", "SourceType" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_KnightSyncRequest_ActivePerConnectorSource",
                table: "KnightSyncRequests");

            migrationBuilder.CreateIndex(
                name: "UX_KnightSyncRequest_ActivePerConnector",
                table: "KnightSyncRequests",
                columns: new[] { "TenantId", "ConnectorConfigId" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }
    }
}
