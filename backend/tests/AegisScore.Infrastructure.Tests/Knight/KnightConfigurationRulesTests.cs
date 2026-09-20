using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Particularidades das regras de configuração que o fluxo feliz não exercita: valor
/// ausente, documento ilegível ou de versão desconhecida, coleta anterior à ampliação, capacidade que falhou,
/// critério não aplicável, exceções explícitas e lista truncada. Em nenhum desses casos a ausência aprova.
/// </summary>
public sealed class KnightConfigurationRulesTests
{
    private static KnightCapabilityStatus Cap(KnightCapability c, KnightCapabilityOutcome o = KnightCapabilityOutcome.Collected, string? d = null) => new(c, o, d);

    private static KnightEvaluationContext Ctx(
        IEnumerable<KnightConfigurationDocument> docs, IEnumerable<KnightCapabilityStatus> caps, KnightDirectoryConfiguration? directory = null)
    {
        var capList = caps.ToList();
        return new KnightEvaluationContext(KnightFactSet.Empty, capList, directory, new KnightTenantConfiguration(docs, capList),
            Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);
    }

    private static KnightControlOutcome Eval(string id, KnightEvaluationContext c) =>
        EntraConfigurationControls.Definitions.Single(d => d.Id == id).Evaluate!(c);

    private static KnightConfigurationDocument Auth(bool? createApps) => KnightTenantConfiguration.Document("authorizationPolicy", null,
        new EntraAuthorizationPolicyConfiguration(createApps, null, null, null, null, null, null, Array.Empty<string>(), null, null, null));

    [Fact]
    public void SemContextoDeColeta_NenhumControleDeConfiguracaoAprova()
    {
        var evaluated = KnightIndicatorEvaluator.Evaluate(KnightFactSet.Empty, KnightSourceType.MicrosoftEntraId);
        evaluated.Where(e => e.Definition.Evaluate is not null)
            .Should().HaveCount(EntraConfigurationControls.Definitions.Count)
            .And.OnlyContain(e => e.Status == KnightIndicatorStatus.NotEvaluated && e.NotEvaluatedReason != null);
    }

    [Fact]
    public void ValorAusente_NaoVira_DesabilitadoNemConforme()
    {
        var o = Eval("AK-ENTRA-016", Ctx(new[] { Auth(null) }, new[] { Cap(KnightCapability.AuthorizationPolicy) }));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("não informou");
    }

    [Fact]
    public void CapacidadeQueFalhou_DeclaraOMotivoDaColeta()
    {
        var o = Eval("AK-ENTRA-016", Ctx(Array.Empty<KnightConfigurationDocument>(),
            new[] { Cap(KnightCapability.AuthorizationPolicy, KnightCapabilityOutcome.InsufficientPermission, "Permissão insuficiente para esta coleta. HTTP 403") }));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("HTTP 403");
    }

    [Fact]
    public void ColetaAnteriorAAmpliacao_NaoAvalia_EDizPorque()
    {
        var o = Eval("AK-ENTRA-016", Ctx(Array.Empty<KnightConfigurationDocument>(), Array.Empty<KnightCapabilityStatus>()));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("não foi coletada nesta aquisição");
    }

    [Fact]
    public void DocumentoDeVersaoDesconhecida_NaoEhCompletadoPorSuposicao()
    {
        var doc = Auth(false) with { SchemaVersion = "aegis-config-entra-authorization-policy-v99" };
        var o = Eval("AK-ENTRA-016", Ctx(new[] { doc }, new[] { Cap(KnightCapability.AuthorizationPolicy) }));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("versão desconhecida");
    }

    [Fact]
    public void ReprovacaoDeConfiguracao_TrazEncontradoEEsperado_ComoEvidencia()
    {
        var o = Eval("AK-ENTRA-016", Ctx(new[] { Auth(true) }, new[] { Cap(KnightCapability.AuthorizationPolicy) }));
        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.AffectedObjectCount.Should().Be(0, "a configuração do locatário não é um objeto afetado contado");
        o.EvidenceObjects.Should().ContainSingle(e => e.Kind == KnightAffectedObjectKind.TenantSetting && e.Detail == "Encontrado: Sim. Esperado: Não.");
    }

    [Fact]
    public void DiretorioNaoHibrido_ControlesDeSincronizacao_NaoSeAplicam()
    {
        var sync = KnightTenantConfiguration.Document("directorySynchronization", null,
            new EntraDirectorySynchronizationConfiguration(false, null, null, null));
        var ctx = Ctx(new[] { sync }, new[] { Cap(KnightCapability.DirectorySynchronization), Cap(KnightCapability.DirectorySettings) });
        Eval("AK-ENTRA-039", ctx).Status.Should().Be(KnightIndicatorStatus.NotApplicable);
        Eval("AK-ENTRA-034", ctx).Status.Should().Be(KnightIndicatorStatus.NotApplicable);
    }

