using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Application.Identity;

/// <summary>
/// Estado da COLETA (dimensão distinta de "conector conectado" e de "controle avaliado"). Reflete os DADOS
/// ARMAZENADOS do último snapshot — não a saúde da última tentativa, exposta à parte em
/// <see cref="IdentityEvidenceProjection.LastAttemptState"/>.
/// </summary>
public enum IdentityEvidenceCollectionState
{
    /// <summary>Conector de identidade não configurado — não há fonte.</summary>
    NoConnector = 0,
    /// <summary>Conector desabilitado/desconectado — não coleta.</summary>
    Disabled = 1,
    /// <summary>Conector habilitado, mas sem credencial legível.</summary>
    MissingCredential = 2,
    /// <summary>Configurado, porém nunca coletou (só tentativas sem dado, ou nenhuma tentativa).</summary>
    NeverCollected = 3,
    /// <summary>Coleta válida e COMPLETA — os agregados são a verdade.</summary>
    Complete = 4,
    /// <summary>Coleta válida, porém PARCIAL — parte das capacidades faltou (permissão/indisponibilidade).</summary>
    Partial = 5,
}

/// <summary>
/// Estado da evidência de UM controle NIST à luz da telemetria de identidade — a TERCEIRA dimensão, separada
/// de "conector conectado" e "coleta bem-sucedida". Nunca inventa score: um controle só é avaliado quando há
/// mapping determinístico EXPLICITAMENTE autorizado pela regra ativa (nenhum nesta entrega para os controles
/// de identidade), e evidência coletada porém insuficiente é uma conclusão VÁLIDA — não uma aprovação.
/// </summary>
public enum IdentityControlEvidenceState
{
    /// <summary>Sem fonte de telemetria (conector não configurado/desabilitado/sem credencial).</summary>
    NoSource = 0,
    /// <summary>Há conector, mas nenhuma coleta produziu dado ainda.</summary>
    NeverCollected = 1,
    /// <summary>Telemetria COLETADA, porém insuficiente para avaliar o REQUISITO deste controle. Não é score, não é ausência.</summary>
    CollectedButInsufficient = 2,
    /// <summary>Controle efetivamente avaliado por mapping determinístico autorizado (reservado; nenhum de identidade nesta entrega).</summary>
    Evaluated = 3,
}

/// <summary>Estado por capacidade projetado para a UI (o que a fonte coletou ou por que faltou), sem PII.</summary>
public sealed record IdentityCapabilityView(KnightCapability Capability, KnightCapabilityOutcome Outcome, string? Detail);

/// <summary>
/// Evidência de identidade de UM controle NIST: o estado da evidência e uma explicação HONESTA. Quando
/// <see cref="State"/> é <see cref="IdentityControlEvidenceState.CollectedButInsufficient"/>, a mensagem
/// reconhece que existe telemetria coletada E explica por que ela não basta para o requisito do controle —
/// sem afirmar conformidade nem conceder pontos.
/// </summary>
public sealed record IdentityControlEvidence(
    string Code,
    string Title,
    IdentityControlEvidenceState State,
    string Explanation);

