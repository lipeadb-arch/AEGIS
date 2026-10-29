using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Pertencimento a grupos e identidades privilegiadas no Entra ID. O relatório de 27/10
/// tinha AK-ENTRA-008 ("9 papéis dependem de grupos cujo pertencimento não é coletado"), AK-ENTRA-014 (política de MFA
/// dirigida a grupo) e AK-ENTRA-001/011 ("membro privilegiado não identificável como usuário" — eram grupos atribuídos a
/// papéis) sem avaliação. Duas camadas: o analisador puro (alcance conta a conta) e o percurso inteiro (Graph sintético →
/// coletor → ADM → releitura → avaliação).
/// </summary>
public sealed class KnightEntraGroupMembershipTests : IDisposable
{
    private const string Ga = EntraConfigurationScenario.Ga;
    private const string Sa = EntraConfigurationScenario.Sa;
    private const string Dev = "cf1c38e5-3621-4004-a7cb-879624dced7c"; // Application Developer
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-27T12:00:00Z");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightEntraGroupMembershipTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ===== 1) Analisador: alcance conta a conta ===================================================================

    private static ConditionalAccessPolicyConfiguration Mfa(string id, string[]? includeUsers = null, string[]? includeGroups = null,
        string[]? excludeGroups = null, string[]? includeRoles = null, string[]? excludeUsers = null) =>
        new(id, "Política " + id, ConditionalAccessPolicyState.Enabled, "enabled",
            includeUsers ?? Array.Empty<string>(), excludeUsers ?? Array.Empty<string>(),
            includeGroups ?? Array.Empty<string>(), excludeGroups ?? Array.Empty<string>(),
            includeRoles ?? Array.Empty<string>(), Array.Empty<string>(),
            false, false, new[] { "All" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), false, false, false, false, false, "OR", new[] { "mfa" }, null, null);

    private static EntraGroupMembership Group(string id, EntraGroupMembershipState state, params string[] users) =>
        new(id, "Grupo " + id, state, users.Select(u => new EntraGroupMember(u, EntraMemberKind.User)).ToList(),
            new[] { "policy:x:include" }, At, "/v1.0/groups/" + id + "/transitiveMembers");

    private static DirectoryRoleConfiguration RoleV2(string template, string name, string[] users, string[]? groups = null, string[]? apps = null) =>
        new(template, name, users.Length + (groups?.Length ?? 0) + (apps?.Length ?? 0), users,
            groups ?? Array.Empty<string>(), apps ?? Array.Empty<string>(), Array.Empty<string>());

    private static ConditionalAccessAnalysis Analyze(IReadOnlyList<ConditionalAccessPolicyConfiguration> policies,
        IReadOnlyList<DirectoryRoleConfiguration> roles, IReadOnlyList<EntraGroupMembership>? groups, string[]? enabled = null) =>
        ConditionalAccessAnalyzer.Analyze(new KnightDirectoryConfiguration(policies, roles, groups, enabled))!;

    private static KnightObservation Signal(ConditionalAccessAnalysis a, KnightSignalKey key) =>
        ConditionalAccessAnalyzer.ToObservations(a, null).Single(o => o.Key == key);

    [Fact]
    public void PoliticaDirigidaAGrupo_MembroLidoEhAlcancado_ForaDoGrupoCompletoNaoEh()
    {
        var policies = new[] { Mfa("p-grupo", includeGroups: new[] { "g-adm" }) };
        var roles = new[] { RoleV2(Ga, "Global Administrator", new[] { "u1", "u2" }) };

        var covered = Analyze(policies, roles, new[] { Group("g-adm", EntraGroupMembershipState.Complete, "u1", "u2") });
        covered.AdminMfa.Evaluable.Should().BeTrue();
        covered.AdminMfa.Roles.Single().State.Should().Be(RoleMfaCoverageState.Covered);
        Signal(covered, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).Count.Should().Be(0);

        var gap = Analyze(policies, roles, new[] { Group("g-adm", EntraGroupMembershipState.Complete, "u1") });
        gap.AdminMfa.Roles.Single().State.Should().Be(RoleMfaCoverageState.NotCovered, "u2 está fora de um grupo lido por completo");
        gap.AdminMfa.Roles.Single().UncoveredMemberIds.Should().Equal("u2");
        Signal(gap, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).Count.Should().Be(1);
    }

    [Theory]
    [InlineData(EntraGroupMembershipState.Incomplete)]
    [InlineData(EntraGroupMembershipState.Denied)]
    [InlineData(EntraGroupMembershipState.NotFound)]
    [InlineData(EntraGroupMembershipState.Failed)]
    public void LeituraNaoCompleta_QuemNaoFoiListadoFicaDesconhecido_NuncaAprovaPorListaParcial(EntraGroupMembershipState state)
    {
        var policies = new[] { Mfa("p-grupo", includeGroups: new[] { "g-adm" }) };
        var roles = new[] { RoleV2(Ga, "Global Administrator", new[] { "u1", "u2" }) };
        var a = Analyze(policies, roles, new[] { Group("g-adm", state, "u1") });

        var role = a.AdminMfa.Roles.Single();
        role.State.Should().Be(RoleMfaCoverageState.Unresolved);
        role.Members.Single(m => m.MemberId == "u1").State.Should().Be(MemberMfaCoverageState.Covered, "listado é prova positiva");
        role.Members.Single(m => m.MemberId == "u2").State.Should().Be(MemberMfaCoverageState.Unresolved);
        Signal(a, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).Outcome.Should().Be(KnightObservationOutcome.Missing);
    }

