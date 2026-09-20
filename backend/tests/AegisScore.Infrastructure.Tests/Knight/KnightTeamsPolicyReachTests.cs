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
/// [AEGIS-KNIGHT-COVERAGE-02] O ALCANCE de um critério que vive numa política do Teams — a parte que o fluxo
/// feliz não exercita. Existir uma política adequada não é a mesma coisa que o critério valer no ambiente, e
/// esta bateria trava essa diferença em cada uma das suas formas.
/// </summary>
public sealed class KnightTeamsPolicyReachTests
{
    private static KnightCapabilityStatus Cap(KnightCapability c, KnightCapabilityOutcome o = KnightCapabilityOutcome.Collected, string? d = null) => new(c, o, d);

    private static KnightEvaluationContext Ctx(IEnumerable<KnightConfigurationDocument> docs, params KnightCapabilityStatus[] caps)
    {
        var list = caps.ToList();
        return new KnightEvaluationContext(KnightFactSet.Empty, list, null, new KnightTenantConfiguration(docs, list),
            Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);
    }

    private static KnightControlOutcome Eval(string id, KnightEvaluationContext c) =>
        TeamsConfigurationControls.Definitions.Single(d => d.Id == id).Evaluate!(c);

    /// <summary>Política de reunião com a admissão de lobby informada (o critério de AK-TEAMS-010).</summary>
    private static KnightConfigurationDocument Meeting(string identity, string? autoAdmitted) =>
        KnightTenantConfiguration.Document(
            TeamsMeetingPolicyConfiguration.PolicyType + ":" + identity, null,
            new TeamsMeetingPolicyConfiguration(identity, false, false, autoAdmitted, false,
                "EnabledExceptAnonymous", "OrganizerOnlyUserOverride", false, false, false));

    private static KnightConfigurationDocument Assignment(string policyName, string groupId, int rank = 1) =>
        KnightTenantConfiguration.Document($"TeamsMeetingPolicy:{policyName}:{groupId}", null,
            new TeamsPolicyAssignment(TeamsMeetingPolicyConfiguration.PolicyType, policyName, groupId, rank));

    private static readonly KnightCapabilityStatus Policies = Cap(KnightCapability.TeamsMeetingPolicies);
    private static readonly KnightCapabilityStatus Assignments = Cap(KnightCapability.TeamsPolicyAssignments);

    [Fact]
    public void SemContextoDeColeta_NenhumControleDoTeamsAprova()
    {
        var evaluated = KnightIndicatorEvaluator.Evaluate(KnightFactSet.Empty, KnightSourceType.MicrosoftTeams);
        evaluated.Should().HaveCount(TeamsConfigurationControls.Definitions.Count)
            .And.OnlyContain(e => e.Status == KnightIndicatorStatus.NotEvaluated && e.NotEvaluatedReason != null);
    }

    [Fact]
    public void SemAPoliticaPadraoDaOrganizacao_NaoAvalia()
    {
        var o = Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Tag:Convidados", "EveryoneInCompanyExcludingGuests") }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("não devolveu a política padrão da organização");
    }

    [Fact]
    public void ColetaConcluidaSemNenhumaPolitica_NaoViraAmbienteSemProblema()
    {
        var o = Eval("AK-TEAMS-010", Ctx(Array.Empty<KnightConfigurationDocument>(), Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("nem a padrão da organização");
    }

    [Fact]
    public void PoliticaPadraoIlegivelParaOCriterio_NaoAvalia_EPreservaAsDemaisComoEvidencia()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[] { Meeting("Global", null), Meeting("Tag:Executivos", "OrganizerOnly") }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("não informou");
        o.EvidenceObjects.Should().HaveCount(2, "o que foi lido continua visível como evidência");
    }

    [Fact]
    public void PadraoAtende_MasUmaPersonalizadaEhIlegivel_NaoAprovaPorSuposicao()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[] { Meeting("Global", "EveryoneInCompanyExcludingGuests"), Meeting("Tag:Convidados", null) }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("política “Convidados”").And.Contain("exigiria supor");
    }

    [Fact]
    public void ValorForaDoContrato_NaoViraReprovacao_EhDeclaradoNaoReconhecido()
    {
        var o = Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Global", "ModoNovoQueNaoConhecemos") }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("valor não reconhecido");
    }

    [Fact]
    public void ValorDocumentadoPorEmMaisRestritivo_Aprova()
    {
        foreach (var aceito in new[] { "EveryoneInCompanyExcludingGuests", "OrganizerOnly", "InvitedUsers" })
            Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Global", aceito) }, Policies, Assignments))
                .Status.Should().Be(KnightIndicatorStatus.Passed, aceito);

        foreach (var recusado in new[] { "EveryoneInCompany", "EveryoneInSameAndFederatedCompany", "Everyone" })
            Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Global", recusado) }, Policies, Assignments))
                .Status.Should().Be(KnightIndicatorStatus.Exposed, recusado);
    }

