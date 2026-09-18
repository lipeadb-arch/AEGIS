using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Leitura determinística do acesso condicional e as regras v3 que a consomem.
/// Prova, caso a caso, o que a leitura por flags errava: política dirigida a PAPEL cobre o papel; somente
/// relatório e desabilitada não impõem; MFA como alternativa (OU) não é exigência; condição de localização
/// estreita a exigência; política por GRUPO não resolvido deixa o resultado inconclusivo; exclusões nominais são
/// exceções declaradas. E, nas regras: security defaults NÃO verificado nunca autoriza reprovar.
/// </summary>
public sealed class KnightConditionalAccessTests
{
    private const string GlobalAdmin = "62e90394-69f5-4237-9190-012177145e10";
    private const string SecurityAdmin = "194ae4cb-b126-40b2-bd5b-6091b380977d";

    private static DirectoryRoleConfiguration Role(string templateId, string name, params string[] users) =>
        new(templateId, name, users.Length, users);

    private static ConditionalAccessPolicyConfiguration Policy(
        string id,
        ConditionalAccessPolicyState state = ConditionalAccessPolicyState.Enabled,
        string[]? includeUsers = null, string[]? excludeUsers = null,
        string[]? includeRoles = null, string[]? excludeRoles = null,
        string[]? includeGroups = null, string[]? excludeGroups = null,
        string[]? apps = null, string[]? excludeApps = null, string[]? clients = null,
        bool location = false, string? op = null, string[]? controls = null, string? strength = null) =>
        new(id, "Política " + id, state, state.ToString(),
            includeUsers ?? Array.Empty<string>(), excludeUsers ?? Array.Empty<string>(),
            includeGroups ?? Array.Empty<string>(), excludeGroups ?? Array.Empty<string>(),
            includeRoles ?? Array.Empty<string>(), excludeRoles ?? Array.Empty<string>(),
            false, false,
            apps ?? new[] { "All" }, excludeApps ?? Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(),
            clients ?? Array.Empty<string>(),
            false, location, false, false, false,
            op, controls ?? new[] { "mfa" }, strength, strength is null ? null : "Phishing-resistant MFA");

    private static ConditionalAccessAnalysis Analyze(
        IReadOnlyList<ConditionalAccessPolicyConfiguration> policies, IReadOnlyList<DirectoryRoleConfiguration>? roles) =>
        ConditionalAccessAnalyzer.Analyze(new KnightDirectoryConfiguration(policies, roles))!;

    private static readonly DirectoryRoleConfiguration[] Roles =
    {
        Role(GlobalAdmin, "Global Administrator", "u-ga-1", "u-bg-1"),
        Role(SecurityAdmin, "Security Administrator", "u-sa-1"),
    };

    [Fact]
    public void PoliticaDirigidaAosPapeis_CobreOsPapeis_EExclusaoNominalEhExcecaoDeclarada()
    {
        var a = Analyze(new[] { Policy("p-admins", includeRoles: new[] { GlobalAdmin, SecurityAdmin }, excludeUsers: new[] { "u-bg-1" }) }, Roles);

        a.AdminMfa.Evaluable.Should().BeTrue();
        a.AdminMfa.UncoveredRoleCount.Should().Be(0, "a política mira os dois papéis, em todas as aplicações");
        var ga = a.AdminMfa.Roles.Single(r => r.Role.TemplateId == GlobalAdmin);
        ga.State.Should().Be(RoleMfaCoverageState.Covered);
        ga.ExceptionMemberIds.Should().Equal("u-bg-1");
    }

    [Theory]
    [InlineData(ConditionalAccessPolicyState.ReportOnly)]
    [InlineData(ConditionalAccessPolicyState.Disabled)]
    public void SomenteRelatorioOuDesabilitada_NaoImpoe(ConditionalAccessPolicyState state)
    {
        var a = Analyze(new[] { Policy("p", state, includeUsers: new[] { "All" }) }, Roles);

        a.AdminMfa.UncoveredRoleCount.Should().Be(2);
        a.EnforcedMfaPolicyCount.Should().Be(0);
        a.AdminMfa.Roles.SelectMany(r => r.Notes).Should().Contain(n => n.Contains("não conta como exigência"));
    }

