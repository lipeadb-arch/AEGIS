using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Testes PUROS da projeção NIST da evidência de identidade. Provam que a
/// projeção separa conector × coleta × controle, que NENHUM controle de identidade recebe veredito/score, e
/// que o dashboard distingue "sem fonte" de "coletado, porém insuficiente". GV.RR-01 e PR.AA-01/03 permanecem
/// não avaliados por telemetria — com a razão determinística ancorada na regra ativa.
/// </summary>
public sealed class IdentityEvidenceProjectionTests
{
    private static IdentityEvidenceSnapshotView CompleteView() => new(
        Guid.NewGuid(), Guid.NewGuid(), KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID",
        "aegis-identity-evidence-v1", KnightSourceState.Completed, KnightSourceState.Completed,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
        new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 12),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 3),
            KnightObservation.OfFlag(KnightSignalKey.AdminMfaPolicyEnforced, true),
        },
        new[] { new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected) });

    private static IdentityControlEvidence Control(IdentityEvidenceProjection p, string code) =>
        p.Controls.Single(c => c.Code == code);

    [Fact]
    public void Build_NoConnector_AllControlsNoSource()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.NotConfigured, null);

        p.CollectionState.Should().Be(IdentityEvidenceCollectionState.NoConnector);
        p.Controls.Should().OnlyContain(c => c.State == IdentityControlEvidenceState.NoSource);
        p.Controls.Should().NotBeEmpty();
    }

    [Fact]
    public void Build_ConfiguredButNeverCollected_ControlsNeverCollected()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.Configured, null);

        p.CollectionState.Should().Be(IdentityEvidenceCollectionState.NeverCollected);
        p.Controls.Should().OnlyContain(c => c.State == IdentityControlEvidenceState.NeverCollected);
    }

    [Fact]
    public void Build_WithCompleteData_ControlsCollectedButInsufficient_NeverEvaluated()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.Configured, CompleteView());

        p.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete);
        p.Controls.Should().OnlyContain(c => c.State == IdentityControlEvidenceState.CollectedButInsufficient);
        p.Controls.Should().NotContain(c => c.State == IdentityControlEvidenceState.Evaluated,
            "nenhum mapping de score foi autorizado para telemetria de identidade nesta fundação");
    }

    // ---- 9) GV.RR-01 não é avaliado por quantidade de administradores -------------------------------

    [Fact]
    public void GvRr01_IsNeverEvaluatedByTelemetry_ReasonCitesManualEvidence()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.Configured, CompleteView());
        var gvRr = Control(p, "GV.RR-01");

        gvRr.State.Should().Be(IdentityControlEvidenceState.CollectedButInsufficient);
        gvRr.State.Should().NotBe(IdentityControlEvidenceState.Evaluated);
        gvRr.Explanation.Should().ContainAny("documental", "manual", "RACI", "administradores",
            "a razão deixa explícito que quantidade de administradores não avalia GV.RR-01");
    }

    // ---- 10) PR.AA-01 não é avaliado apenas por MFA/Conditional Access ------------------------------

    [Fact]
    public void PrAa01_IsNeverEvaluatedByMfaAlone_ReasonCitesLifecycle()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.Configured, CompleteView());
        var prAa01 = Control(p, "PR.AA-01");

        prAa01.State.Should().Be(IdentityControlEvidenceState.CollectedButInsufficient);
        prAa01.Explanation.Should().ContainAny("ciclo de vida", "offboarding", "órfã",
            "PR.AA-01 exige ciclo de vida/offboarding/órfãs, não MFA isolada");
    }

    [Fact]
    public void PrAa03_IsNeverEvaluated_ReasonCitesEnforcementEvidence()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.Configured, CompleteView());
        var prAa03 = Control(p, "PR.AA-03");

        prAa03.State.Should().Be(IdentityControlEvidenceState.CollectedButInsufficient);
        prAa03.Explanation.Should().ContainAny("imposição", "sign-in", "legada", "phishing");
    }

    // ---- 13) Dashboard distingue "sem fonte" de "coletado, mas insuficiente" -------------------------

    [Fact]
    public void Dashboard_DistinguishesNoSourceFromCollectedButInsufficient()
    {
        var noSource = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.NotConfigured, null);
        var collected = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.Configured, CompleteView());

        Control(noSource, "PR.AA-01").State.Should().Be(IdentityControlEvidenceState.NoSource);
        Control(collected, "PR.AA-01").State.Should().Be(IdentityControlEvidenceState.CollectedButInsufficient);
        noSource.CollectionState.Should().NotBe(collected.CollectionState);
    }

    // ---- Degradação: evidência preservada mesmo com conector depois sem credencial -------------------

    [Fact]
    public void Build_PreservedData_ButConnectorLostCredential_ShowsCollectedButDegraded()
    {
        var p = IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.MissingCredential, CompleteView());

        p.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete, "a evidência preservada prevalece");
        p.IsDegraded.Should().BeTrue("o conector atual perdeu a credencial — degradação sinalizada à parte");
        p.Controls.Should().OnlyContain(c => c.State == IdentityControlEvidenceState.CollectedButInsufficient);
    }
}