    [Fact]
    public void AuthenticatorDesabilitado_ControleDeContexto_NaoSeAplica()
    {
        var doc = KnightTenantConfiguration.Document("authenticationMethodsPolicy", null, new EntraAuthenticationMethodsConfiguration(
            "migrationComplete", null, new[] { new EntraAuthenticationMethodState("MicrosoftAuthenticator", "disabled", Array.Empty<string>(), Array.Empty<string>()) },
            null, null));
        Eval("AK-ENTRA-028", Ctx(new[] { doc }, new[] { Cap(KnightCapability.AuthenticationMethodsPolicy) }))
            .Status.Should().Be(KnightIndicatorStatus.NotApplicable);
    }

    [Fact]
    public void ValorPadraoDoModelo_EhDitoNaEvidencia()
    {
        var setting = KnightTenantConfiguration.Document(EntraDirectorySettingConfiguration.PasswordRuleTemplateId, null,
            new EntraDirectorySettingConfiguration(EntraDirectorySettingConfiguration.PasswordRuleTemplateId, "Password Rule Settings", false,
                new Dictionary<string, string?> { ["LockoutThreshold"] = "10" }, new[] { "LockoutThreshold" }));
        var o = Eval("AK-ENTRA-035", Ctx(new[] { setting }, new[] { Cap(KnightCapability.DirectorySettings) }));
        o.Status.Should().Be(KnightIndicatorStatus.Passed);
        o.EvidenceObjects.Single().Detail.Should().Contain("padrão do modelo");
    }

    [Fact]
    public void ListaTruncada_ContagemVemDoTotal_EALimitacaoEhDeclarada()
    {
        var listed = Enumerable.Range(0, 3).Select(i => new EntraApplicationCredentialSummary($"app-{i}", $"App {i}", 1, 1, 400, 0, null)).ToList();
        var inv = KnightTenantConfiguration.Document("applicationCredentials", null,
            new EntraApplicationCredentialInventory(5000, 2000, 180, listed, ListComplete: false, ApplicationsWithLongLivedCertificates: 1500));
        var o = Eval("AK-ENTRA-027", Ctx(new[] { inv }, new[] { Cap(KnightCapability.ApplicationInventory) }));
        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.AffectedObjectCount.Should().Be(1500);
        o.Affected.Should().HaveCount(3);
        o.AffectedComplete.Should().BeFalse();
        o.Limitation.Should().Contain("1500");
    }

    // ---- Acesso condicional: v1, exceções e grupos ---------------------------------------------------

    private static ConditionalAccessPolicyConfiguration Policy(
        string id, IReadOnlyList<string> includeUsers, IReadOnlyList<string>? excludeUsers = null, IReadOnlyList<string>? includeGroups = null,
        bool v2 = true, IReadOnlyList<string>? controls = null, string? transfer = null) =>
        new(id, id, ConditionalAccessPolicyState.Enabled, "enabled",
            includeUsers, excludeUsers ?? Array.Empty<string>(), includeGroups ?? Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), false, false,
            new[] { "All" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new[] { "all" },
            false, false, false, false, false, "OR", controls ?? new[] { "block" }, null, null,
            AuthenticationFlowsTransferMethods: transfer, SessionAndConditionsCaptured: v2);

    private static KnightEvaluationContext CaCtx(params ConditionalAccessPolicyConfiguration[] policies) =>
        Ctx(Array.Empty<KnightConfigurationDocument>(), new[] { Cap(KnightCapability.ConditionalAccessPolicies) },
            new KnightDirectoryConfiguration(policies, Array.Empty<DirectoryRoleConfiguration>()));

    [Fact]
    public void PoliticasColetadasNoFormatoAnterior_NaoAvaliamCriteriosDeSessaoOuFluxo()
    {
        var o = Eval("AK-ENTRA-066", CaCtx(Policy("p", new[] { "All" }, v2: false, transfer: "deviceCodeFlow")));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("coleta anterior");
    }

    [Fact]
    public void ExclusaoSemOutraCobertura_NaoAprovaNemReprova_ENomeiaAExcecao()
    {
        var o = Eval("AK-ENTRA-066", CaCtx(Policy("p", new[] { "All" }, excludeUsers: new[] { "u-emergencia" }, transfer: "deviceCodeFlow")));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("u-emergencia");
    }

    [Fact]
    public void PoliticaDirigidaAGrupo_NaoComprovaAlcanceDeTodos()
    {
        var o = Eval("AK-ENTRA-066", CaCtx(Policy("p", Array.Empty<string>(), includeGroups: new[] { "g1" }, transfer: "deviceCodeFlow")));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("grupo");
    }

    [Fact]
    public void PoliticaQueNaoBloqueia_NaoContaComoBloqueio()
    {
        var o = Eval("AK-ENTRA-066", CaCtx(Policy("p", new[] { "All" }, controls: new[] { "mfa" }, transfer: "deviceCodeFlow")));
        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.EvidenceObjects.Should().ContainSingle(e => e.Detail!.Contains("não bloqueia o acesso"));
    }
}
