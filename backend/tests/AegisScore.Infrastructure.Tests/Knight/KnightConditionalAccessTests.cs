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
/// Prova, caso a caso, o que a leitura por flags errava: política dirigida a PAPEL cobre quem detém o papel;
/// somente relatório e desabilitada não impõem; MFA como alternativa (OU) não é exigência; condição de localização
/// estreita a exigência; política por GRUPO não resolvido deixa o resultado inconclusivo. E, nas regras: security
/// defaults NÃO verificado nunca autoriza reprovar.
///
/// Revisão corretiva: EXCLUSÕES nunca viram cobertura. Política existente, alvo declarado e cobertura comprovada
/// são coisas distintas: exceção explícita sem outra política que a cubra deixa 007/008 sem conclusão (nem
/// aprovado, nem reprovado); membro que nenhuma política alcança — ou papel sem nenhum membro coberto — é lacuna
/// comprovada; outra política que cubra a exceção preserva a aprovação. Base mínima (014) exige alcance declarado
/// amplo, não "qualquer política".
/// </summary>
public sealed class KnightConditionalAccessTests
{
    private const string GlobalAdmin = "62e90394-69f5-4237-9190-012177145e10";
    private const string SecurityAdmin = "194ae4cb-b126-40b2-bd5b-6091b380977d";
    private static readonly string[] BothLegacy = { "exchangeActiveSync", "other" };

    private static DirectoryRoleConfiguration Role(string templateId, string name, params string[] users) =>
        new(templateId, name, users.Length, users);

    private static ConditionalAccessPolicyConfiguration Policy(
        string id,
        ConditionalAccessPolicyState state = ConditionalAccessPolicyState.Enabled,
        string[]? includeUsers = null, string[]? excludeUsers = null,
        string[]? includeRoles = null, string[]? excludeRoles = null,
        string[]? includeGroups = null, string[]? excludeGroups = null,
        string[]? apps = null, string[]? excludeApps = null, string[]? clients = null,
        bool location = false, string? op = null, string[]? controls = null, string? strength = null,
        bool excludeGuests = false) =>
        new(id, "Política " + id, state, state.ToString(),
            includeUsers ?? Array.Empty<string>(), excludeUsers ?? Array.Empty<string>(),
            includeGroups ?? Array.Empty<string>(), excludeGroups ?? Array.Empty<string>(),
            includeRoles ?? Array.Empty<string>(), excludeRoles ?? Array.Empty<string>(),
            false, excludeGuests,
            apps ?? new[] { "All" }, excludeApps ?? Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(),
            clients ?? Array.Empty<string>(),
            false, location, false, false, false,
            op, controls ?? new[] { "mfa" }, strength, strength is null ? null : "Phishing-resistant MFA");

    private static ConditionalAccessPolicyConfiguration Block(
        string id, string[]? includeUsers = null, string[]? excludeUsers = null, string[]? excludeGroups = null,
        string[]? clients = null, bool location = false,
        ConditionalAccessPolicyState state = ConditionalAccessPolicyState.Enabled) =>
        Policy(id, state, includeUsers: includeUsers ?? new[] { "All" }, excludeUsers: excludeUsers,
            excludeGroups: excludeGroups, clients: clients ?? BothLegacy, location: location, controls: new[] { "block" });

    private static ConditionalAccessAnalysis Analyze(
        IReadOnlyList<ConditionalAccessPolicyConfiguration> policies, IReadOnlyList<DirectoryRoleConfiguration>? roles) =>
        ConditionalAccessAnalyzer.Analyze(new KnightDirectoryConfiguration(policies, roles))!;

    private static readonly DirectoryRoleConfiguration[] Roles =
    {
        Role(GlobalAdmin, "Global Administrator", "u-ga-1", "u-bg-1"),
        Role(SecurityAdmin, "Security Administrator", "u-sa-1"),
    };

