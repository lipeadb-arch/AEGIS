namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// SQL do REPARO idempotente da evidência documental legada, aplicado pela migration
/// <c>DocumentEvidenceLifecycle</c> no PostgreSQL. Extraído para constantes para que a migration e o teste
/// focado exercitem EXATAMENTE o mesmo SQL — sem risco de o teste validar uma cópia divergente.
///
/// PostgreSQL-específico (alias em DELETE, <c>now()</c>): não roda sobre SQLite. A MESMA lógica é coberta
/// em C# (SQLite) pela reconciliação; este SQL é a versão em massa, one-shot, do mesmo invariante.
/// Enums como int (ordinal Npgsql): VerdictSource {Documentary=0, Telemetry=1};
/// CoverageEvidenceSource {None=0, Document=1, Interview=2, Both=3}; AiAnalysisStatus {Queued=1}.
/// </summary>
public static class DocumentEvidenceRepair
{
    /// <summary>(a) Retrai o estado DOCUMENTAL sem prova literal; telemetria preservada.</summary>
    public const string RetractLegacyDocumentaryLedger = @"
DELETE FROM ""TenantControlStates"" ts
WHERE ts.""LastVerdictSource"" = 0
  AND NOT EXISTS (
      SELECT 1
        FROM ""DocumentControlMappings"" m
        JOIN ""Subcategories"" s ON s.""Code"" = m.""SubcategoryCode""
       WHERE s.""Id"" = ts.""SubcategoryId""
         AND m.""TenantId"" = ts.""TenantId""
         AND m.""EvidenceQuote"" IS NOT NULL
  );";

    /// <summary>(b) Cobertura exclusivamente documental sem prova literal → desaparece.</summary>
    public const string DropOrphanDocumentaryCoverage = @"
DELETE FROM ""SubcategoryCoverages"" c
WHERE c.""EvidenceSource"" = 1
  AND NOT EXISTS (
      SELECT 1 FROM ""DocumentControlMappings"" m
       WHERE m.""TenantId"" = c.""TenantId""
         AND m.""SubcategoryCode"" = c.""SubcategoryCode""
         AND m.""EvidenceQuote"" IS NOT NULL
  );";

    /// <summary>(c) Cobertura Both sem prova literal → volta para Interview; entrevista preservada.</summary>
    public const string DemoteOrphanBothCoverageToInterview = @"
UPDATE ""SubcategoryCoverages"" c
   SET ""EvidenceSource"" = 2,
       ""OriginDocumentId"" = NULL
 WHERE c.""EvidenceSource"" = 3
   AND NOT EXISTS (
       SELECT 1 FROM ""DocumentControlMappings"" m
        WHERE m.""TenantId"" = c.""TenantId""
          AND m.""SubcategoryCode"" = c.""SubcategoryCode""
          AND m.""EvidenceQuote"" IS NOT NULL
   );";

    /// <summary>(d) Re-enfileira documentos EXISTENTES com binário para reanálise pelo novo pipeline.</summary>
    public const string RequeueExistingDocumentsWithBinary = @"
UPDATE ""GovernanceDocuments""
   SET ""AnalysisStatus"" = 1,
       ""AnalysisQueuedAt"" = now(),
       ""AnalysisLeaseId"" = NULL,
       ""AnalysisLeaseExpiresAt"" = NULL,
       ""AnalysisAttempts"" = 0,
       ""AnalysisNextAttemptAt"" = NULL,
       ""AnalysisError"" = NULL
 WHERE ""StorageUri"" IS NOT NULL;";

    /// <summary>As quatro etapas, na ordem de aplicação — usada pela migration e pelo teste focado.</summary>
    public static readonly string[] Statements =
    {
        RetractLegacyDocumentaryLedger,
        DropOrphanDocumentaryCoverage,
        DemoteOrphanBothCoverageToInterview,
        RequeueExistingDocumentsWithBinary,
    };
}
