namespace AegisScore.Application.Services;

/// <summary>
/// Rotina ÚNICA de reconciliação da evidência documental de um tenant, sobre um conjunto de subcategorias
/// afetadas — usada TANTO pela exclusão de documento QUANTO pela reanálise. Recomputa, de forma
/// determinística e a partir da evidência PROBATÓRIA vigente (mappings com trecho literal), o ledger de
/// conformidade e a cobertura, sem deixar mapping/cobertura/estado órfãos.
///
/// <para>Invariantes que impõe, por subcategoria afetada:</para>
/// <list type="bullet">
/// <item>sem evidência documental elegível e estado vigente Documentary → retrai (volta a "não avaliado");</item>
/// <item>com outro documento válido → recalcula usando o documento vencedor;</item>
/// <item>estado vigente de telemetria → preservado integralmente;</item>
/// <item>cobertura exclusivamente documental sem documento válido → desaparece;</item>
/// <item>cobertura <c>Both</c> sem documento → volta para <c>Interview</c> (evidência de entrevista intacta).</item>
/// </list>
///
/// O chamador é responsável por CAPTURAR os códigos afetados antes de mutar (os do documento excluído; a
/// UNIÃO dos antigos e novos na reanálise) e por persistir a mutação ANTES de reconciliar.
/// </summary>
public interface IDocumentEvidenceReconciler
{
    Task ReconcileAsync(
        Guid tenantId, IReadOnlyCollection<string> affectedSubcategoryCodes, CancellationToken ct = default);
}
