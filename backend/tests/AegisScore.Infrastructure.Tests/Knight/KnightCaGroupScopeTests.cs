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
/// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] O pertencimento lido (PR #101) também decide o alcance das EXIGÊNCIAS de acesso condicional
/// (AK-ENTRA-040, 058–070) e do bloqueio de autenticação legada (AK-ENTRA-007). Antes, esses caminhos tratavam toda política
/// dirigida a grupo, ou com exclusão de grupo, como "pertencimento não coletado" — mesmo com o pertencimento completo no ADM.
/// </summary>
public sealed class KnightCaGroupScopeTests
{
    private const string Ga = EntraConfigurationScenario.Ga;
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
    private static readonly string[] Contas = { "u1", "u2", "u3" };

    private static ConditionalAccessPolicyConfiguration Policy(string id, string[]? includeUsers = null, string[]? includeGroups = null,
        string[]? excludeGroups = null, string[]? includeRoles = null, string[]? excludeUsers = null, string[]? clients = null,
        string[]? controls = null, string? transfer = null, string[]? apps = null,
        ConditionalAccessPolicyState state = ConditionalAccessPolicyState.Enabled) =>
        new(id, "Política " + id, state, state.ToString(),
            includeUsers ?? Array.Empty<string>(), excludeUsers ?? Array.Empty<string>(),
            includeGroups ?? Array.Empty<string>(), excludeGroups ?? Array.Empty<string>(),
            includeRoles ?? Array.Empty<string>(), Array.Empty<string>(),
            false, false, apps ?? new[] { "All" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            clients ?? new[] { "all" }, false, false, false, false, false, "OR", controls ?? new[] { "block" }, null, null,
            AuthenticationFlowsTransferMethods: transfer, SessionAndConditionsCaptured: true);

    private static ConditionalAccessPolicyConfiguration DeviceCode(string id, string[]? includeUsers = null, string[]? includeGroups = null,
        string[]? excludeGroups = null, string[]? includeRoles = null, string[]? apps = null,
        ConditionalAccessPolicyState state = ConditionalAccessPolicyState.Enabled) =>
        Policy(id, includeUsers, includeGroups, excludeGroups, includeRoles, transfer: "deviceCodeFlow", apps: apps, state: state);

    private static ConditionalAccessPolicyConfiguration Legacy(string id, string[]? includeUsers = null, string[]? includeGroups = null,
        string[]? excludeGroups = null) =>
        Policy(id, includeUsers, includeGroups, excludeGroups, clients: new[] { "exchangeActiveSync", "other" });

    private static EntraGroupMembership Group(string id, EntraGroupMembershipState state, params string[] users) =>
        new(id, "Grupo " + id, state, users.Select(u => new EntraGroupMember(u, EntraMemberKind.User)).ToList(),
            new[] { "policy:x:include" }, At, "/v1.0/groups/" + id + "/transitiveMembers");

    private static DirectoryRoleConfiguration RoleV2(string template, string name, string[] users, string[]? groups = null) =>
        new(template, name, users.Length + (groups?.Length ?? 0), users, groups ?? Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());

    /// <summary>A mesma forma das exigências do catálogo: candidata pelo tema, lacuna pela aplicação e pelo controle exigido.</summary>
    private static readonly CaRequirement BlockDeviceCode = new(
        "bloqueio do fluxo de código de dispositivo",
        r => (r.Policy.AuthenticationFlowsTransferMethods ?? "").Contains("deviceCodeFlow", StringComparison.OrdinalIgnoreCase),
        r => !r.Blocks ? "não bloqueia o acesso" : !ConditionalAccessRequirements.AllApplications(r.Policy) ? "não alcança todas as aplicações" : null);

    private static KnightDirectoryConfiguration Directory(IReadOnlyList<ConditionalAccessPolicyConfiguration> policies,
        IReadOnlyList<EntraGroupMembership>? groups, string[]? enabled = null, IReadOnlyList<DirectoryRoleConfiguration>? roles = null) =>
        new(policies, roles ?? Array.Empty<DirectoryRoleConfiguration>(), groups, enabled);

    private static CaRequirementEvaluation AllUsers(KnightDirectoryConfiguration d) =>
        ConditionalAccessRequirements.ForAllUsers(d, BlockDeviceCode, null, id => "Pessoa " + id);

    private static ConditionalAccessAnalysis Analyze(KnightDirectoryConfiguration d) => ConditionalAccessAnalyzer.Analyze(d)!;

    private static KnightObservation LegacySignal(ConditionalAccessAnalysis a) =>
        ConditionalAccessAnalyzer.ToObservations(a, null).Single(o => o.Key == KnightSignalKey.LegacyAuthenticationBlocked);

    // ===== Exigências para todos os usuários ===========================================================================

    [Fact]
    public void Exigencia_DirigidaAGrupoQueAlcancaCadaConta_Aprovada_ConferidaContaAConta()
    {
        var e = AllUsers(Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }) },
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1", "u2", "u3") }, Contas));