/// <summary>
/// Projeção CONSULTIVA e determinística da evidência de identidade para o dashboard/postura/relatórios. Separa
/// explicitamente TRÊS dimensões: (1) o estado do conector; (2) o estado da coleta (completa/parcial/nunca);
/// (3) o estado de evidência por controle NIST. NUNCA concede pontos, NUNCA cria EvidenceSignal, NUNCA decide
/// conformidade. Preserva a fonte e o horário reais do snapshot (freshness).
/// </summary>
public sealed record IdentityEvidenceProjection(
    IdentityEvidenceConnectorState ConnectorState,
    IdentityEvidenceCollectionState CollectionState,
    KnightSourceState LastAttemptState,
    bool IsDegraded,
    string Source,
    string? SchemaVersion,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastAttemptDetail,
    IReadOnlyList<IdentityCapabilityView> Capabilities,
    IReadOnlyList<IdentityControlEvidence> Controls,
    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Postura AGREGADA de risco de identidade do MESMO snapshot — sem uma
    /// segunda consulta ao Graph e sem PII. <c>null</c> quando o snapshot é v1 ou quando nunca houve coleta.
    /// CONSULTIVA: não altera score, não vira EvidenceSignal e NÃO promove nem rebaixa controle NIST.
    /// </summary>
    IdentityRiskPosture? IdentityRisk = null,
    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Postura AGREGADA de métodos de autenticação registrados. Conceito
    /// DISTINTO de risco: registro de MFA não é detecção, e detecção não é método de autenticação.
    /// </summary>
    IdentityAuthenticationPosture? AuthenticationPosture = null)
{
    /// <summary>
    /// Controles NIST de identidade que o dashboard reconhece — e a razão determinística pela qual a telemetria
    /// coletada do Entra ID é INSUFICIENTE para um veredito de cada um, à luz da regra ATIVA
    /// (aegis_assessment_rules.json). Estas razões são a prova textual de que nenhum mapping foi forçado.
    /// </summary>
    private static readonly IReadOnlyList<IdentityControlDescriptor> IdentityControls = new[]
    {
        // PR.AA-01 = ciclo de vida, titularidade, offboarding, contas órfãs e rotação de credenciais (AC-2(3),
        // IA-4/5/12). MFA, convidados e Conditional Access — o que o KNIGHT coleta — NÃO provam esse requisito.
        new IdentityControlDescriptor("PR.AA-01", "Identidades e credenciais gerenciadas",
            "A telemetria de identidade coletada (papéis privilegiados, cobertura de MFA, convidados, acesso condicional) " +
            "não cobre o requisito de PR.AA-01: ciclo de vida vinculado ao RH, titularidade das contas, latência de offboarding, " +
            "contas órfãs e rotação de credenciais de serviço. Permanece NÃO AVALIADO — telemetria presente, evidência insuficiente."),

        // PR.AA-03 = MFA IMPOSTA POR POLÍTICA + resistência a phishing (logs de sign-in) + autenticação legada
        // residual + sessão. O KNIGHT observa REGISTRO/capacidade de MFA e EXISTÊNCIA de política — não a imposição
        // efetiva por sign-in, nem a fração resistente a phishing, nem o volume de autenticação legada que a regra exige.
        new IdentityControlDescriptor("PR.AA-03", "Autenticação de identidades e ativos",
            "A telemetria coletada indica registro/capacidade de MFA e existência de políticas, mas NÃO a imposição efetiva por " +
            "política nos logs de sign-in, a fração de autenticações resistentes a phishing nem o volume residual de autenticação " +
            "legada que PR.AA-03 exige para um veredito. Permanece NÃO AVALIADO — telemetria presente, evidência insuficiente."),

        // GV.RR-01 = accountability executiva (RACI, atas de comitê, termos assinados) — MANUAL_AUDIT_REQUIRED.
        // Nenhuma telemetria de identidade — inclusive a QUANTIDADE de administradores — pode avaliá-lo.
        new IdentityControlDescriptor("GV.RR-01", "Papéis, responsabilidades e autoridades",
            "GV.RR-01 exige evidência documental/manual de responsabilidade executiva (matriz RACI, atas de comitê, termos de " +
            "responsabilidade). Nenhuma telemetria de identidade — nem a quantidade de administradores — avalia esse requisito. " +
            "Permanece NÃO AVALIADO por telemetria."),
    };

    private sealed record IdentityControlDescriptor(string Code, string Title, string InsufficientReason);

    /// <summary>
    /// Constrói a projeção a partir do estado do conector e do snapshot mais recente (ou null). PURA e
    /// determinística. Não concede score a nenhum controle — CollectedButInsufficient é o teto para os
    /// controles de identidade nesta entrega.
    /// </summary>
    public static IdentityEvidenceProjection Build(
        IdentityEvidenceConnectorState connectorState, IdentityEvidenceSnapshotView? snapshot)
    {
        var hasData = snapshot?.HasAnyData ?? false;
        var collectionState = ResolveCollectionState(connectorState, snapshot);

        // Evidência coletada (mesmo que o conector depois perca a credencial) → "coletado, porém insuficiente".
        // Sem qualquer dado → sem fonte (conector deficiente) ou nunca coletado (configurado, sem coleta ainda).
        var controlState = hasData
            ? IdentityControlEvidenceState.CollectedButInsufficient
            : connectorState == IdentityEvidenceConnectorState.Configured
                ? IdentityControlEvidenceState.NeverCollected
                : IdentityControlEvidenceState.NoSource;

        var controls = IdentityControls
            .Select(d => new IdentityControlEvidence(
                d.Code, d.Title, controlState,
                controlState == IdentityControlEvidenceState.CollectedButInsufficient
                    ? d.InsufficientReason
                    : ExplainAbsence(controlState, d.Title)))
            .ToList();

        var capabilities = (snapshot?.Capabilities ?? Array.Empty<KnightCapabilityStatus>())
            .Select(c => new IdentityCapabilityView(c.Capability, c.Outcome, c.Detail))
            .ToList();

        // Degradação: há evidência preservada, mas ou a última tentativa falhou, ou o conector já não está apto.
        var lastAttemptOk = snapshot?.LastAttemptState is KnightSourceState.Completed or KnightSourceState.PartialCollection;
        var isDegraded = hasData
            && (!lastAttemptOk || connectorState != IdentityEvidenceConnectorState.Configured);

        return new IdentityEvidenceProjection(
            connectorState,
            collectionState,
            snapshot?.LastAttemptState ?? KnightSourceState.NotConfigured,
            isDegraded,
            snapshot?.Source ?? "Microsoft Entra ID",
            snapshot?.SchemaVersion,
            snapshot?.LastCollectionAt,
            snapshot?.LastAttemptAt,
            snapshot?.LastAttemptDetail,
            capabilities,
            controls,
            snapshot?.IdentityRisk,
            snapshot?.AuthenticationPosture);
    }

    private static IdentityEvidenceCollectionState ResolveCollectionState(
        IdentityEvidenceConnectorState connectorState, IdentityEvidenceSnapshotView? snapshot)
    {
        // Dados armazenados PREVALECEM: uma evidência completa/parcial é preservada mesmo que o conector depois
        // perca a credencial ou seja desabilitado (a degradação atual aparece à parte em ConnectorState/IsDegraded).
        if (snapshot is not null)
        {
            if (snapshot.DataState == KnightSourceState.Completed) return IdentityEvidenceCollectionState.Complete;
            if (snapshot.DataState == KnightSourceState.PartialCollection) return IdentityEvidenceCollectionState.Partial;
        }

        // Sem dado preservado: o estado da coleta espelha a deficiência do conector, ou "nunca coletado".
        return connectorState switch
        {
            IdentityEvidenceConnectorState.NotConfigured => IdentityEvidenceCollectionState.NoConnector,
            IdentityEvidenceConnectorState.Disabled => IdentityEvidenceCollectionState.Disabled,
            IdentityEvidenceConnectorState.MissingCredential => IdentityEvidenceCollectionState.MissingCredential,
            _ => IdentityEvidenceCollectionState.NeverCollected,
        };
    }

    private static string ExplainAbsence(IdentityControlEvidenceState state, string title) => state switch
    {
        IdentityControlEvidenceState.NoSource =>
            $"Sem fonte de telemetria de identidade conectada — {title} não pode ser avaliado por telemetria.",
        IdentityControlEvidenceState.NeverCollected =>
            $"Conector configurado, mas nenhuma coleta produziu evidência ainda — {title} permanece não avaliado.",
        _ => $"{title} permanece não avaliado.",
    };
}
