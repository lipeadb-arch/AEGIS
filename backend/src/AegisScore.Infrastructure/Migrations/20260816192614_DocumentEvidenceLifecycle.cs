using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// Rastreabilidade da evidência documental + REPARO idempotente da homologação, AUTOCONTIDO.
    ///
    /// Adiciona a persistência mínima da reconciliação determinística — trecho literal por mapping
    /// (<c>EvidenceQuote</c>) e origem documental por estado do ledger (<c>OriginDocumentId</c>, com FK
    /// Restrict + índice para nunca apontar a documento inexistente) — e, no MESMO passo, invalida os
    /// derivados documentais LEGADOS sem prova literal. É o que faz o estado órfão de Govern (ex.: 40% sem
    /// documento válido) desaparecer no próximo deploy, sem SQL manual no banco.
    ///
    /// O SQL de reparo é INLINE nesta migration DE PROPÓSITO: uma migration histórica é imutável e não
    /// pode depender de uma classe de runtime que mude no futuro. Aplicado pelo <c>AegisScore.DbMigrator</c>
    /// (PostgreSQL). Os testes de LÓGICA equivalente rodam em SQLite (reconciliação); a TRANSIÇÃO real desta
    /// migration sobre banco legado é validada por <c>DocumentEvidenceRepairMigrationTests</c> (PostgreSQL).
    ///
    /// Enums como int (ordinal do Npgsql): VerdictSource {Documentary=0, Telemetry=1};
    /// CoverageEvidenceSource {None=0, Document=1, Interview=2, Both=3};
    /// AiAnalysisStatus {Pending=0, Queued=1, Processing=2, Analyzed=3, Failed=4}.
    /// </summary>
    public partial class DocumentEvidenceLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Persistência mínima da reconciliação determinística.
            migrationBuilder.AddColumn<Guid>(
                name: "OriginDocumentId",
                table: "TenantControlStates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceQuote",
                table: "DocumentControlMappings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantControlStates_OriginDocumentId",
                table: "TenantControlStates",
                column: "OriginDocumentId");

            // FK Restrict: OriginDocumentId nunca aponta para documento inexistente. A coluna nasce toda
            // NULL e o reparo abaixo não a preenche, então a FK é trivialmente satisfeita na aplicação.
            migrationBuilder.AddForeignKey(
                name: "FK_TenantControlStates_GovernanceDocuments_OriginDocumentId",
                table: "TenantControlStates",
                column: "OriginDocumentId",
                principalTable: "GovernanceDocuments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // 2) REPARO idempotente (determinístico e seguro em banco vazio — os WHERE não casam nada). Como
            //    EvidenceQuote nasce NULL, "NÃO EXISTS mapping com trecho literal" é verdadeiro em toda linha
            //    legada, então esta 1ª aplicação limpa TODO derivado documental sem prova.

            // (a) LEDGER — retrai o estado DOCUMENTAL legado sem prova literal verificável. TELEMETRIA
            //     (LastVerdictSource=1) é preservada integralmente.
            migrationBuilder.Sql(@"
DELETE FROM ""TenantControlStates"" ts
WHERE ts.""LastVerdictSource"" = 0
  AND NOT EXISTS (
      SELECT 1
        FROM ""DocumentControlMappings"" m
        JOIN ""Subcategories"" s ON s.""Code"" = m.""SubcategoryCode""
       WHERE s.""Id"" = ts.""SubcategoryId""
         AND m.""TenantId"" = ts.""TenantId""
         AND m.""EvidenceQuote"" IS NOT NULL
  );");

            // (b) COBERTURA exclusivamente documental (Document=1) sem prova literal → DESAPARECE.
            migrationBuilder.Sql(@"
DELETE FROM ""SubcategoryCoverages"" c
WHERE c.""EvidenceSource"" = 1
  AND NOT EXISTS (
      SELECT 1 FROM ""DocumentControlMappings"" m
       WHERE m.""TenantId"" = c.""TenantId""
         AND m.""SubcategoryCode"" = c.""SubcategoryCode""
         AND m.""EvidenceQuote"" IS NOT NULL
  );");

            // (c) COBERTURA Both (3) sem prova literal → volta para Interview (2); a origem documental é
            //     zerada, mas a evidência de ENTREVISTA é preservada (não se apaga a linha).
            migrationBuilder.Sql(@"
UPDATE ""SubcategoryCoverages"" c
   SET ""EvidenceSource"" = 2,
       ""OriginDocumentId"" = NULL
 WHERE c.""EvidenceSource"" = 3
   AND NOT EXISTS (
       SELECT 1 FROM ""DocumentControlMappings"" m
        WHERE m.""TenantId"" = c.""TenantId""
          AND m.""SubcategoryCode"" = c.""SubcategoryCode""
          AND m.""EvidenceQuote"" IS NOT NULL
   );");

            // (d) Remove os DocumentControlMappings LEGADOS sem trecho verificável (EvidenceQuote NULL) —
            //     eles não sustentam controle nem cobertura e não podem seguir exibidos como se provassem.
            //     A reanálise recria, com trecho literal, os que realmente tiverem prova.
            migrationBuilder.Sql(@"
DELETE FROM ""DocumentControlMappings""
WHERE ""EvidenceQuote"" IS NULL;");

            // (e) RE-ENFILEIRA documentos com binário (StorageUri) para reanálise pelo novo pipeline e LIMPA
            //     a conclusão legada (resumo/analisado em/modelo/erro) para nada probatório antigo aparecer
            //     enquanto aguardam. Só toca linhas existentes — o documento sintético já excluído não volta.
            migrationBuilder.Sql(@"
UPDATE ""GovernanceDocuments""
   SET ""AnalysisStatus"" = 1,
       ""AnalysisQueuedAt"" = now(),
       ""AnalyzedAt"" = NULL,
       ""AnalysisSummary"" = NULL,
       ""ModelUsed"" = NULL,
       ""AnalysisError"" = NULL,
       ""AnalysisLeaseId"" = NULL,
       ""AnalysisLeaseExpiresAt"" = NULL,
       ""AnalysisAttempts"" = 0,
       ""AnalysisNextAttemptAt"" = NULL
 WHERE ""StorageUri"" IS NOT NULL;");

            // (f) Documentos SEM binário não podem reanalisar nem seguir exibindo conclusão probatória
            //     antiga: voltam a Pending (0) e têm a conclusão legada limpa. Seus mappings já saíram em (d).
            migrationBuilder.Sql(@"
UPDATE ""GovernanceDocuments""
   SET ""AnalysisStatus"" = 0,
       ""AnalyzedAt"" = NULL,
       ""AnalysisSummary"" = NULL,
       ""ModelUsed"" = NULL,
       ""AnalysisError"" = NULL,
       ""AnalysisLeaseId"" = NULL,
       ""AnalysisLeaseExpiresAt"" = NULL,
       ""AnalysisAttempts"" = 0,
       ""AnalysisNextAttemptAt"" = NULL
 WHERE ""StorageUri"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // O REPARO de dados (retração de estados/cobertura/mappings legados) é IRREVERSÍVEL por natureza
            // — não há como recriar uma prova que nunca existiu. O Down reverte apenas o schema (FK, índice
            // e as duas colunas); os derivados retraídos permanecem retraídos, o que é seguro e correto.
            migrationBuilder.DropForeignKey(
                name: "FK_TenantControlStates_GovernanceDocuments_OriginDocumentId",
                table: "TenantControlStates");

            migrationBuilder.DropIndex(
                name: "IX_TenantControlStates_OriginDocumentId",
                table: "TenantControlStates");

            migrationBuilder.DropColumn(
                name: "OriginDocumentId",
                table: "TenantControlStates");

            migrationBuilder.DropColumn(
                name: "EvidenceQuote",
                table: "DocumentControlMappings");
        }
    }
}