    [Fact]
    public void ExclusaoPorGrupo_MembroListadoEhExcluido_NaoMembroDeGrupoCompletoContinuaCoberto()
    {
        var policies = new[] { Mfa("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }) };
        var roles = new[] { RoleV2(Ga, "Global Administrator", new[] { "u1", "u-bg" }) };

        var a = Analyze(policies, roles, new[] { Group("g-bg", EntraGroupMembershipState.Complete, "u-bg") });
        var role = a.AdminMfa.Roles.Single();
        role.Members.Single(m => m.MemberId == "u1").State.Should().Be(MemberMfaCoverageState.Covered);
        role.Members.Single(m => m.MemberId == "u-bg").State.Should().Be(MemberMfaCoverageState.Excepted, "exclusão não é ignorada");
        role.State.Should().Be(RoleMfaCoverageState.CoveredWithExceptions);
        a.AdminMfa.Evaluable.Should().BeFalse("exceção explícita não comprova proteção");

        // Mesma política, exclusão com leitura recusada: ninguém fica coberto por presunção.
        var denied = Analyze(policies, roles, new[] { Group("g-bg", EntraGroupMembershipState.Denied) });
        denied.AdminMfa.Roles.Single().Members.Should().OnlyContain(m => m.State == MemberMfaCoverageState.Unresolved);
    }

    [Fact]
    public void PapelViaGrupo_PoliticaPorPapelNaoComprovaAlcance_GrupoAlvoComprova()
    {
        var roles = new[] { RoleV2(Ga, "Global Administrator", Array.Empty<string>(), groups: new[] { "g-ga" }) };
        var groups = new[] { Group("g-ga", EntraGroupMembershipState.Complete, "u7", "u8") };

        var byRole = Analyze(new[] { Mfa("p-admins", includeRoles: new[] { Ga }) }, roles, groups);
        byRole.AdminMfa.Roles.Single().State.Should().Be(RoleMfaCoverageState.Unresolved,
            "a documentação não afirma que a política dirigida ao papel alcance quem o detém via grupo");
        byRole.AdminMfa.Roles.Single().Members.Select(m => m.MemberId).Should().BeEquivalentTo("u7", "u8");

        var byGroup = Analyze(new[] { Mfa("p-admins", includeRoles: new[] { Ga }), Mfa("p-grupo", includeGroups: new[] { "g-ga" }) }, roles, groups);
        byGroup.AdminMfa.Roles.Single().State.Should().Be(RoleMfaCoverageState.Covered);
        Signal(byGroup, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).Count.Should().Be(0);
    }

    [Fact]
    public void PapelSoComAplicacoes_NaoEhTratadoComoUsuarioSujeitoAMfa()
    {
        var roles = new[] { RoleV2(Dev, "Application Developer", Array.Empty<string>(), apps: new[] { "sp-1" }) };
        var a = Analyze(new[] { Mfa("p-admins", includeRoles: new[] { Ga }) }, roles, Array.Empty<EntraGroupMembership>());

        var role = a.AdminMfa.Roles.Single();
        role.State.Should().Be(RoleMfaCoverageState.NoUserMembers);
        role.Notes.Should().Contain(n => n.Contains("identidade de aplicação"));
        Signal(a, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).Count.Should().Be(0, "o papel não entra na contagem");
    }

    [Fact]
    public void PapelComGrupoNaoLido_NaoEhCoberto_MesmoComOsDiretosCobertos()
    {
        var roles = new[] { RoleV2(Ga, "Global Administrator", new[] { "u1" }, groups: new[] { "g-oculto" }) };
        var a = Analyze(new[] { Mfa("p-admins", includeRoles: new[] { Ga }) }, roles, new[] { Group("g-oculto", EntraGroupMembershipState.Denied) });
        a.AdminMfa.Roles.Single().State.Should().Be(RoleMfaCoverageState.Unresolved);
        a.AdminMfa.Roles.Single().Notes.Should().Contain(n => n.Contains("leitura recusada"));
    }

    [Fact]
    public void AquisicaoAnterior_SemPertencimento_MantemALeituraDeEntao()
    {
        // Papel v1 (sem tipos) e nenhum pertencimento: o mesmo resultado que a análise produzia antes.
        var v1 = new DirectoryRoleConfiguration(Ga, "Global Administrator", 2, new[] { "u1" });
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-adm" }) }, new[] { v1 }, null);
        a.AdminMfa.Evaluable.Should().BeFalse();
        a.AdminMfa.InconclusiveReason.Should().Contain("cujo pertencimento não é coletado nesta entrega");
        a.Baseline.InconclusiveReason.Should().Contain("cujo pertencimento não é coletado nesta entrega");
    }

    [Fact]
    public void BaseMinima_TodosComExclusaoDeGrupoLida_EhBase_ComAsContasDelimitadas()
    {
        var a = Analyze(new[] { Mfa("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }) },
            Array.Empty<DirectoryRoleConfiguration>(), new[] { Group("g-bg", EntraGroupMembershipState.Complete, "u-bg") });
        a.Baseline.BaselinePolicyIds.Should().Equal("p-todos");
        a.Baseline.Reach!["p-todos"].Should().Contain("1 conta excluída");
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Count.Should().Be(1);
    }

    private static readonly string[] Nove = { "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8", "u9" };

    [Fact]
    public void BaseMinima_DirigidaAGrupoComContasForaDoAlvo_NaoEhBase()
    {
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-parte" }) },
            Array.Empty<DirectoryRoleConfiguration>(), new[] { Group("g-parte", EntraGroupMembershipState.Complete, "u1", "u2") }, enabled: Nove);
        a.Baseline.RestrictedPolicyIds.Should().Equal("p-grupo");
        a.Baseline.Reach!["p-grupo"].Should().Contain("7 de 9 conta(s) habilitada(s) fora do alvo");
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Count.Should().Be(0, "há conta fora do alvo: não há base para o ambiente");
    }

    [Fact]
    public void BaseMinima_QuantidadesIguaisComIdsDiferentes_NaoAprova()
    {
        // Três no grupo, três habilitadas — mas "u-desabilitada" não é "u3": a comparação é por identidade.
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }) }, Array.Empty<DirectoryRoleConfiguration>(),
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1", "u2", "u-desabilitada") }, enabled: new[] { "u1", "u2", "u3" });
        a.Baseline.BaselinePolicyIds.Should().BeEmpty();
        a.Baseline.RestrictedPolicyIds.Should().Equal("p-grupo");
        a.Baseline.Reach!["p-grupo"].Should().Contain("1 de 3 conta(s) habilitada(s) fora do alvo");
    }

