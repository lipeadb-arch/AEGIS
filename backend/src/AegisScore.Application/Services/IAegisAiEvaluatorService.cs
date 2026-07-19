using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Veredito de conformidade estruturado que a IA produz para uma subcategoria NIST, já traduzido em
/// pontos do Aegis Score (<paramref name="AwardedScore"/> de 0..<paramref name="MaxScorePoints"/>).
/// <see cref="Checks"/> é o CHECKLIST técnico que justifica o status (populado pelo motor, persistido com o
/// estado e expandido no card do HUD) — vazio quando o motor não decompõe o veredito.
/// </summary>
public record ComplianceVerdict(ControlStatus Status, string AiEvidence, int AwardedScore, int MaxScorePoints)
{
    public IReadOnlyList<ComplianceCheck> Checks { get; init; } = Array.Empty<ComplianceCheck>();

    /// <summary>
    /// Contexto de inteligência que o motor anexou ao veredito (severidade, rastro cru da ferramenta,
    /// plano de ação, confiança, ameaças, MTTD/MTTR). O avaliador por telemetria já pede este bloco ao LLM
    /// e o preenche; fica nulo quando o motor é determinístico (StubLlmClient de DEV) ou não o produz.
    /// </summary>
    public ControlIntelligence? Intelligence { get; init; }

    /// <summary>
    /// Lacunas de evidência por trás do veredito, discriminadas entre telemetria e documentação. Vazia
    /// quando o controle é conforme ou quando a fonte não as decompõe — o consumidor nunca recebe nulo.
    /// </summary>
    public IReadOnlyList<MissingRequirement> MissingRequirements { get; init; } = Array.Empty<MissingRequirement>();
}

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