    [Fact]
    public void AprovacaoDeclaraOAlcance_EAAusenciaDeEnumeracaoPorUsuario()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[] { Meeting("Global", "EveryoneInCompanyExcludingGuests"), Meeting("Tag:Executivos", "OrganizerOnly") }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.Passed);
        o.Evidence.Should().Contain("Verificado em 2 políticas").And.Contain("atribuições DIRETAS de política por conta de usuário não são enumeradas");
        o.Affected.Should().BeEmpty("num controle aprovado a contagem de afetados é zero por definição");
    }

    [Fact]
    public void PersonalizadaInadequadaComGrupo_DizOGrupoEAPrecedencia_SemInventarUsuarios()
    {
        var o = Eval("AK-TEAMS-010", Ctx(new[]
        {
            Meeting("Global", "EveryoneInCompanyExcludingGuests"),
            Meeting("Tag:Convidados", "Everyone"),
            Assignment("Convidados", "grp-0001", rank: 2),
        }, Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Affected.Should().ContainSingle();
        o.Affected.Single().Detail.Should().Contain("atribuída a 1 grupo(s)").And.Contain("grp-0001")
            .And.Contain("precedência 2").And.Contain("não são enumerados");
        o.Affected.Single().Kind.Should().Be(KnightAffectedObjectKind.Policy);
        o.Evidence.Should().Contain("A política padrão da organização atende ao critério");
    }

    [Fact]
    public void PersonalizadaInadequadaSemAtribuicao_DizQueOAlcanceNaoFoiDemonstrado()
    {
        var o = Eval("AK-TEAMS-010", Ctx(new[]
        {
            Meeting("Global", "EveryoneInCompanyExcludingGuests"),
            Meeting("Tag:Orfa", "Everyone"),
        }, Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Affected.Single().Detail.Should().Contain("Alcance não demonstrado")
            .And.Contain("nenhuma atribuição a grupo foi encontrada");
    }

    [Fact]
    public void SemAsAtribuicoes_OControleAindaAvalia_MasOAlcanceFicaDeclaradoComoNaoDemonstrado()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[] { Meeting("Global", "EveryoneInCompanyExcludingGuests"), Meeting("Tag:Convidados", "Everyone") },
            Policies, Cap(KnightCapability.TeamsPolicyAssignments, KnightCapabilityOutcome.InsufficientPermission, "sem permissão")));

        o.Status.Should().Be(KnightIndicatorStatus.Exposed, "a política inadequada existe, e isso é um fato");
        o.Affected.Single().Detail.Should().Contain("Alcance não demonstrado");
        o.Limitation.Should().Contain("sem permissão");
    }

    [Fact]
    public void AtribuicaoDeOutroTipoDePolitica_NaoEhUsadaComoAlcance()
    {
        var outra = KnightTenantConfiguration.Document("TeamsMessagingPolicy:Convidados:grp-9999", null,
            new TeamsPolicyAssignment(TeamsMessagingPolicyConfiguration.PolicyType, "Convidados", "grp-9999", 1));

        var o = Eval("AK-TEAMS-010", Ctx(new[]
        {
            Meeting("Global", "EveryoneInCompanyExcludingGuests"),
            Meeting("Tag:Convidados", "Everyone"),
            outra,
        }, Policies, Assignments));

        o.Affected.Single().Detail.Should().NotContain("grp-9999")
            .And.Contain("nenhuma atribuição a grupo foi encontrada");
    }

    [Fact]
    public void CapacidadeQueFalhou_DeclaraOMotivoDaColeta_NuncaAprovacao()
    {
        var o = Eval("AK-TEAMS-010", Ctx(Array.Empty<KnightConfigurationDocument>(),
            Cap(KnightCapability.TeamsMeetingPolicies, KnightCapabilityOutcome.Throttled, "Limite de taxa. HTTP 429"),
            Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("HTTP 429");
    }

    [Fact]
    public void ColetaAnteriorAAmpliacao_NaoAvalia_EDizPorque()
    {
        var o = Eval("AK-TEAMS-010", Ctx(Array.Empty<KnightConfigurationDocument>()));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("não foi coletada nesta aquisição");
    }

    [Fact]
    public void DocumentoDeVersaoDesconhecida_NaoEhCompletadoPorSuposicao()
    {
        var doc = Meeting("Global", "EveryoneInCompanyExcludingGuests") with { SchemaVersion = "aegis-config-teams-meeting-policy-v99" };
        var o = Eval("AK-TEAMS-010", Ctx(new[] { doc }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("versão desconhecida");
    }

    [Fact]
    public void RestricaoDeEntrada_NaoSeAplicaQuandoAComunicacaoEstaDesligada()
    {
        var fed = KnightTenantConfiguration.Document(TeamsFederationConfiguration.ExternalId, null,
            new TeamsFederationConfiguration(true, "AllowList", new[] { "parceiro.example.com" }, Array.Empty<string>(),
                false, false, true, "Blocked", Array.Empty<string>(), true));

        var o = Eval("AK-TEAMS-005", Ctx(new[] { fed }, Cap(KnightCapability.TeamsFederationConfiguration)));
        o.Status.Should().Be(KnightIndicatorStatus.NotApplicable,
            "sem comunicação com contas não gerenciadas não existe conversa de entrada a restringir");
        o.NotEvaluatedReason.Should().Contain("AK-TEAMS-004");
    }

    [Fact]
    public void ArmazenamentoDeTerceiros_EstadoDesconhecido_NaoViraAprovacao()
    {
        var cliente = KnightTenantConfiguration.Document(TeamsClientConfiguration.ExternalId, null,
            new TeamsClientConfiguration(false, null, false, false, false, false, false));

        var o = Eval("AK-TEAMS-001", Ctx(new[] { cliente }, Cap(KnightCapability.TeamsClientConfiguration)));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("Dropbox").And.Contain("exigiria supor");
    }

    [Fact]
    public void ArmazenamentoDeTerceiros_UmHabilitadoEOutroDesconhecido_ReprovaComALimitacaoDeclarada()
    {
        var cliente = KnightTenantConfiguration.Document(TeamsClientConfiguration.ExternalId, null,
            new TeamsClientConfiguration(false, true, null, false, false, false, false));

        var o = Eval("AK-TEAMS-001", Ctx(new[] { cliente }, Cap(KnightCapability.TeamsClientConfiguration)));
        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Evidence.Should().Contain("Dropbox").And.Contain("1 outro(s) provedor(es) não foi informado");
        o.AffectedComplete.Should().BeFalse();
        o.Limitation.Should().Contain("Box");
    }

    [Fact]
    public void ListaFechadaDeDominios_Aprova_ESemListaAceitaQualquerOrganizacao_Reprova()
    {
        KnightConfigurationDocument Fed(string? kind, params string[] dominios) =>
            KnightTenantConfiguration.Document(TeamsFederationConfiguration.ExternalId, null,
                new TeamsFederationConfiguration(true, kind, dominios, Array.Empty<string>(), false, false, false,
                    "Blocked", Array.Empty<string>(), true));

        var cap = Cap(KnightCapability.TeamsFederationConfiguration);
        Eval("AK-TEAMS-003", Ctx(new[] { Fed("AllowList", "parceiro.example.com") }, cap)).Status.Should().Be(KnightIndicatorStatus.Passed);
        Eval("AK-TEAMS-003", Ctx(new[] { Fed("AllowAllKnownDomains") }, cap)).Status.Should().Be(KnightIndicatorStatus.Exposed);
        Eval("AK-TEAMS-003", Ctx(new[] { Fed(null) }, cap)).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        Eval("AK-TEAMS-003", Ctx(new[] { Fed("FormaNova") }, cap)).NotEvaluatedReason.Should().Contain("não reconhece");
    }

    /// <summary>
    /// Com a comunicação com contas não gerenciadas LIGADA, a restrição de entrada volta a ser um critério real —
    /// e é avaliada, não presumida.
    /// </summary>
    [Fact]
    public void RestricaoDeEntrada_ComComunicacaoLigada_EhAvaliadaNosDoisSentidos()
    {
        KnightConfigurationDocument Fed(bool inbound) =>
            KnightTenantConfiguration.Document(TeamsFederationConfiguration.ExternalId, null,
                new TeamsFederationConfiguration(true, "AllowList", new[] { "parceiro.example.com" }, Array.Empty<string>(),
                    false, true, inbound, "Blocked", Array.Empty<string>(), true));

        var cap = Cap(KnightCapability.TeamsFederationConfiguration);
        Eval("AK-TEAMS-005", Ctx(new[] { Fed(false) }, cap)).Status.Should().Be(KnightIndicatorStatus.Passed);
        Eval("AK-TEAMS-005", Ctx(new[] { Fed(true) }, cap)).Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public void AcessoExternoDesligado_Aprova_SemDependerDaListaDeDominios()
    {
        var fed = KnightTenantConfiguration.Document(TeamsFederationConfiguration.ExternalId, null,
            new TeamsFederationConfiguration(false, null, Array.Empty<string>(), Array.Empty<string>(), false, false,
                false, "Blocked", Array.Empty<string>(), true));

        var o = Eval("AK-TEAMS-003", Ctx(new[] { fed }, Cap(KnightCapability.TeamsFederationConfiguration)));
        o.Status.Should().Be(KnightIndicatorStatus.Passed);
        o.Evidence.Should().Contain("acesso externo está desligado");
    }
}