    [Fact]
    public void BaseMinima_AlcanceComprovadoContaAConta_EhBase_SemRelaxarAsCondicoes()
    {
        var groups = new[] { Group("g-todos", EntraGroupMembershipState.Complete, Nove.Concat(new[] { "u-desabilitada" }).ToArray()) };
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }) }, Array.Empty<DirectoryRoleConfiguration>(), groups, enabled: Nove);
        a.Baseline.BaselinePolicyIds.Should().Equal("p-grupo");
        a.Baseline.Reach!["p-grupo"].Should().Contain("9 de 9 conta(s) habilitada(s) alcançada(s)");
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Count.Should().Be(1);

        // O mesmo alcance com condição que estreita (localização) não é base: demonstrar alcance não relaxa o critério.
        var narrowed = Mfa("p-grupo", includeGroups: new[] { "g-todos" }) with { HasLocationCondition = true };
        Analyze(new[] { narrowed }, Array.Empty<DirectoryRoleConfiguration>(), groups, enabled: Nove).Baseline.BaselinePolicyIds.Should().BeEmpty();

        // Exclusão nominal de uma conta habilitada é delimitada (excluída explicitamente), como nas políticas para todos.
        var withBreakGlass = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }, excludeUsers: new[] { "u9" }) },
            Array.Empty<DirectoryRoleConfiguration>(), groups, enabled: Nove);
        withBreakGlass.Baseline.Reach!["p-grupo"].Should().Contain("8 de 9").And.Contain("1 excluída(s) explicitamente");
    }

    [Fact]
    public void BaseMinima_DirigidaAGrupoComTodasAsContasExcluidas_NaoEhBase()
    {
        // Revisão do PR #101: fora = 0 e desconhecidas = 0, mas nenhuma conta sujeita ao MFA — a política não protege ninguém.
        var groups = new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1", "u2") };
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }, excludeUsers: new[] { "u1", "u2" }) },
            Array.Empty<DirectoryRoleConfiguration>(), groups, enabled: new[] { "u1", "u2" });
        a.Baseline.BaselinePolicyIds.Should().BeEmpty();
        a.Baseline.RestrictedPolicyIds.Should().Equal("p-grupo");
        a.Baseline.Reach!["p-grupo"].Should().Contain("nenhuma das 2 conta(s) habilitada(s) é alcançada").And.Contain("2 excluída(s) explicitamente");
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Count.Should().Be(0);

        // O mesmo pela exclusão de um grupo lido por completo, e na política para todos os usuários.
        var byGroup = groups.Append(Group("g-bg", EntraGroupMembershipState.Complete, "u1", "u2")).ToArray();
        Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }, excludeGroups: new[] { "g-bg" }) },
            Array.Empty<DirectoryRoleConfiguration>(), byGroup, enabled: new[] { "u1", "u2" }).Baseline.RestrictedPolicyIds.Should().Equal("p-grupo");
        var all = Analyze(new[] { Mfa("p-todos", includeUsers: new[] { "All" }, excludeGroups: new[] { "g-bg" }) },
            Array.Empty<DirectoryRoleConfiguration>(), byGroup, enabled: new[] { "u1", "u2" });
        all.Baseline.BaselinePolicyIds.Should().BeEmpty();
        all.Baseline.Reach!["p-todos"].Should().Contain("nenhuma das 2 conta(s) habilitada(s) é alcançada");
    }

    [Fact]
    public void BaseMinima_CriterioRevisado_IdentificadoPelaVersao_SemReescreverFotografiaAntiga()
    {
        var d = KnightCatalog.Indicators.Single(i => i.Id == "AK-ENTRA-014");
        d.Version.Should().Be("3");
        d.ExpectedEvidence.Should().Contain("alcance demonstrado conta a conta").And.Contain("não alcança nenhuma conta");

        KnightControlPresentation For(string catalog) => KnightControlPresentations.For("AK-ENTRA-014", d.Category, d.Severity,
            KnightIndicatorStatus.Passed, KnightSourceType.MicrosoftEntraId, d.NistCodes, d.MitreTechniques, catalog);
        For(KnightCatalog.Version).Criterion.Should().Be(d.ExpectedEvidence);
        For("ak-knight-v10").Criterion.Should().BeNull("a fotografia v10 não recebe o critério que não aplicou");
    }

    [Fact]
    public void BaseMinima_DirigidaAGrupoComPopulacaoVazia_NaoAprovaPorAusenciaDeContas()
    {
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }) }, Array.Empty<DirectoryRoleConfiguration>(),
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1") }, enabled: Array.Empty<string>());
        a.Baseline.BaselinePolicyIds.Should().BeEmpty();
        a.Baseline.UnresolvedPolicyIds.Should().Equal("p-grupo");
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Outcome.Should().Be(KnightObservationOutcome.Missing);
        a.Baseline.InconclusiveReason.Should().Contain("nenhuma conta habilitada");
    }

    [Theory]
    [InlineData(EntraGroupMembershipState.Incomplete)]
    [InlineData(EntraGroupMembershipState.Denied)]
    public void BaseMinima_LeituraIncompleta_PreservaAIncerteza(EntraGroupMembershipState state)
    {
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }) }, Array.Empty<DirectoryRoleConfiguration>(),
            new[] { Group("g-todos", state, "u1", "u2") }, enabled: new[] { "u1", "u2", "u3" });
        a.Baseline.UnresolvedPolicyIds.Should().Equal("p-grupo");
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Outcome.Should().Be(KnightObservationOutcome.Missing);
        a.Baseline.InconclusiveReason.Should().Contain("alcance não comprovado para 1 de 3");
    }

    [Fact]
    public void BaseMinima_SemEnumeracaoCompleta_NaoResolvida()
    {
        var a = Analyze(new[] { Mfa("p-grupo", includeGroups: new[] { "g-todos" }) }, Array.Empty<DirectoryRoleConfiguration>(),
            new[] { Group("g-todos", EntraGroupMembershipState.Complete, "u1") }, enabled: null);
        Signal(a, KnightSignalKey.BaselineMfaPolicies).Outcome.Should().Be(KnightObservationOutcome.Missing, "sem a população não há comparação");
        a.Baseline.InconclusiveReason.Should().Contain("sem a enumeração completa das contas habilitadas");
    }

    [Fact]
    public void DocumentosDoAdm_PapelV1EGrupo_IdaEVolta()
    {
        var group = Group("g-adm", EntraGroupMembershipState.Incomplete, "u2", "u1") with { Detail = "teto", HttpStatus = null };
        var config = new KnightDirectoryConfiguration(new[] { Mfa("p") }, new[] { RoleV2(Ga, "GA", new[] { "u1" }, groups: new[] { "g-adm" }) },
            new[] { group }, new[] { "u1", "u2" });
        var observed = DirectoryConfigurationDocuments.ToObserved(config);
        observed.Single(o => o.Kind == ConfigurationObjectKind.DirectoryRole).SchemaVersion.Should().Be(DirectoryRoleConfiguration.SchemaVersion);
        observed.Single(o => o.Kind == ConfigurationObjectKind.DirectoryGroupMembership).SchemaVersion.Should().Be(EntraGroupMembership.SchemaVersion);

        var back = DirectoryConfigurationDocuments.FromObserved(observed, true, true, groupsCollected: true, enabledAccountIds: new[] { "u1", "u2" });
        back.PrivilegedRoles!.Single().GroupMemberIds.Should().Equal("g-adm");
        back.GroupMemberships!.Single().State.Should().Be(EntraGroupMembershipState.Incomplete);
        back.GroupMemberships!.Single().Members.Select(m => m.Id).Should().Equal("u1", "u2");
        back.EnabledAccountIds.Should().Equal("u1", "u2");

        // Um papel gravado na v1 (antes deste pacote) continua legível e sem tipos.
        var v1 = new IdentityObservedConfiguration(ConfigurationObjectKind.DirectoryRole, Ga, "GA", DirectoryRoleConfiguration.SchemaVersionV1,
            """{"templateId":"62e90394-69f5-4237-9190-012177145e10","displayName":"GA","memberCount":2,"userMemberIds":["u1"]}""");
        var old = DirectoryConfigurationDocuments.FromObserved(new[] { v1 }, false, true);
        old.PrivilegedRoles!.Single().MemberTypesCaptured.Should().BeFalse();
        old.GroupMemberships.Should().BeNull("aquisição sem a capacidade: pertencimento não coletado, nunca vazio");
    }

    // ===== 2) Percurso inteiro: Graph sintético → coletor → ADM → avaliação ========================================

    /// <summary>Locatário com grupos atribuídos a papéis, aplicação em papel e política de MFA dirigida a grupo.</summary>
    internal sealed class GroupTenant
    {
        public string[] Todos { get; init; } = { "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8", "u9" };
        public string[] Enabled { get; init; } = { "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8", "u9" };
        public bool PolicyToAllUsersExcludingBreakGlass { get; init; }
        public bool HiddenExclusion { get; init; }
        public bool PagedAdmins { get; init; } = true;
        public string ExtraPolicies { get; init; } = "";
        public List<string> Requests { get; } = new();

        private static string J(object o) => JsonSerializer.Serialize(o);
        private static object U(string id) => new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.user", ["id"] = id, ["displayName"] = "Pessoa " + id, ["userPrincipalName"] = id + "@demo.example.com",
        };

        public (HttpStatusCode, string)? Route(string url)
        {
            lock (Requests) Requests.Add(url);
            if (url.Contains("/directoryRoles?"))
                return (HttpStatusCode.OK, J(new { value = new[]
                {
                    new { id = "r-ga", displayName = "Global Administrator", roleTemplateId = Ga },
                    new { id = "r-sa", displayName = "Security Administrator", roleTemplateId = Sa },
                    new { id = "r-dev", displayName = "Application Developer", roleTemplateId = Dev },
                } }));
            if (url.Contains("/directoryRoles/r-ga/members"))
                return (HttpStatusCode.OK, J(new { value = new object[]
                {
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = "u1", ["userType"] = "Member",
                        ["displayName"] = "Ana Prado", ["userPrincipalName"] = "u1@demo.example.com",
                        ["signInActivity"] = new { lastSignInDateTime = DateTimeOffset.UtcNow.AddDays(-2).ToString("o") } },
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g-ga", ["displayName"] = "AG-Administradores-global" },
                } }));
            if (url.Contains("/directoryRoles/r-dev/members"))
                return (HttpStatusCode.OK, J(new { value = new object[]
                {
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g-dev", ["displayName"] = "Devops App Administrators" },
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.servicePrincipal", ["id"] = "sp-pipeline", ["displayName"] = "Pipeline" },
                } }));
            if (url.Contains("/groups/g-ga/transitiveMembers"))
                return url.Contains("skiptoken=2") || !PagedAdmins
                    ? (HttpStatusCode.OK, J(new { value = new[] { U("u8") } }))
                    : (HttpStatusCode.OK, J(new Dictionary<string, object?>
                    {
                        ["value"] = new[] { U("u7") },
                        ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/groups/g-ga/transitiveMembers?$skiptoken=2",
                    }));
            if (url.Contains("/groups/g-dev/transitiveMembers")) return (HttpStatusCode.OK, J(new { value = new[] { U("u9") } }));
            if (url.Contains("/groups/g-todos/transitiveMembers")) return (HttpStatusCode.OK, J(new { value = Todos.Select(U).ToArray() }));
            if (url.Contains("/groups/g-bg/transitiveMembers"))
                return HiddenExclusion
                    ? EntraConfigurationScenario.Forbidden()
                    : (HttpStatusCode.OK, J(new { value = new[] { U("u-bg") } }));
            if (url.Contains("/groups/") && url.Contains("$select=id,displayName"))
                return (HttpStatusCode.OK, J(new { id = url.Split('/')[5].Split('?')[0], displayName = "Grupo " + url.Split('/')[5].Split('?')[0] }));
            if (url.Contains("/users?") && url.Contains("id in (") && url.Contains("userType"))
            {
                var inner = url[(url.IndexOf("id in (", StringComparison.Ordinal) + 7)..];
                inner = inner[..inner.IndexOf(')')];
                return (HttpStatusCode.OK, J(new { value = inner.Split(',').Select(x => x.Trim().Trim('\'')).Select(id => new Dictionary<string, object?>
                {
                    ["id"] = id, ["userType"] = "Member", ["displayName"] = "Pessoa " + id, ["userPrincipalName"] = id + "@demo.example.com",
                    ["signInActivity"] = new { lastSignInDateTime = DateTimeOffset.UtcNow.AddDays(-3).ToString("o") },
                }).ToArray() }));
            }
            if (url.Contains("userRegistrationDetails"))
                return (HttpStatusCode.OK, J(new { value = Todos.Concat(new[] { "u-bg" }).Distinct().Select(id => new
                {
                    id, isMfaCapable = true, isMfaRegistered = true, userType = "member", userPrincipalName = id + "@demo.example.com", userDisplayName = "Pessoa " + id,
                }).ToArray() }));
            if (url.Contains("accountEnabled eq true"))
                return (HttpStatusCode.OK, J(new { value = Enabled.Select(id => new { id, userPrincipalName = id + "@demo.example.com", displayName = "Pessoa " + id }).ToArray() }));
            if (url.Contains("/beta/users/") && url.EndsWith("/authentication/requirements"))
                return (HttpStatusCode.OK, J(new Dictionary<string, object?> { ["perUserMfaState"] = "disabled" }));
            if (url.Contains("conditionalAccess/policies"))
                return (HttpStatusCode.OK, PolicyToAllUsersExcludingBreakGlass
                    ? $$$"""
                        {"value":[
                          {"id":"p-todos","displayName":"Exigir MFA para todos os usuários","state":"enabled",
                           "conditions":{"users":{"includeUsers":["All"],"excludeGroups":["g-bg"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
                           "grantControls":{"operator":"OR","builtInControls":["mfa"]}}
                        ]}
                        """
                    : $$$"""
                        {"value":[
                          {"id":"p-grupo","displayName":"Exigir a autenticação multifator para todos os usuários","state":"enabled",
                           "conditions":{"users":{"includeGroups":["g-todos"],"excludeGroups":["g-bg"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
                           "grantControls":{"operator":"OR","builtInControls":["mfa"]}},
                          {"id":"p-admins","displayName":"Require multifactor authentication for admins","state":"enabled",
                           "conditions":{"users":{"includeRoles":["{{{Ga}}}","{{{Sa}}}","{{{Dev}}}"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
                           "grantControls":{"operator":"OR","builtInControls":["mfa"]}}{{{ExtraPolicies}}}
                        ]}
                        """);
            return null;
        }
    }

    [Fact]
    public async Task GruposAtribuidosAPapeis_ResolvidosPeloPertencimento_Ent001E011PassamASerAvaliados()
    {
        var tenant = new GroupTenant();
        var (run, db) = await RunAsync(tenant);

        run.Capabilities.Single(c => c.Capability == KnightCapability.EntraGroupMembership).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Indicator(run, "AK-ENTRA-001").Status.Should().NotBe(KnightIndicatorStatus.NotEvaluated, "os grupos dos papéis foram resolvidos");
        Indicator(run, "AK-ENTRA-011").Status.Should().NotBe(KnightIndicatorStatus.NotEvaluated);
        tenant.Requests.Should().Contain(u => u.Contains("/groups/g-ga/transitiveMembers?$skiptoken=2"), "a paginação é seguida");
        tenant.Requests.Count(u => u.Contains("/groups/g-bg/transitiveMembers")).Should().Be(1, "cada grupo é lido uma vez por coleta");

        // O ADM guarda o papel v2 (membros por tipo) e o pertencimento de cada grupo com procedência e completude.
        var docs = await Observations(db, run.Id);
        var ga = Deserialize<DirectoryRoleConfiguration>(docs.Single(d => d.Kind == ConfigurationObjectKind.DirectoryRole && d.ExternalId == Ga));
        ga.UserMemberIds.Should().Equal("u1");
        ga.GroupMemberIds.Should().Equal("g-ga");
        var dev = Deserialize<DirectoryRoleConfiguration>(docs.Single(d => d.Kind == ConfigurationObjectKind.DirectoryRole && d.ExternalId == Dev));
        dev.ServicePrincipalMemberIds.Should().Equal("sp-pipeline");

        var gGa = Deserialize<EntraGroupMembership>(docs.Single(d => d.Kind == ConfigurationObjectKind.DirectoryGroupMembership && d.ExternalId == "g-ga"));
        gGa.State.Should().Be(EntraGroupMembershipState.Complete);
        gGa.Members.Select(m => m.Id).Should().Equal("u7", "u8");
        gGa.Members.Should().OnlyContain(m => m.DisplayName != null, "conta privilegiada é nomeada");
        gGa.ReferencedBy.Should().Contain("role:" + Ga);
        gGa.Endpoint.Should().Be("/v1.0/groups/g-ga/transitiveMembers");
        var todos = Deserialize<EntraGroupMembership>(docs.Single(d => d.ExternalId == "g-todos" && d.Kind == ConfigurationObjectKind.DirectoryGroupMembership));
        todos.ReferencedBy.Should().Equal("policy:p-grupo:include");
        todos.Members.Where(m => m.Id is "u4" or "u5").Should().OnlyContain(m => m.DisplayName == null,
            "membro que não é privilegiado fica só pelo id — sem dado pessoal a mais no ADM");

    }

    [Fact]
    public async Task MfaAdministrativa_PorPertencimento_AvaliadaComOsGruposDasPoliticas()
    {
        var (run, _) = await RunAsync(new GroupTenant());

        // u7/u8 detêm GA via grupo: a política por papel não comprova, mas a dirigida a g-todos (lido, completo) comprova.
        var admin = Indicator(run, "AK-ENTRA-008");
        admin.Status.Should().Be(KnightIndicatorStatus.Passed, admin.NotEvaluatedReason ?? admin.Evidence);
    }

    [Fact]
    public async Task ExclusaoComPertencimentoOculto_FicaDesconhecida_ComOGrupoNomeado()
    {
        var (run, db) = await RunAsync(new GroupTenant { HiddenExclusion = true });

        var cap = run.Capabilities.Single(c => c.Capability == KnightCapability.EntraGroupMembership);
        cap.Outcome.Should().Be(KnightCapabilityOutcome.Collected, "os demais grupos foram lidos; o recusado guarda o próprio desfecho");
        cap.Detail.Should().Contain("Member.Read.Hidden").And.Contain("HTTP 403");
        var bg = Deserialize<EntraGroupMembership>((await Observations(db, run.Id)).Single(d => d.ExternalId == "g-bg"));
        bg.State.Should().Be(EntraGroupMembershipState.Denied);
        bg.HttpStatus.Should().Be(403);

        var baseline = Indicator(run, "AK-ENTRA-014");
        baseline.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "a exclusão não lida não é ignorada");
        baseline.NotEvaluatedReason.Should().Contain("Grupo g-bg").And.Contain("leitura recusada");
    }

    [Fact]
    public async Task BaseMinima_DirigidaAGrupoQueNaoAlcancaTodasAsContas_ReprovaComOsNumeros()
    {
        var (run, _) = await RunAsync(new GroupTenant { Todos = new[] { "u1", "u2", "u3", "u4", "u5" } });

        var baseline = Indicator(run, "AK-ENTRA-014");
        baseline.Status.Should().Be(KnightIndicatorStatus.Exposed, baseline.NotEvaluatedReason ?? baseline.Evidence);
    }

    [Fact]
    public async Task BaseMinima_QuantidadesIguaisIdsDiferentes_Reprova_PelaIdentidade()
    {
        // Nove no grupo, nove habilitadas — mas o grupo tem uma conta desabilitada no lugar de u9.
        var (run, _) = await RunAsync(new GroupTenant { Todos = new[] { "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8", "u-desabilitada" } });
        Indicator(run, "AK-ENTRA-014").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public async Task BaseMinima_GrupoAlcancaCadaContaHabilitada_Aprova_ComAEvidenciaRelidaDoAdm()
    {
        var (run, db) = await RunAsync(new GroupTenant());
        var baseline = Indicator(run, "AK-ENTRA-014");
        baseline.Status.Should().Be(KnightIndicatorStatus.Passed, baseline.NotEvaluatedReason ?? baseline.Evidence);

        // A população veio da MESMA enumeração (sem consulta por usuário a mais) e foi preservada no ADM, com o teto dito.
        var perUser = Deserialize<EntraPerUserMfaInventory>((await Observations(db, run.Id)).Single(d => d.Kind == ConfigurationObjectKind.PerUserMfaInventory));
        perUser.Truncated.Should().BeFalse();
        perUser.EnabledAccountIds.Should().BeEquivalentTo(new GroupTenant().Enabled);
        var objects = await db.KnightAffectedObjects.AsNoTracking().Where(o => o.RunId == run.Id && o.IndicatorId == "AK-ENTRA-014").ToListAsync();
        objects.Should().Contain(o => o.DisplayName == "Exigir a autenticação multifator para todos os usuários"
            && o.Detail!.Contains("9 de 9 conta(s) habilitada(s) alcançada(s)"));
    }

    [Fact]
    public async Task BaseMinima_TodosComExclusaoDeGrupoLida_Aprova()
    {
        var (run, _) = await RunAsync(new GroupTenant { PolicyToAllUsersExcludingBreakGlass = true });
        var baseline = Indicator(run, "AK-ENTRA-014");
        baseline.Status.Should().Be(KnightIndicatorStatus.Passed, baseline.NotEvaluatedReason ?? baseline.Evidence);
    }

    /// <summary>[AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Bloqueios dirigidos ao grupo g-todos (fluxo de código e autenticação legada).</summary>
    private const string GroupScopedBlocks = """
        ,
          {"id":"p-codigo","displayName":"Bloquear o fluxo de código de dispositivo","state":"enabled",
           "conditions":{"users":{"includeGroups":["g-todos"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"],
                         "authenticationFlows":{"transferMethods":"deviceCodeFlow"}},
           "grantControls":{"operator":"OR","builtInControls":["block"]}},
          {"id":"p-legada","displayName":"Bloquear a autenticação legada","state":"enabled",
           "conditions":{"users":{"includeGroups":["g-todos"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["exchangeActiveSync","other"]},
           "grantControls":{"operator":"OR","builtInControls":["block"]}}
        """;

    [Fact]
    public async Task BloqueiosDirigidosAGrupo_PertencimentoCompletoNoAdm_AvaliadosContaAConta()
    {
        var (run, db) = await RunAsync(new GroupTenant { ExtraPolicies = GroupScopedBlocks });

        // O pertencimento que decide é o preservado no ADM, com a procedência das duas políticas novas.
        var todos = Deserialize<EntraGroupMembership>((await Observations(db, run.Id))
            .Single(d => d.ExternalId == "g-todos" && d.Kind == ConfigurationObjectKind.DirectoryGroupMembership));
        todos.State.Should().Be(EntraGroupMembershipState.Complete);
        todos.ReferencedBy.Should().Contain(new[] { "policy:p-codigo:include", "policy:p-legada:include" });

        var deviceCode = Indicator(run, "AK-ENTRA-066");
        deviceCode.Status.Should().Be(KnightIndicatorStatus.Passed, deviceCode.NotEvaluatedReason ?? deviceCode.Evidence);
        var legacy = Indicator(run, "AK-ENTRA-007");
        legacy.Status.Should().Be(KnightIndicatorStatus.Passed, legacy.NotEvaluatedReason ?? legacy.Evidence);

        var objects = await db.KnightAffectedObjects.AsNoTracking()
            .Where(o => o.RunId == run.Id && (o.IndicatorId == "AK-ENTRA-066" || o.IndicatorId == "AK-ENTRA-007")).ToListAsync();
        objects.Should().Contain(o => o.IndicatorId == "AK-ENTRA-066" && o.ExternalId == "p-codigo" && o.Detail!.Contains("9 de 9 conta(s) habilitada(s)"));
        objects.Should().Contain(o => o.IndicatorId == "AK-ENTRA-007" && o.ExternalId == "p-legada" && o.Detail!.Contains("9 de 9 conta(s) habilitada(s)"));
    }

    [Fact]
    public async Task BloqueiosDirigidosAGrupo_GrupoNaoAlcancaTodasAsContas_ReprovaComOsNumeros()
    {
        var (run, _) = await RunAsync(new GroupTenant { ExtraPolicies = GroupScopedBlocks, Todos = new[] { "u1", "u2", "u3", "u4", "u5" } });

        var deviceCode = Indicator(run, "AK-ENTRA-066");
        deviceCode.Status.Should().Be(KnightIndicatorStatus.Exposed, deviceCode.NotEvaluatedReason ?? deviceCode.Evidence);
        deviceCode.Evidence.Should().Contain("4 de 9 conta(s) habilitada(s)");
        Indicator(run, "AK-ENTRA-007").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public async Task PaginacaoInterrompida_GrupoIncompleto_PreservaOsListados_ENaoAprova()
    {
        var tenant = new GroupTenant();
        var handler = new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant, tenant.Route).Handler();
        // Teto de UMA página: o grupo g-ga (duas páginas) fica incompleto; os demais (uma página) completos.
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler), maxPages: 1));
        var result = await collector.CollectAsync(new KnightCollectionContext(Guid.NewGuid(),
            new KnightEntraIdConfiguration("11111111-2222-3333-4444-555555555555", "app", "segredo")));

        // AK-ENTRA-002 continua contando IDENTIDADES (o grupo é uma), agora com o tipo correto em vez de "não identificado".
        var privileged = result.AffectedObjectSets.Single(x => x.Signal == KnightSignalKey.PrivilegedAccountsTotal).Objects;
        privileged.Where(o => o.ExternalId is "g-ga" or "g-dev").Should().HaveCount(2).And.OnlyContain(o => o.Kind == KnightAffectedObjectKind.Group);
        privileged.Should().NotContain(o => o.Kind == KnightAffectedObjectKind.Unknown);
        result.Facts.Get(KnightSignalKey.PrivilegedAccountsTotal).Count.Should().Be(privileged.Count);

        var gGa = result.DirectoryConfiguration!.GroupMemberships!.Single(g => g.GroupId == "g-ga");
        gGa.State.Should().Be(EntraGroupMembershipState.Incomplete);
        gGa.Members.Select(m => m.Id).Should().Equal("u7");
        result.Facts.Get(KnightSignalKey.PrivilegedAccountsWithoutMfa).Outcome.Should().Be(KnightObservationOutcome.Missing);
        result.Facts.Get(KnightSignalKey.PrivilegedAccountsWithoutMfa).MissingReason.Should().Contain("AG-Administradores-global")
            .And.Contain("lido em parte");
    }

    // ---- Infra ---------------------------------------------------------------------------------------------

    private static KnightIndicatorView Indicator(KnightAssessment run, string id) => run.Indicators.Single(i => i.IndicatorId == id);

    private static T Deserialize<T>(IdentityConfigurationObservation o) =>
        JsonSerializer.Deserialize<T>(o.ConfigurationJson, IdentityEvidenceFactsJson.Options)!;

    private async Task<(KnightAssessment Run, AegisScoreDbContext Db)> RunAsync(GroupTenant tenant)
    {
        var tenantId = Guid.NewGuid();
        await SeedAsync(tenantId);
        var db = NewContext(tenantId);
        var scenario = new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant, tenant.Route);
        var run = await KnightMulticloudReportTests.ServiceFor(db, tenantId, scenario.Handler()).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        foreach (var c in run.Capabilities.Where(c => c.Capability is KnightCapability.EntraGroupMembership or KnightCapability.PrivilegedRoleInventory))
            _output.WriteLine($"cap {c.Capability} {c.Outcome} {c.Detail}");
        foreach (var i in run.Indicators.Where(i => i.IndicatorId is "AK-ENTRA-001" or "AK-ENTRA-002" or "AK-ENTRA-007" or "AK-ENTRA-008" or "AK-ENTRA-011" or "AK-ENTRA-014" or "AK-ENTRA-066"))
            _output.WriteLine($"{i.IndicatorId} {i.Status} {i.NotEvaluatedReason ?? i.Evidence}");
        return (run, db);
    }

    private static async Task<List<IdentityConfigurationObservation>> Observations(AegisScoreDbContext db, Guid runId)
    {
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        return await db.IdentityConfigurationObservations.AsNoTracking().Where(o => o.AcquisitionId == run.IdentityAcquisitionId).ToListAsync();
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync(Guid tenantId)
    {
        await using (var db = NewContext(null))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Cliente Demo", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