    private static KnightObservation Signal(ConditionalAccessAnalysis a, KnightSignalKey key) =>
        ConditionalAccessAnalyzer.ToObservations(a, null).Single(o => o.Key == key);

    /// <summary>Veredito do indicador a partir da MESMA análise (security defaults comprovadamente desligados, salvo indicação).</summary>
    private static KnightEvaluatedIndicator Verdict(string id, ConditionalAccessAnalysis a, bool? defaults = false) =>
        Eval(id, ConditionalAccessAnalyzer.ToObservations(a, null).Append(Defaults(defaults)).ToArray());

    // ---- MFA administrativa: o que conta como cobertura --------------------------------------------

    [Fact]
    public void PoliticaDirigidaAosPapeis_SemExclusao_CobreOsPapeis_EAprova()
    {
        var a = Analyze(new[] { Policy("p-admins", includeRoles: new[] { GlobalAdmin, SecurityAdmin }) }, Roles);

        a.AdminMfa.Evaluable.Should().BeTrue();
        a.AdminMfa.UncoveredRoleCount.Should().Be(0, "a política mira os dois papéis, em todas as aplicações");
        a.AdminMfa.Roles.Should().OnlyContain(r => r.State == RoleMfaCoverageState.Covered && r.ExceptionMemberIds.Count == 0);
        Verdict("AK-ENTRA-008", a).Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public void PoliticaDirigidaAosPapeis_ComContaExcluida_NaoEhCobertura_EhExcecaoSemConclusao()
    {
        // Antes: o papel era "coberto", a contagem zerava e AK-ENTRA-008 aprovava com "todos os papéis são cobertos".
        var a = Analyze(new[] { Policy("p-admins", includeRoles: new[] { GlobalAdmin, SecurityAdmin }, excludeUsers: new[] { "u-bg-1" }) }, Roles);

        var ga = a.AdminMfa.Roles.Single(r => r.Role.TemplateId == GlobalAdmin);
        ga.State.Should().Be(RoleMfaCoverageState.CoveredWithExceptions);
        ga.ExceptionMemberIds.Should().Equal("u-bg-1");
        ga.Members.Single(m => m.MemberId == "u-bg-1").PolicyIds.Should().Equal("p-admins");
        a.AdminMfa.Roles.Single(r => r.Role.TemplateId == SecurityAdmin).State.Should().Be(RoleMfaCoverageState.Covered);

        a.AdminMfa.Evaluable.Should().BeFalse("a exceção não é irregular, mas também não comprova proteção");
        a.AdminMfa.InconclusiveReason.Should().Contain("não é irregular por si").And.Contain("não comprova proteção")
            .And.Contain("Global Administrator");
        Signal(a, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).IsCollected.Should().BeFalse("nunca zero por suposição");

        var e = Verdict("AK-ENTRA-008", a);
        e.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "nem aprovação nem reprovação automática");
        e.NotEvaluatedReason.Should().Contain("excluída explicitamente");
    }