        e.State.Should().Be(CaCoverageState.Satisfied, e.Reason);
        e.Satisfying.Select(r => r.Policy.Id).Should().Equal("p-g");
        e.Reach!["p-g"].Should().Contain("3 de 3 conta(s) habilitada(s)");
        e.NotCounting.Should().BeEmpty();
    }

    [Fact]
    public void Exigencia_GrupoCompletoDeixaContaDeFora_Reprovada_ComOsNumeros()
    {
        var e = AllUsers(Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-parte" }) },
            new[] { Group("g-parte", EntraGroupMembershipState.Complete, "u1", "u2") }, Contas));

        e.State.Should().Be(CaCoverageState.NotSatisfied, "u3 está fora de um grupo lido por completo — lacuna comprovada");
        e.Reason.Should().Contain("1 de 3 conta(s) habilitada(s)");
        e.NotCounting.Single().Why.Should().NotContain("não é coletado");
    }

    [Fact]
    public void Exigencia_QuantidadesIguaisIdsDiferentes_NaoAprova()
    {
        var e = AllUsers(Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }) },
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1", "u2", "u-desabilitada") }, Contas));
        e.State.Should().Be(CaCoverageState.NotSatisfied, "a comparação é pela identidade, nunca pela contagem");
    }

    [Fact]
    public void Exigencia_DuasPoliticasDeGrupoSomadas_AlcancamTodas_Aprovada()
    {
        var e = AllUsers(Directory(
            new[] { DeviceCode("p-a", includeGroups: new[] { "g-a" }), DeviceCode("p-b", includeUsers: new[] { "u3" }) },
            new[] { Group("g-a", EntraGroupMembershipState.Complete, "u1", "u2") }, Contas));
        e.State.Should().Be(CaCoverageState.Satisfied, e.Reason);
        e.Satisfying.Select(r => r.Policy.Id).Should().BeEquivalentTo("p-a", "p-b");
    }

    [Fact]
    public void Exigencia_ExclusaoPorGrupoLida_ContasNomeadas_ECobertaPorOutraPoliticaDeGrupo()
    {
        var policies = new List<ConditionalAccessPolicyConfiguration>
        {
            DeviceCode("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }),
        };
        var groups = new List<EntraGroupMembership>
        {
            Group("g-bg", EntraGroupMembershipState.Complete, "u-bg"),
            Group("g-emerg", EntraGroupMembershipState.Complete, "u-bg"),
        };

        var open = AllUsers(Directory(policies, groups, Contas));
        open.State.Should().Be(CaCoverageState.OpenExceptions, "exceção explícita não comprova proteção");
        open.OpenExceptions.Should().ContainSingle().Which.Should().Contain("Pessoa u-bg").And.Contain("Grupo g-bg").And.NotContain("não coletado");

        // Outra política habilitada, dirigida a OUTRO grupo lido por completo que contém a conta excluída, cobre a exceção.
        policies.Add(DeviceCode("p-emerg", includeGroups: new[] { "g-emerg" }));
        var covered = AllUsers(Directory(policies, groups, Contas));
        covered.State.Should().Be(CaCoverageState.Satisfied, covered.Reason);
    }

    [Theory]
    [InlineData(EntraGroupMembershipState.Incomplete)]
    [InlineData(EntraGroupMembershipState.Denied)]
    [InlineData(EntraGroupMembershipState.NotFound)]
    [InlineData(EntraGroupMembershipState.Failed)]
    public void Exigencia_PertencimentoNaoCompleto_NaoResolvida_ComALeituraQueFalta(EntraGroupMembershipState state)
    {
        var e = AllUsers(Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }) },
            new[] { Group("g-todos", state, "u1", "u2") }, Contas));

        e.State.Should().Be(CaCoverageState.Unresolved, "quem não foi listado fica desconhecido — nunca aprovação por lista parcial");
        e.Reason.Should().Contain("1 de 3").And.Contain("Grupo g-todos").And.Contain(EntraGroupMembership.Label(state))
            .And.NotContain("não é coletado");
    }

    [Fact]
    public void Exigencia_ExclusaoOcultaNaPoliticaDeGrupo_NaoResolvida()
    {
        var e = AllUsers(Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }, excludeGroups: new[] { "g-oculto" }) },
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, Contas), Group("g-oculto", EntraGroupMembershipState.Denied) }, Contas));
        e.State.Should().Be(CaCoverageState.Unresolved, "a exclusão não lida não é ignorada");
        e.Reason.Should().Contain("Grupo g-oculto").And.Contain("leitura recusada");
    }

    [Fact]
    public void Exigencia_PopulacaoVaziaOuAusente_NuncaAprova()
    {
        var policies = new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }) };
        var groups = new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1") };

        var empty = AllUsers(Directory(policies, groups, Array.Empty<string>()));
        empty.State.Should().Be(CaCoverageState.Unresolved);
        empty.Reason.Should().Contain("nenhuma conta habilitada");

        var missing = AllUsers(Directory(policies, groups, enabled: null));
        missing.State.Should().Be(CaCoverageState.Unresolved);
        missing.Reason.Should().Contain("sem a enumeração completa das contas habilitadas");
    }

    [Fact]
    public void Exigencia_TodasAsContasExcluidas_NaoAprova()
    {
        var e = AllUsers(Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }, excludeGroups: new[] { "g-todos2" }) },
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, Contas), Group("g-todos2", EntraGroupMembershipState.Complete, Contas) },
            Contas));
        e.State.Should().Be(CaCoverageState.NotSatisfied, "a política não alcança conta alguma");
        e.Reason.Should().Contain("nenhuma das 3 conta(s) habilitada(s)");
    }

    [Fact]
    public void Exigencia_GrupoResolvidoNaoDispensaEstadoAplicacaoNemControle()
    {
        var groups = new[] { Group("g-todos", EntraGroupMembershipState.Complete, Contas) };
        foreach (var policy in new[]
                 {
                     DeviceCode("p-relatorio", includeGroups: new[] { "g-todos" }, state: ConditionalAccessPolicyState.ReportOnly),
                     DeviceCode("p-desab", includeGroups: new[] { "g-todos" }, state: ConditionalAccessPolicyState.Disabled),
                     DeviceCode("p-apps", includeGroups: new[] { "g-todos" }, apps: new[] { "Office365" }),
                     Policy("p-mfa", includeGroups: new[] { "g-todos" }, transfer: "deviceCodeFlow", controls: new[] { "mfa" }),
                 })
        {
            var e = AllUsers(Directory(new[] { policy }, groups, Contas));
            e.State.Should().Be(CaCoverageState.NotSatisfied, policy.Id);
            e.Satisfying.Should().BeEmpty(policy.Id);
        }
    }

    // ===== Papéis privilegiados ========================================================================================

    [Fact]
    public void Privilegiados_PapelDetidoViaGrupo_ConferidoPeloPertencimento_PoliticaPorPapelNaoComprova()
    {
        var roles = new[] { RoleV2(Ga, "Global Administrator", Array.Empty<string>(), groups: new[] { "g-ga" }) };
        var groups = new[] { Group("g-ga", EntraGroupMembershipState.Complete, "u7") };

        // Dirigida ao GRUPO que detém o papel: alcance comprovado pelo pertencimento.
        var byGroup = ConditionalAccessRequirements.ForPrivilegedRoles(
            Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-ga" }) }, groups, roles: roles), BlockDeviceCode, null);
        byGroup.State.Should().Be(CaCoverageState.Satisfied, byGroup.Reason);
        byGroup.Roles.Single().Members.Single().MemberId.Should().Be("u7");

        // Dirigida ao PAPEL: quem o detém por grupo não é comprovadamente alcançado — antes, isso aprovava pelo papel.
        var byRole = ConditionalAccessRequirements.ForPrivilegedRoles(
            Directory(new[] { DeviceCode("p-r", includeRoles: new[] { Ga }) }, groups, roles: roles), BlockDeviceCode, null);
        byRole.State.Should().Be(CaCoverageState.Unresolved);
        byRole.Reason.Should().NotContain("não é coletado");

        // Grupo do papel lido em parte: nunca coberto.
        var partial = ConditionalAccessRequirements.ForPrivilegedRoles(
            Directory(new[] { DeviceCode("p-g", includeGroups: new[] { "g-ga" }) },
                new[] { Group("g-ga", EntraGroupMembershipState.Incomplete, "u7") }, roles: roles), BlockDeviceCode, null);
        partial.State.Should().Be(CaCoverageState.Unresolved);
    }

    [Fact]
    public void Privilegiados_ExclusaoPorGrupoLida_MembroExcluidoEhExcecao_OutroCoberto()
    {
        var roles = new[] { RoleV2(Ga, "Global Administrator", new[] { "u1", "u-bg" }) };
        var e = ConditionalAccessRequirements.ForPrivilegedRoles(
            Directory(new[] { DeviceCode("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }) },
                new[] { Group("g-bg", EntraGroupMembershipState.Complete, "u-bg") }, roles: roles), BlockDeviceCode, null);

        e.State.Should().Be(CaCoverageState.OpenExceptions, "antes: não resolvida por 'pertencimento não coletado'");
        e.Roles.Single().ExceptionMemberIds.Should().Equal("u-bg");
    }

    // ===== Autenticação legada =========================================================================================

    [Fact]
    public void Legada_DirigidaAGrupoQueAlcancaCadaConta_Bloqueada()
    {
        var a = Analyze(Directory(new[] { Legacy("p-legada", includeGroups: new[] { "g-todos" }) },
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, Contas) }, Contas));

        a.LegacyAuth.Blocked.Should().BeTrue("antes: 'parcial' e reprovada sem considerar o pertencimento");
        a.LegacyAuth.BlockingPolicyIds.Should().Equal("p-legada");
        a.LegacyAuth.Reach!["p-legada"].Should().Contain("3 de 3 conta(s) habilitada(s)");
        LegacySignal(a).Flag.Should().BeTrue();
    }

    [Fact]
    public void Legada_DirigidaAGrupoComContaDeFora_NaoBloqueada_ComOAlcance()
    {
        var a = Analyze(Directory(new[] { Legacy("p-legada", includeGroups: new[] { "g-parte" }) },
            new[] { Group("g-parte", EntraGroupMembershipState.Complete, "u1") }, Contas));
        a.LegacyAuth.Blocked.Should().BeFalse();
        a.LegacyAuth.InconclusiveReason.Should().BeNull("lacuna comprovada, não incerteza");
        a.LegacyAuth.Reach!["p-legada"].Should().Contain("1 de 3");
        LegacySignal(a).Flag.Should().BeFalse();
    }

    [Theory]
    [InlineData(EntraGroupMembershipState.Incomplete)]
    [InlineData(EntraGroupMembershipState.Denied)]
    public void Legada_PertencimentoNaoCompleto_SinalAusente_ComOGrupo(EntraGroupMembershipState state)
    {
        var a = Analyze(Directory(new[] { Legacy("p-legada", includeGroups: new[] { "g-todos" }) },
            new[] { Group("g-todos", state, "u1") }, Contas));
        a.LegacyAuth.Blocked.Should().BeFalse();
        LegacySignal(a).Outcome.Should().Be(KnightObservationOutcome.Missing);
        a.LegacyAuth.InconclusiveReason.Should().Contain("Grupo g-todos").And.Contain(EntraGroupMembership.Label(state));
    }

    [Fact]
    public void Legada_ExclusaoPorGrupoLida_CobertaPorPoliticaDeOutroGrupo_Bloqueada()
    {
        var a = Analyze(Directory(
            new[]
            {
                Legacy("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }),
                Legacy("p-emerg", includeGroups: new[] { "g-emerg" }),
            },
            new[] { Group("g-bg", EntraGroupMembershipState.Complete, "u-bg"), Group("g-emerg", EntraGroupMembershipState.Complete, "u-bg") },
            Contas));

        a.LegacyAuth.Blocked.Should().BeTrue("a conta excluída é bloqueada pela outra política, pelo pertencimento lido");
        a.LegacyAuth.BlockingPolicyIds.Should().BeEquivalentTo("p-todos", "p-emerg");
    }

    [Fact]
    public void Legada_ExclusaoPorGrupoLidaSemOutraCobertura_InconclusivaComAsContas()
    {
        var a = Analyze(Directory(new[] { Legacy("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }) },
            new[] { Group("g-bg", EntraGroupMembershipState.Complete, "u-bg") }, Contas));
        a.LegacyAuth.Blocked.Should().BeFalse();
        a.LegacyAuth.InconclusiveReason.Should().Contain("1 conta excluída via grupo “Grupo g-bg” (lido por completo)")
            .And.NotContain("pertencimento não coletado");
    }

    // ===== Compatibilidade com aquisições anteriores ===================================================================

    [Fact]
    public void AquisicaoSemPertencimento_MantemALeituraDeEntao_NaoResolvida()
    {
        var policies = new[] { DeviceCode("p-g", includeGroups: new[] { "g-todos" }) };
        var e = AllUsers(Directory(policies, groups: null, Contas));
        e.State.Should().Be(CaCoverageState.Unresolved);
        e.Reason.Should().Contain("não é coletado");

        var excl = AllUsers(Directory(new[] { DeviceCode("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }) }, null, Contas));
        excl.State.Should().Be(CaCoverageState.OpenExceptions);
        excl.OpenExceptions.Single().Should().Contain("pertencimento não coletado");

        var legacy = Analyze(Directory(new[] { Legacy("p-legada", includeGroups: new[] { "g-todos" }) }, null, Contas));
        legacy.LegacyAuth.Blocked.Should().BeFalse("sem pertencimento, o grupo nunca é presumido");
        LegacySignal(legacy).Outcome.Should().Be(KnightObservationOutcome.Missing, "informação ausente é alcance desconhecido, não lacuna");
    }

    [Fact]
    public void RegrasRevisadas_IdentificadasPelaVersao_SemCriterioNovoEmFotografiaAntiga()
    {
        KnightCatalog.Version.Should().Be("ak-knight-v12");
        foreach (var (id, rule) in new[] { ("AK-ENTRA-007", "3"), ("AK-ENTRA-040", "2"), ("AK-ENTRA-058", "2"), ("AK-ENTRA-066", "2"), ("AK-ENTRA-070", "2") })
        {
            var d = KnightCatalog.Indicators.Single(i => i.Id == id);
            d.Version.Should().Be(rule, id);
            d.ExpectedEvidence.Should().Contain("pertencimento", id);
            KnightControlPresentations.For(id, d.Category, d.Severity, KnightIndicatorStatus.Passed, KnightSourceType.MicrosoftEntraId,
                d.NistCodes, d.MitreTechniques, "ak-knight-v11").Criterion.Should().BeNull(id + ": fotografia v11 não recebe critério novo");
        }
    }
}
