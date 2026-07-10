using System;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Veredito de conformidade estruturado que a IA produz para uma subcategoria NIST, já traduzido em
/// pontos do Aegis Score (<paramref name="AwardedScore"/> de 0..<paramref name="MaxScorePoints"/>).
/// </summary>
public record ComplianceVerdict(ControlStatus Status, string AiEvidence, int AwardedScore, int MaxScorePoints);

/// <summary>
/// Motor de avaliação de conformidade do Aegis Score — a PORTA (a implementação vive na Infrastructure
/// porque persiste no AegisScoreDbContext, seguindo a Clean Architecture do projeto). Ingere telemetria
/// bruta de uma ferramenta de segurança, pede um veredito NIST CSF 2.0 a um LLM e faz o upsert do
/// <see cref="TenantControlState"/> da subcategoria alvo.
/// </summary>
public interface IAegisAiEvaluatorService
{
    /// <summary>
    /// Avalia UMA subcategoria para UM tenant a partir de um payload de telemetria cru (log/JSON de
    /// Microsoft Sentinel, CrowdStrike, Defender…), persiste o veredito e devolve o resultado.
    /// </summary>
    /// <param name="tenantId">Tenant alvo — precisa casar com o tenant ambiente (defesa em profundidade).</param>
    /// <param name="subcategoryCode">Código NIST CSF 2.0, ex.: "PR.AA-01".</param>
    /// <param name="rawTelemetryPayload">Saída crua da ferramenta (tratada como dado NÃO confiável).</param>
    Task<ComplianceVerdict> EvaluateAsync(
        Guid tenantId, string subcategoryCode, string rawTelemetryPayload, CancellationToken ct = default);
}
