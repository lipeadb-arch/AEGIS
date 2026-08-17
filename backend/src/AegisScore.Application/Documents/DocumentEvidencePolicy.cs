namespace AegisScore.Application.Documents;

/// <summary>
/// Política ÚNICA da evidência documental — o número que decide o que a evidência pode fazer vive AQUI,
/// não espalhado por worker/reconciler/migration.
/// </summary>
public static class DocumentEvidencePolicy
{
    /// <summary>
    /// Confiança MÍNIMA para a evidência documental probatória (a) marcar cobertura como <c>Coberto</c> e
    /// (b) alterar o <c>TenantControlState</c>/Aegis Score (crédito parcial de 50%). Abaixo deste limiar a
    /// evidência ainda vale para RASTREABILIDADE (o mapping com trecho literal existe) e pode gerar
    /// cobertura <c>Parcial</c>, mas NÃO cria nem preserva crédito no score. Telemetria segue autoritativa.
    /// </summary>
    public const double MinConfidenceForScore = 0.70;
}
