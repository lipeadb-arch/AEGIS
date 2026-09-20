using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] Contexto de avaliação dos controles de CONFIGURAÇÃO
// ============================================================================
// Os indicadores originais leem fatos agregados (contagens, razões, flags). Os controles de configuração leem o
// que a coleta observou — documentos tipados do locatário, políticas, papéis — e produzem, além do veredito, os
// objetos que o sustentam: AFETADOS (entram na contagem) e EVIDÊNCIAS de configuração ("onde foi encontrado",
// inclusive quando aprovado). É o mesmo motor e a mesma fórmula: o contexto só amplia o que a regra enxerga.

/// <summary>Tudo o que uma regra de configuração pode ler de UMA coleta — sempre a coleta relida do ADM.</summary>
public sealed class KnightEvaluationContext
{
    public KnightEvaluationContext(
        KnightFactSet facts,
        IReadOnlyList<KnightCapabilityStatus> capabilities,
        KnightDirectoryConfiguration? directory,
        KnightTenantConfiguration configuration,
        IReadOnlyList<KnightAffectedObjectEvidence> objectSets,
        DateTimeOffset collectedAt)
    {
        Facts = facts ?? KnightFactSet.Empty;
        Capabilities = capabilities ?? Array.Empty<KnightCapabilityStatus>();
        Directory = directory;
        Configuration = configuration ?? KnightTenantConfiguration.Empty;
        ObjectSets = objectSets ?? Array.Empty<KnightAffectedObjectEvidence>();
        CollectedAt = collectedAt;
    }

    public KnightFactSet Facts { get; }
    public IReadOnlyList<KnightCapabilityStatus> Capabilities { get; }
    public KnightDirectoryConfiguration? Directory { get; }
    public KnightTenantConfiguration Configuration { get; }
    public IReadOnlyList<KnightAffectedObjectEvidence> ObjectSets { get; }
    public DateTimeOffset CollectedAt { get; }

    /// <summary>Contexto só com fatos agregados: todo controle de configuração fica não avaliado, com o motivo.</summary>
    public static KnightEvaluationContext FromFacts(KnightFactSet facts) =>
        new(facts, Array.Empty<KnightCapabilityStatus>(), null, KnightTenantConfiguration.Empty,
            Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);

    public static KnightEvaluationContext From(KnightCollectionResult result) =>
        new(result.Facts, result.Capabilities, result.DirectoryConfiguration, result.TenantConfigurationOrEmpty,
            result.AffectedObjectSets, result.CollectedAt);

    /// <summary>Desfecho de uma capacidade nesta coleta, ou <c>null</c> quando ela não foi tentada.</summary>
    public KnightCapabilityStatus? Capability(KnightCapability capability) =>
        Capabilities.FirstOrDefault(c => c.Capability == capability);

    /// <summary>Nome observado de um objeto privilegiado desta coleta (para listar exceções por nome, nunca por suposição).</summary>
    public string MemberLabel(string id)
    {
        foreach (var set in ObjectSets.Where(s => s.Signal == KnightSignalKey.PrivilegedAccountsTotal))
            foreach (var o in set.Objects)
                if (string.Equals(o.ExternalId, id, StringComparison.OrdinalIgnoreCase))
                    return o.DisplayName ?? o.UserPrincipalName ?? id;
        return id;
    }
}

/// <summary>
/// Desfecho de um controle de configuração: o veredito e os objetos que o sustentam. A contagem de afetados é a
/// da lista de afetados — nunca um número que a lista contradiga.
/// </summary>
public sealed record KnightControlOutcome(
    KnightIndicatorStatus Status,
    string Evidence,
    string? NotEvaluatedReason,
    IReadOnlyList<KnightIndicatorObject> Affected,
    IReadOnlyList<KnightIndicatorObject> EvidenceObjects,
    bool AffectedComplete = true,
    string? Limitation = null,
    int? AffectedCountOverride = null)
{
    public int AffectedObjectCount => AffectedCountOverride ?? Affected.Count;

    public static KnightControlOutcome NotEvaluated(string reason, IReadOnlyList<KnightIndicatorObject>? evidence = null) =>
        new(KnightIndicatorStatus.NotEvaluated, $"Não avaliado: {reason}", reason,
            Array.Empty<KnightIndicatorObject>(), evidence ?? Array.Empty<KnightIndicatorObject>());

    public static KnightControlOutcome NotApplicable(string reason, IReadOnlyList<KnightIndicatorObject>? evidence = null) =>
        new(KnightIndicatorStatus.NotApplicable, $"Não se aplica: {reason}", reason,
            Array.Empty<KnightIndicatorObject>(), evidence ?? Array.Empty<KnightIndicatorObject>());

    public static KnightControlOutcome Passed(string evidence, IReadOnlyList<KnightIndicatorObject>? evidenceObjects = null) =>
        new(KnightIndicatorStatus.Passed, evidence, null, Array.Empty<KnightIndicatorObject>(),
            evidenceObjects ?? Array.Empty<KnightIndicatorObject>());

    public static KnightControlOutcome Exposed(
        string evidence, IReadOnlyList<KnightIndicatorObject> affected, IReadOnlyList<KnightIndicatorObject>? evidenceObjects = null,
        bool complete = true, string? limitation = null, int? affectedCount = null) =>
        new(KnightIndicatorStatus.Exposed, evidence, null, affected, evidenceObjects ?? Array.Empty<KnightIndicatorObject>(),
            complete, limitation, affectedCount);
}

/// <summary>Fábricas dos objetos preservados por controles de configuração (texto observado × esperado).</summary>
public static class KnightObjects
{
    /// <summary>Configuração de locatário observada como evidência: o valor encontrado e o esperado.</summary>
    public static KnightIndicatorObject Setting(string externalId, string displayName, string found, string expected, string? detail = null) =>
        new(KnightObjectRelation.Evidence, KnightAffectedObjectKind.TenantSetting, externalId, displayName, null,
            Array.Empty<string>(), detail ?? $"Encontrado: {found}. Esperado: {expected}.", $"{displayName}: {found}");

    /// <summary>A mesma configuração, como objeto AFETADO (a configuração em si é o que precisa mudar).</summary>
    public static KnightIndicatorObject AffectedSetting(string externalId, string displayName, string found, string expected, string? detail = null) =>
        Setting(externalId, displayName, found, expected, detail) with { Relation = KnightObjectRelation.Affected };

    public static KnightIndicatorObject Affected(
        KnightAffectedObjectKind kind, string externalId, string? displayName, string? upn, string detail,
        IReadOnlyList<string>? roles = null, string? observed = null) =>
        new(KnightObjectRelation.Affected, kind, externalId, displayName, upn, roles ?? Array.Empty<string>(), detail, observed);

    public static KnightIndicatorObject Evidence(
        KnightAffectedObjectKind kind, string externalId, string? displayName, string detail, string? observed = null) =>
        new(KnightObjectRelation.Evidence, kind, externalId, displayName, null, Array.Empty<string>(), detail, observed);

    public static string YesNo(bool? value) => value switch { true => "sim", false => "não", _ => "não informado pela fonte" };

    public static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
