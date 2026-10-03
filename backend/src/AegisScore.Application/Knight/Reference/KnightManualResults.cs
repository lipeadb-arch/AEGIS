using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Remediation;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Reference;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Resultado manual estruturado dos controles de referência
// ============================================================================
// Só recebe resultado manual a referência que o KNIGHT NÃO avalia de forma automatizada — critério organizacional,
// ausência comprovada de método publicado ou acesso que o conector não tem. A evidência usa o fluxo já existente: a mesma
// referência de evidência dos planos de ação (chamado, documento, registro) ou um documento da Central de Governança.
// O resultado manual nunca entra na cobertura automatizada, na nota KNIGHT nem na aprovação: é sempre exibido à parte,
// identificado como verificação manual (atestação).

/// <summary>Um resultado manual como a tela e as exportações o mostram.</summary>
public sealed record KnightManualResultView(
    Guid Id,
    string ReferenceKey,
    KnightManualResult Result,
    string ResultLabel,
    string Justification,
    string ResponsibleName,
    string? EvidenceReference,
    Guid? EvidenceDocumentId,
    string? EvidenceDocumentTitle,
    string? EvidenceDocumentSha256,
    DateOnly? ValidUntil,
    bool Expired,
    string ReferenceDisposition,
    string CatalogVersion,
    string RecordedByName,
    DateTimeOffset RecordedAt);

/// <summary>Pedido de registro. O autor vem do token, nunca do corpo.</summary>
public sealed record RecordKnightManualResultCommand(
    string ReferenceKey,
    string Result,
    string Justification,
    string ResponsibleName,
    string? EvidenceReference,
    Guid? EvidenceDocumentId,
    DateOnly? ValidUntil);

/// <summary>Pedido recusado pela validação — a mensagem diz o que corrigir.</summary>
public sealed class KnightManualResultValidationException : Exception
{
    public KnightManualResultValidationException(string message) : base(message) { }
}

public interface IKnightManualResultService
{
    /// <summary>Resultado VIGENTE de cada referência (o mais recente; retirados não aparecem).</summary>
    Task<IReadOnlyList<KnightManualResultView>> CurrentAsync(CancellationToken ct = default);

    /// <summary>Histórico completo de uma referência, do mais recente ao mais antigo.</summary>
    Task<IReadOnlyList<KnightManualResultView>> HistoryAsync(string referenceKey, CancellationToken ct = default);

    Task<KnightManualResultView> RecordAsync(RecordKnightManualResultCommand command, RemediationActor actor, CancellationToken ct = default);
}

public static class KnightManualResults
{
    /// <summary>Disposições que aceitam resultado manual (não há avaliação automatizada da referência).</summary>
    public static bool Eligible(KnightReferenceDisposition d) =>
        d is KnightReferenceDisposition.ManualOnly or KnightReferenceDisposition.ApiLimitation or KnightReferenceDisposition.RequiresAccess;

    public static string ResultLabel(KnightManualResult r) => r switch
    {
        KnightManualResult.Compliant => "Conforme (verificação manual)",
        KnightManualResult.NonCompliant => "Não conforme (verificação manual)",
        KnightManualResult.NotApplicable => "Não se aplica (verificação manual)",
        KnightManualResult.Withdrawn => "Resultado retirado",
        _ => r.ToString(),
    };

    public static bool TryParse(string? text, out KnightManualResult result)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "compliant": case "conforme": result = KnightManualResult.Compliant; return true;
            case "noncompliant": case "naoconforme": case "não conforme": case "nao conforme": result = KnightManualResult.NonCompliant; return true;
            case "notapplicable": case "naoseaplica": case "não se aplica": result = KnightManualResult.NotApplicable; return true;
            case "withdrawn": case "retirado": result = KnightManualResult.Withdrawn; return true;
            default: result = default; return false;
        }
    }

    public static bool IsExpired(DateOnly? validUntil, DateTimeOffset now) =>
        validUntil is { } d && d < DateOnly.FromDateTime(now.UtcDateTime);
}
