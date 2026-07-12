using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueGovernanceDocumentSha256 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GovernanceDocuments_TenantId_Sha256",
                table: "GovernanceDocuments");

            // Dedupe defensivo ANTES de subir o índice único: bases de dev podem já conter documentos
            // duplicados (mesmo TenantId+Sha256) inseridos enquanto o índice era não-único — e o
            // CREATE UNIQUE INDEX falharia sobre eles. Mantém a linha mais ANTIGA de cada grupo
            // (ORDER BY CreatedAt, Id) e remove as demais; a FK de ControlMappings é ON DELETE CASCADE,
            // então os mapeamentos das linhas descartadas somem junto. No-op numa base já limpa.
            migrationBuilder.Sql(@"
                DELETE FROM ""GovernanceDocuments"" g
                USING (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (
                               PARTITION BY ""TenantId"", ""Sha256""
                               ORDER BY ""CreatedAt"", ""Id""
                           ) AS rn
                    FROM ""GovernanceDocuments""
                    WHERE ""Sha256"" IS NOT NULL
                ) dup
                WHERE g.""Id"" = dup.""Id"" AND dup.rn > 1;");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceDocuments_TenantId_Sha256",
                table: "GovernanceDocuments",
                columns: new[] { "TenantId", "Sha256" },
                unique: true,
                filter: "\"Sha256\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GovernanceDocuments_TenantId_Sha256",
                table: "GovernanceDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceDocuments_TenantId_Sha256",
                table: "GovernanceDocuments",
                columns: new[] { "TenantId", "Sha256" });
        }
    }
}