    [Fact]
    public void PoliticaParaTodos_QueExcluiTodosOsAdministradores_EhLacunaComprovada()
    {
        var a = Analyze(new[] { Policy("p-all", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-ga-1", "u-bg-1", "u-sa-1" }) }, Roles);

        a.AdminMfa.UniversalPolicyIds.Should().BeEmpty("política com exclusão não é universal");
        a.AdminMfa.Evaluable.Should().BeTrue();
        a.AdminMfa.UncoveredRoleCount.Should().Be(2, "nenhum membro de nenhum papel é alcançado");
        a.AdminMfa.Roles.Should().OnlyContain(r => r.State == RoleMfaCoverageState.NotCovered);
        a.AdminMfa.Roles.SelectMany(r => r.ExceptionMemberIds).Should().BeEquivalentTo("u-ga-1", "u-bg-1", "u-sa-1");

        var e = Verdict("AK-ENTRA-008", a);
        e.Status.Should().Be(KnightIndicatorStatus.Exposed);
        e.AffectedObjectCount.Should().Be(2);
        e.Evidence.Should().NotContain("Todos os");
    }

    [Fact]
    public void PoliticaDosPapeis_QueExcluiGrupoDesconhecido_FicaSemConclusao()
    {
        var a = Analyze(new[] { Policy("p-admins", includeRoles: new[] { GlobalAdmin, SecurityAdmin }, excludeGroups: new[] { "grp-x" }) }, Roles);

        a.AdminMfa.Roles.Should().OnlyContain(r => r.State == RoleMfaCoverageState.Unresolved);
        a.AdminMfa.Evaluable.Should().BeFalse();
        a.AdminMfa.InconclusiveReason.Should().Contain("grupos");
        Verdict("AK-ENTRA-008", a).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public void InclusaoNominal_ComUmAdministradorExcluido_NaoAprova()
    {
        var a = Analyze(new[] { Policy("p-nominal", includeUsers: new[] { "u-ga-1", "u-bg-1", "u-sa-1" }, excludeUsers: new[] { "u-bg-1" }) }, Roles);

        var ga = a.AdminMfa.Roles.Single(r => r.Role.TemplateId == GlobalAdmin);
        ga.State.Should().Be(RoleMfaCoverageState.CoveredWithExceptions, "a exclusão prevalece sobre a inclusão nominal");
        ga.ExceptionMemberIds.Should().Equal("u-bg-1");
        Verdict("AK-ENTRA-008", a).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public void InclusaoNominal_QueOmiteUmAdministrador_EhLacunaComprovada_ComQuemFicouDeFora()
    {
        // Antes: "não resolvido". Usuário nominal é conhecido — quem não está na lista não é alcançado.
        var a = Analyze(new[] { Policy("p-nominal", includeUsers: new[] { "u-ga-1", "u-sa-1" }) }, Roles);

        var ga = a.AdminMfa.Roles.Single(r => r.Role.TemplateId == GlobalAdmin);
        ga.State.Should().Be(RoleMfaCoverageState.NotCovered);
        ga.UncoveredMemberIds.Should().Equal("u-bg-1");
        a.AdminMfa.UncoveredRoleCount.Should().Be(1);
        Verdict("AK-ENTRA-008", a).Should().Match<KnightEvaluatedIndicator>(e => e.Status == KnightIndicatorStatus.Exposed && e.AffectedObjectCount == 1);
    }

    [Fact]
    public void ExcecaoCobertaPorOutraPolitica_PreservaAprovacao()
    {
        var nominal = Analyze(new[]
        {
            Policy("p1", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-bg-1" }),
            Policy("p2", includeUsers: new[] { "u-bg-1" }),
        }, Roles);
        nominal.AdminMfa.Roles.Should().OnlyContain(r => r.State == RoleMfaCoverageState.Covered);
        nominal.AdminMfa.Roles.SelectMany(r => r.ExceptionMemberIds).Should().BeEmpty("p2 cobre a conta que p1 exclui");
        nominal.AdminMfa.Roles.Single(r => r.Role.TemplateId == GlobalAdmin).Members
            .Single(m => m.MemberId == "u-bg-1").PolicyIds.Should().Equal("p2");
        Verdict("AK-ENTRA-008", nominal).Status.Should().Be(KnightIndicatorStatus.Passed);

        var porPapel = Analyze(new[]
        {
            Policy("p1", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-bg-1" }),
            Policy("p2", includeRoles: new[] { GlobalAdmin }),
        }, Roles);
        Verdict("AK-ENTRA-008", porPapel).Status.Should().Be(KnightIndicatorStatus.Passed, "a conta detém o papel que p2 inclui");

        var somenteRelatorio = Analyze(new[]
        {
            Policy("p1", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-bg-1" }),
            Policy("p2", ConditionalAccessPolicyState.ReportOnly, includeUsers: new[] { "u-bg-1" }),
        }, Roles);
        Verdict("AK-ENTRA-008", somenteRelatorio).Status.Should().Be(KnightIndicatorStatus.NotEvaluated,
            "somente relatório não cobre a exceção");
    }

    [Fact]
    public void MembroDeDoisPapeis_EhCobertoPelaPoliticaDoPapelQueDetem()
    {
        // O acesso condicional vale para a PESSOA que detém o papel incluído — em todas as entradas dela.
        var roles = new[] { Role(GlobalAdmin, "Global Administrator", "u2"), Role(SecurityAdmin, "Security Administrator", "u2") };
        var a = Analyze(new[] { Policy("p", includeRoles: new[] { GlobalAdmin }) }, roles);

        a.AdminMfa.Roles.Should().OnlyContain(r => r.State == RoleMfaCoverageState.Covered);
        Verdict("AK-ENTRA-008", a).Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public void ExclusaoDeConvidados_DeixaSemConclusao_PoisACondicaoNaoEhColetada()
    {
        var a = Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, excludeGuests: true) }, Roles);
        a.AdminMfa.Evaluable.Should().BeFalse();
        a.AdminMfa.Roles.Should().OnlyContain(r => r.State == RoleMfaCoverageState.Unresolved);
    }

    [Theory]
    [InlineData(ConditionalAccessPolicyState.ReportOnly)]
    [InlineData(ConditionalAccessPolicyState.Disabled)]
    public void SomenteRelatorioOuDesabilitada_NaoImpoe(ConditionalAccessPolicyState state)
    {
        var a = Analyze(new[] { Policy("p", state, includeUsers: new[] { "All" }) }, Roles);

        a.AdminMfa.UncoveredRoleCount.Should().Be(2);
        a.Baseline.BaselinePolicyIds.Should().BeEmpty();
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
        var sa = a.AdminMfa.Roles.Single(r => r.State == RoleMfaCoverageState.NotCovered);
        sa.Role.TemplateId.Should().Be(SecurityAdmin);
        sa.Notes.Should().Contain(n => n.Contains("exclui este papel"));
    }

    [Fact]
    public void PoliticaPorGrupo_SemOutraCobertura_FicaInconclusiva()
    {
        var a = Analyze(new[] { Policy("p", includeGroups: new[] { "grp-admins" }) }, Roles);

        a.AdminMfa.Evaluable.Should().BeFalse("o pertencimento ao grupo não é coletado — não se afirma nem se nega");
        a.AdminMfa.InconclusiveReason.Should().Contain("grupos");
        Signal(a, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy).IsCollected.Should().BeFalse();
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
    public void SemInventarioDePapeis_SoPoliticaUniversalSemExclusaoConclui()
    {
        Analyze(new[] { Policy("p", includeUsers: new[] { "All" }) }, null).AdminMfa.Evaluable.Should().BeTrue();
        Analyze(new[] { Policy("p", includeRoles: new[] { GlobalAdmin }) }, null).AdminMfa.Evaluable.Should().BeFalse();

        var comExclusao = Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-bg-1" }) }, null);
        comExclusao.AdminMfa.Evaluable.Should().BeFalse("sem inventário, o excluído pode ser um administrador");
        comExclusao.AdminMfa.InconclusiveReason.Should().Contain("exclusões");
    }

    [Fact]
    public void PoliticasNaoColetadas_ViramSinaisAusentes_NuncaNenhumaPolitica()
    {
        ConditionalAccessAnalyzer.Analyze(new KnightDirectoryConfiguration(null, Roles)).Should().BeNull();
        var obs = ConditionalAccessAnalyzer.ToObservations(null, "HTTP 403");
        obs.Should().OnlyContain(o => !o.IsCollected && o.MissingReason == "HTTP 403");
    }

    // ---- Autenticação legada -----------------------------------------------------------------------

    [Fact]
    public void BloqueioLegado_ExigeOsDoisTiposDeCliente_SomenteRelatorioEGrupoNaoBloqueiam()
    {
        Analyze(new[] { Block("p") }, Roles).LegacyAuth.Blocked.Should().BeTrue();
        Analyze(new[] { Block("p", clients: new[] { "exchangeActiveSync" }) }, Roles).LegacyAuth.Blocked.Should().BeFalse();
        Analyze(new[] { Block("p", state: ConditionalAccessPolicyState.ReportOnly) }, Roles).LegacyAuth.Blocked.Should().BeFalse();
        Analyze(new[] { Policy("p", includeGroups: new[] { "g" }, clients: BothLegacy, controls: new[] { "block" }) }, Roles)
            .LegacyAuth.Blocked.Should().BeFalse("bloquear só um grupo não bloqueia a autenticação legada do tenant");
    }

    [Theory]
    [InlineData("usuário")]
    [InlineData("grupo")]
    public void BloqueioLegadoComExclusao_NaoAfirmaTodosOsUsuarios_NemReprova(string exclusao)
    {
        // Antes: exclusões de usuário e de grupo eram ignoradas e AK-ENTRA-007 afirmava bloqueio "para todos".
        var a = Analyze(new[]
        {
            exclusao == "usuário" ? Block("p", excludeUsers: new[] { "u-bg-1" }) : Block("p", excludeGroups: new[] { "grp-x" }),
        }, Roles);

        a.LegacyAuth.Blocked.Should().BeFalse();
        a.LegacyAuth.ExceptionPolicyIds.Should().Equal("p");
        a.LegacyAuth.InconclusiveReason.Should().Contain("exceto").And.Contain("não é irregular por si");
        Signal(a, KnightSignalKey.LegacyAuthenticationBlocked).IsCollected.Should().BeFalse();

        var e = Verdict("AK-ENTRA-007", a);
        e.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        e.NotEvaluatedReason.Should().Contain(exclusao == "usuário" ? "usuário excluído" : "grupo excluído");
    }

    [Fact]
    public void BloqueioLegado_ExcecaoCobertaPorOutraPolitica_PreservaAprovacao()
    {
        var a = Analyze(new[] { Block("p1", excludeUsers: new[] { "u-bg-1" }), Block("p2", includeUsers: new[] { "u-bg-1" }) }, Roles);
        a.LegacyAuth.Blocked.Should().BeTrue();
        a.LegacyAuth.BlockingPolicyIds.Should().BeEquivalentTo("p1", "p2");
        Verdict("AK-ENTRA-007", a).Status.Should().Be(KnightIndicatorStatus.Passed);

        // Grupo excluído: coberto só por política SEM exclusões que inclua o grupo.
        var grupo = Analyze(new[] { Block("p1", excludeGroups: new[] { "grp-x" }), Policy("p2", includeGroups: new[] { "grp-x" }, clients: BothLegacy, controls: new[] { "block" }) }, Roles);
        grupo.LegacyAuth.Blocked.Should().BeTrue();

        var coberturaSoRelatorio = Analyze(new[] { Block("p1", excludeUsers: new[] { "u-bg-1" }), Block("p2", includeUsers: new[] { "u-bg-1" }, state: ConditionalAccessPolicyState.ReportOnly) }, Roles);
        coberturaSoRelatorio.LegacyAuth.Blocked.Should().BeFalse();
        coberturaSoRelatorio.LegacyAuth.InconclusiveReason.Should().NotBeNull();
    }

    [Fact]
    public void BloqueioDeTodosOsTiposDeCliente_AbrangeAAutenticacaoLegada()
    {
        // Antes: "all" não era reconhecido como alcançando os clientes legados — um bloqueio abrangente era ignorado.
        Analyze(new[] { Block("p", clients: new[] { "all" }) }, Roles).LegacyAuth.Blocked.Should().BeTrue();
        Analyze(new[] { Block("p", clients: Array.Empty<string>()) }, Roles).LegacyAuth.Blocked.Should()
            .BeTrue("sem a condição de clientes, a política vale para todos os tipos");

        var comLocalizacao = Analyze(new[] { Block("p", clients: new[] { "all" }, location: true) }, Roles);
        comLocalizacao.LegacyAuth.Blocked.Should().BeFalse("condição de localização estreita o bloqueio");
        comLocalizacao.LegacyAuth.PartialPolicyIds.Should().Equal("p");

        var comExcecao = Analyze(new[] { Block("p", clients: new[] { "all" }, excludeUsers: new[] { "u-bg-1" }) }, Roles);
        comExcecao.LegacyAuth.Blocked.Should().BeFalse();
        Verdict("AK-ENTRA-007", comExcecao).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- Base mínima (AK-ENTRA-014) ----------------------------------------------------------------

    [Fact]
    public void Baseline_PoliticaDeAlcanceRestrito_NaoSustentaConclusaoSobreOAmbiente()
    {
        // Antes: qualquer política habilitada exigindo MFA — mesmo para UM usuário e UMA aplicação — aprovava.
        var restrita = Analyze(new[] { Policy("p", includeUsers: new[] { "u-x" }, apps: new[] { "app-1" }) }, Roles);
        restrita.Baseline.BaselinePolicyIds.Should().BeEmpty();
        restrita.Baseline.RestrictedPolicyIds.Should().Equal("p");
        Verdict("AK-ENTRA-014", restrita).Status.Should().Be(KnightIndicatorStatus.Exposed);
        Verdict("AK-ENTRA-014", restrita, defaults: null).Status.Should().Be(KnightIndicatorStatus.NotEvaluated,
            "security defaults desconhecido continua desconhecido");
        Verdict("AK-ENTRA-014", restrita, defaults: true).Status.Should().Be(KnightIndicatorStatus.Passed);

        Verdict("AK-ENTRA-014", Analyze(new[] { Policy("p", includeRoles: new[] { GlobalAdmin }) }, Roles)).Status
            .Should().Be(KnightIndicatorStatus.Exposed, "só administradores não é base para o ambiente");
        Verdict("AK-ENTRA-014", Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, location: true) }, Roles)).Status
            .Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public void Baseline_AlcanceAmploDeclarado_Aprova_EGrupoFicaSemConclusao()
    {
        var ampla = Analyze(new[] { Policy("p", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-bg-1" }) }, Roles);
        ampla.Baseline.BaselinePolicyIds.Should().Equal("p");
        var e = Verdict("AK-ENTRA-014", ampla);
        e.Status.Should().Be(KnightIndicatorStatus.Passed);
        e.Evidence.Should().Contain("alvo declarado").And.Contain("não comprova a cobertura de cada conta");

        foreach (var porGrupo in new[]
                 {
                     Policy("p", includeGroups: new[] { "grp-todos" }),
                     Policy("p", includeUsers: new[] { "All" }, excludeGroups: new[] { "grp-x" }),
                 })
        {
            var a = Analyze(new[] { porGrupo }, Roles);
            a.Baseline.UnresolvedPolicyIds.Should().Equal("p");
            Verdict("AK-ENTRA-014", a).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "o alcance depende de grupo não coletado");
        }
    }

    // ---- Evidência, veredito e texto concordam -----------------------------------------------------

    private static IReadOnlyDictionary<string, KnightConfigurationObjects> Evidence(
        IReadOnlyList<ConditionalAccessPolicyConfiguration> policies, bool? defaults = false)
    {
        var analysis = Analyze(policies, Roles);
        var obs = ConditionalAccessAnalyzer.ToObservations(analysis, null).Append(Defaults(defaults)).ToList();
        var names = new KnightAffectedObjectEvidence(KnightSignalKey.PrivilegedAccountsTotal, new[]
        {
            new KnightAffectedObjectFact("u-ga-1", KnightAffectedObjectKind.User, "Ana Prado"),
            new KnightAffectedObjectFact("u-bg-1", KnightAffectedObjectKind.User, "Conta de emergência 1"),
            new KnightAffectedObjectFact("u-sa-1", KnightAffectedObjectKind.User, "Bruno Costa"),
        });
        var result = new KnightCollectionResult(
            KnightSourceType.MicrosoftEntraId, KnightSourceState.Completed, "Entra", new KnightFactSet(obs),
            Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow,
            AffectedObjects: new[] { names }, DirectoryConfiguration: new KnightDirectoryConfiguration(policies, Roles));
        return KnightConfigurationEvidence.Build(result);
    }

    [Fact]
    public void Evidencia_DaExcecao_NomeiaAConta_ENaoDescreveOPapelComoCoberto()
    {
        var map = Evidence(new[] { Policy("p-admins", includeRoles: new[] { GlobalAdmin, SecurityAdmin }, excludeUsers: new[] { "u-bg-1" }) });
        var admin = map[KnightConfigurationEvidence.AdminMfaIndicator];

        admin.Affected.Should().BeEmpty("sem lacuna comprovada, não há afetado — e o veredito não é exposição");
        admin.AffectedComplete.Should().BeFalse();
        admin.Limitation.Should().Contain("não é irregular por si");
        var ga = admin.Evidence.Single(o => o.ExternalId == GlobalAdmin);
        ga.Detail.Should().Contain("Conta de emergência 1").And.Contain("não é contado como coberto")
            .And.NotContain("Todos os");
        admin.Evidence.Single(o => o.ExternalId == SecurityAdmin).Detail.Should().StartWith("Todos os 1 membro(s)");
    }

    [Fact]
    public void Evidencia_DaLacuna_TemUmAfetadoPorPapelContado_ENomeiaQuemFicouDeFora()
    {
        var map = Evidence(new[] { Policy("p-all", includeUsers: new[] { "All" }, excludeUsers: new[] { "u-ga-1", "u-bg-1", "u-sa-1" }) });
        var admin = map[KnightConfigurationEvidence.AdminMfaIndicator];

        admin.Affected.Should().HaveCount(2, "a lista tem o tamanho da contagem do veredito");
        admin.AffectedComplete.Should().BeTrue();
        admin.Affected.Single(o => o.ExternalId == GlobalAdmin).Detail.Should()
            .Contain("Lacuna comprovada").And.Contain("Ana Prado").And.Contain("Conta de emergência 1");

        var legacy = Evidence(new[] { Block("p-legacy", excludeUsers: new[] { "u-bg-1" }) })[KnightConfigurationEvidence.LegacyAuthIndicator];
        legacy.Evidence.Single(o => o.ExternalId == "p-legacy").Detail.Should()
            .Contain("Conta de emergência 1").And.Contain("não é afirmado").And.NotContain("sem exclusões");
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
    public void Baseline_PoliticaAmplaAprova_SemEvidenciaNaoReprova()
    {
        Eval("AK-ENTRA-014", Defaults(false), KnightObservation.OfCount(KnightSignalKey.BaselineMfaPolicies, 1)).Status.Should().Be(KnightIndicatorStatus.Passed);
        Eval("AK-ENTRA-014", Defaults(false), KnightObservation.OfCount(KnightSignalKey.BaselineMfaPolicies, 0)).Status.Should().Be(KnightIndicatorStatus.Exposed);
        Eval("AK-ENTRA-014", Defaults(null), KnightObservation.OfCount(KnightSignalKey.BaselineMfaPolicies, 0)).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public void Catalogo_V3_TituloDoRegistroDeMfaAfirmaSoOQueMede()
    {
        KnightCatalog.Version.Should().Be("ak-knight-v6");
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