    [Fact]
    public void MfaComoAlternativa_Ou_NaoEhExigencia_MasForcaDeAutenticacaoEh()
    {
        var alternativa = Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, op: "OR", controls: new[] { "mfa", "compliantDevice" }) }, Roles);
        alternativa.AdminMfa.UncoveredRoleCount.Should().Be(2);
        alternativa.Policies.Single().MfaIsAlternative.Should().BeTrue();

        var forca = Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, controls: Array.Empty<string>(), strength: "00000000-0000-0000-0000-000000000004") }, Roles);
        forca.AdminMfa.UncoveredRoleCount.Should().Be(0, "força de autenticação exigida é exigência de MFA");
    }

    [Fact]
    public void CondicaoDeLocalizacao_OuAplicacoesParciais_EstreitamAExigencia()
    {
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, location: true) }, Roles)
            .AdminMfa.UncoveredRoleCount.Should().Be(2);
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, apps: new[] { "MicrosoftAdminPortals" }) }, Roles)
            .AdminMfa.UncoveredRoleCount.Should().Be(2);
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, excludeApps: new[] { "app-1" }) }, Roles)
            .AdminMfa.UncoveredRoleCount.Should().Be(2);
    }

    [Fact]
    public void PapelExcluido_NaoEhCoberto_MesmoComTodosOsUsuarios()
    {
        var a = Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, excludeRoles: new[] { SecurityAdmin }) }, Roles);
        a.AdminMfa.UncoveredRoleCount.Should().Be(1);
        a.AdminMfa.Roles.Single(r => r.State == RoleMfaCoverageState.NotCovered).Role.TemplateId.Should().Be(SecurityAdmin);
    }

    [Fact]
    public void PoliticaPorGrupo_SemOutraCobertura_FicaInconclusiva()
    {
        var a = Analyze(new[] { Policy("p", includeGroups: new[] { "grp-admins" }) }, Roles);

        a.AdminMfa.Evaluable.Should().BeFalse("o pertencimento ao grupo não é coletado — não se afirma nem se nega");
        a.AdminMfa.InconclusiveReason.Should().Contain("grupos");
        ConditionalAccessAnalyzer.ToObservations(a, null)
            .Single(o => o.Key == KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).IsCollected.Should().BeFalse();
    }

    [Fact]
    public void PapelSemPolitica_EhExposicao_MesmoHavendoOutroNaoResolvido()
    {
        var roles = new[] { Roles[0], Roles[1], Role("role-3", "Exchange Administrator", "u-ex-1") };
        var a = Analyze(new[]
        {
            Policy("p1", includeRoles: new[] { GlobalAdmin }),
            // Por grupo: pode cobrir o Security Admin (não resolvido) — mas EXCLUI o papel de Exchange.
            Policy("p2", includeGroups: new[] { "grp-sec" }, excludeRoles: new[] { "role-3" }),
        }, roles);

        a.AdminMfa.Evaluable.Should().BeTrue();
        a.AdminMfa.UncoveredRoleCount.Should().Be(1, "só o Exchange é exposição comprovada; o Security fica não resolvido, sem ser contado");
        a.AdminMfa.Roles.Single(r => r.Role.TemplateId == SecurityAdmin).State.Should().Be(RoleMfaCoverageState.Unresolved);
    }

    [Fact]
    public void SemInventarioDePapeis_SoPoliticaUniversalConclui()
    {
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }) }, null).AdminMfa.Evaluable.Should().BeTrue();
        var semUniversal = Analyze(new[] { Policy("p", includeRoles: new[] { GlobalAdmin }) }, null);
        semUniversal.AdminMfa.Evaluable.Should().BeFalse();
    }

    [Fact]
    public void PoliticasNaoColetadas_ViramSinaisAusentes_NuncaNenhumaPolitica()
    {
        ConditionalAccessAnalyzer.Analyze(new KnightDirectoryConfiguration(null, Roles)).Should().BeNull();
        var obs = ConditionalAccessAnalyzer.ToObservations(null, "HTTP 403");
        obs.Should().OnlyContain(o => !o.IsCollected && o.MissingReason == "HTTP 403");
    }

    [Fact]
    public void BloqueioLegado_ExigeOsDoisTiposDeCliente_EExcecoesNaoReprovam()
    {
        var both = new[] { "exchangeActiveSync", "other" };
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-bg-1" }, excludeGroups: new[] { "g" }, clients: both, controls: new[] { "block" }) }, Roles)
            .LegacyAuth.Blocked.Should().BeTrue();
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, clients: new[] { "exchangeActiveSync" }, controls: new[] { "block" }) }, Roles)
            .LegacyAuth.Blocked.Should().BeFalse();
        Analyze(new[] { Policy("p", ConditionalAccessPolicyState.ReportOnly, includeUsers: new[] { "All" }, clients: both, controls: new[] { "block" }) }, Roles)
            .LegacyAuth.Blocked.Should().BeFalse();
        Analyze(new[] { Policy("p", includeGroups: new[] { "g" }, clients: both, controls: new[] { "block" }) }, Roles)
            .LegacyAuth.Blocked.Should().BeFalse("bloquear só um grupo não bloqueia a autenticação legada do tenant");
    }

    // ---- Regras v3 do catálogo ---------------------------------------------------------------------

    private static KnightEvaluatedIndicator Eval(string id, params KnightObservation[] obs) =>
        KnightIndicatorEvaluator.Evaluate(new KnightFactSet(obs), KnightSourceType.MicrosoftEntraId).Single(e => e.Definition.Id == id);

    private static KnightObservation Defaults(bool? on) => on is null
        ? KnightObservation.MissingData(KnightSignalKey.SecurityDefaultsEnabled, "HTTP 403")
        : KnightObservation.OfFlag(KnightSignalKey.SecurityDefaultsEnabled, on.Value);

    [Fact]
    public void AdminMfa_SecurityDefaultsNaoVerificado_NaoReprova()
    {
        var e = Eval("AK-ENTRA-008", Defaults(null), KnightObservation.OfCount(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, 2));
        e.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        e.NotEvaluatedReason.Should().Contain("security defaults");

        Eval("AK-ENTRA-008", Defaults(false), KnightObservation.OfCount(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, 2))
            .Should().Match<KnightEvaluatedIndicator>(x => x.Status == KnightIndicatorStatus.Exposed && x.AffectedObjectCount == 2);
        Eval("AK-ENTRA-008", Defaults(true), KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, "x"))
            .Status.Should().Be(KnightIndicatorStatus.Passed);
        Eval("AK-ENTRA-008", Defaults(false), KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, "grupo"))
            .Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public void Legado_SecurityDefaultsNaoVerificado_NaoReprova()
    {
        Eval("AK-ENTRA-007", Defaults(null), KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, false))
            .Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        Eval("AK-ENTRA-007", Defaults(false), KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, false))
            .Status.Should().Be(KnightIndicatorStatus.Exposed);
        Eval("AK-ENTRA-007", Defaults(null), KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, true))
            .Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public void Baseline_PoliticaHabilitadaExigindoMfa_Aprova_SemEvidenciaNaoReprova()
    {
        Eval("AK-ENTRA-014", Defaults(false), KnightObservation.OfCount(KnightSignalKey.EnforcedMfaPolicies, 1)).Status.Should().Be(KnightIndicatorStatus.Passed);
        Eval("AK-ENTRA-014", Defaults(false), KnightObservation.OfCount(KnightSignalKey.EnforcedMfaPolicies, 0)).Status.Should().Be(KnightIndicatorStatus.Exposed);
        Eval("AK-ENTRA-014", Defaults(null), KnightObservation.OfCount(KnightSignalKey.EnforcedMfaPolicies, 0)).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public void Catalogo_V3_TituloDoRegistroDeMfaAfirmaSoOQueMede()
    {
        KnightCatalog.Version.Should().Be("ak-knight-v3");
        var d = KnightCatalog.Indicators.Single(i => i.Id == "AK-ENTRA-001");
        d.Title.Should().Contain("registrado").And.NotContain("efetiva");
        Eval("AK-ENTRA-001",
                KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 1),
                KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 3))
            .Evidence.Should().Contain("registrado").And.NotContain("efetivo");
    }

    [Fact]
    public void Perfis_TodoIndicadorTemDominioServicoEReferencias_EUrlsSaoHttps()
    {
        foreach (var d in KnightCatalog.Indicators)
        {
            var p = KnightControlProfiles.For(d.Id);
            p.Should().NotBeNull($"{d.Id} precisa de perfil para o relatório");
            p!.RequiredCapabilities.Should().NotBeEmpty();
            p.Documentation.Where(x => x.Url is not null).All(x => x.Url!.StartsWith("https://learn.microsoft.com/"))
                .Should().BeTrue("só documentação oficial com endereço conferido");
        }

        var refs = KnightControlProfiles.ReferencesOf("AK-ENTRA-001", new[] { "PR.AA-01", "PR.AA-01" }, new[] { "T1078" });
        refs.Count(r => r.Framework == KnightControlProfiles.NistFramework).Should().Be(1, "o mesmo código não conta duas vezes");
    }
}
