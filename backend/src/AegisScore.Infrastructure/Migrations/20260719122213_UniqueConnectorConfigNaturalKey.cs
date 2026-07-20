using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueConnectorConfigNaturalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O índice de FK em TenantId vira redundante: o composto abaixo é tenant-leading e cobre
            // o mesmo prefixo (o próprio EF o suprimiu do modelo). Mantê-lo só custaria escrita.
            migrationBuilder.DropIndex(
                name: "IX_Connectors_TenantId",
                table: "Connectors");

            // ---- Dedupe defensivo ANTES de subir o índice único ----
            // Bases de dev podem já conter conectores duplicados (mesmo TenantId+Provider+Capability),
            // inseridos enquanto o POST /tenants/connectors sempre INSERIA — e o CREATE UNIQUE INDEX
            // falharia sobre eles. Mesmo padrão do UniqueGovernanceDocumentSha256, com duas diferenças:
            //
            //  (a) o sobrevivente é o MAIS RECENTE, não o mais antigo. Documentos duplicados têm conteúdo
            //      idêntico (dedupe por hash), então tanto faz; conectores duplicados NÃO — cada um pode
            //      carregar credenciais diferentes, e a última configuração é a intenção vigente do operador.
            //  (b) Signals.ConnectorConfigId NÃO tem FK, então apagar o perdedor orfanaria os sinais dele
            //      em silêncio. Repontamos a evidência para o sobrevivente ANTES do DELETE — histórico de
            //      coleta é dado de auditoria e não pode evaporar num ajuste de índice.
            //
            // No-op numa base já limpa.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           FIRST_VALUE(""Id"") OVER (
                               PARTITION BY ""TenantId"", ""Provider"", ""Capability""
                               ORDER BY COALESCE(""UpdatedAt"", ""CreatedAt"") DESC, ""Id"" DESC
                           ) AS keeper
                    FROM ""Connectors""
                )
                UPDATE ""Signals"" s
                SET ""ConnectorConfigId"" = r.keeper
                FROM ranked r
                WHERE s.""ConnectorConfigId"" = r.""Id"" AND r.""Id"" <> r.keeper;");

            migrationBuilder.Sql(@"
                DELETE FROM ""Connectors"" c
                USING (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (
                               PARTITION BY ""TenantId"", ""Provider"", ""Capability""
                               ORDER BY COALESCE(""UpdatedAt"", ""CreatedAt"") DESC, ""Id"" DESC
                           ) AS rn
                    FROM ""Connectors""
                ) dup
                WHERE c.""Id"" = dup.""Id"" AND dup.rn > 1;");

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_TenantId_Provider_Capability",
                table: "Connectors",
                columns: new[] { "TenantId", "Provider", "Capability" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Connectors_TenantId_Provider_Capability",
                table: "Connectors");

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_TenantId",
                table: "Connectors",
                column: "TenantId");
        }
    }
}
