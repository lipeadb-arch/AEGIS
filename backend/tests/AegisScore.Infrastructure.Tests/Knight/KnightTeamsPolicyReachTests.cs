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

    /// <summary>
    /// Aprovam SOMENTE os dois valores que a documentação oficial demonstra manterem todo participante de fora no
    /// lobby: “pessoas da minha organização” e “somente organizadores e coorganizadores” (este, estritamente mais
    /// restritivo). Os demais valores documentados reprovam — inclusive <c>InvitedUsers</c>.
    /// </summary>
    [Fact]
    public void ValorDocumentadoPorEmMaisRestritivo_Aprova()
    {
        foreach (var aceito in new[] { "EveryoneInCompanyExcludingGuests", "OrganizerOnly" })
            Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Global", aceito) }, Policies, Assignments))
                .Status.Should().Be(KnightIndicatorStatus.Passed, aceito);

        foreach (var recusado in new[]
                 { "InvitedUsers", "EveryoneInCompany", "EveryoneInSameAndFederatedCompany", "Everyone" })
            Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Global", recusado) }, Policies, Assignments))
                .Status.Should().Be(KnightIndicatorStatus.Exposed, recusado);
    }

    /// <summary>
    /// [Revisão dirigida] DEFEITO REPRODUZIDO: “pessoas que foram convidadas” (<c>InvitedUsers</c>) era aceito
    /// como equivalente a restringir a admissão automática às pessoas da organização, e o achado publicava a
    /// frase “somente pessoas da organização entram na reunião sem passar pelo lobby” — incompatível com o valor.
    ///
    /// A tabela oficial de opções do lobby mostra que, com esse valor, também ignoram o lobby os CONVIDADOS e os
    /// participantes de ORGANIZAÇÕES CONFIÁVEIS que receberam o convite, ou a quem ele foi encaminhado. A própria
    /// página diz que a opção inclui todos os participantes com conta corporativa ou de estudante e os convidados
    /// a quem o convite foi encaminhado — não apenas quem o organizador convidou diretamente.
    /// https://learn.microsoft.com/en-us/microsoftteams/who-can-bypass-meeting-lobby
    /// </summary>
    [Fact]
    public void ConvidadosNaoSaoExcluidosPorInvitedUsers_ReprovaEDescreveOQueOValorPermite()
    {
        var o = Eval("AK-TEAMS-010", Ctx(new[] { Meeting("Global", "InvitedUsers") }, Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Evidence.Should().NotContain("Somente pessoas da organização entram na reunião sem passar pelo lobby");

        var afetado = o.Affected.Should().ContainSingle().Subject;
        afetado.Detail.Should().Contain("CONVIDADOS")
            .And.Contain("organizações externas")
            .And.Contain("encaminhado");
    }

    /// <summary>Os dois valores preservados continuam descrevendo com precisão quem entra direto.</summary>
    [Fact]
    public void ValoresAceitos_DescrevemQuemEntraSemPassarPeloLobby()
    {
        var org = Eval("AK-TEAMS-010",
            Ctx(new[] { Meeting("Global", "EveryoneInCompanyExcludingGuests") }, Policies, Assignments));
        org.Status.Should().Be(KnightIndicatorStatus.Passed);
        org.EvidenceObjects.Should().ContainSingle()
            .Which.Detail.Should().Contain("somente pessoas da organização");

        var organizador = Eval("AK-TEAMS-010",
            Ctx(new[] { Meeting("Global", "OrganizerOnly") }, Policies, Assignments));
        organizador.Status.Should().Be(KnightIndicatorStatus.Passed);
        organizador.EvidenceObjects.Should().ContainSingle()
            .Which.Detail.Should().Contain("somente organizadores e coorganizadores");
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

    // ======================================================================================================
    //  [Revisão dirigida] Identificação AUSENTE não vira política padrão da organização
    // ======================================================================================================

    private static KnightConfigurationDocument MeetingAt(string docId, string? identity, string? autoAdmitted) =>
        KnightTenantConfiguration.Document(docId, null,
            new TeamsMeetingPolicyConfiguration(identity, false, false, autoAdmitted, false,
                "EnabledExceptAnonymous", "OrganizerOnlyUserOverride", false, false, false));

    /// <summary>
    /// DEFEITO REPRODUZIDO: valores CONFORMES sem <c>identity</c> e sem nenhuma Global explicitamente
    /// identificada APROVAVAM o ambiente — porque nulo, vazio e só-espaços eram lidos como "Global", inventando
    /// a instância que sempre se aplica. Agora nada disso é a política padrão: o controle não avalia, e diz por quê.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdentificacaoAusente_NaoViraPoliticaPadrao_ENaoAprovaOAmbiente(string? identity)
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[] { MeetingAt("TeamsMeetingPolicy:#0", identity, "EveryoneInCompanyExcludingGuests") },
            Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("não devolveu a política padrão da organização")
            .And.Contain("SEM identificação");
    }

    /// <summary>
    /// Registros distintos sem identificação continuam DISTINTOS: não se fundem num objeto só. A posição na
    /// coleta entra no identificador justamente para isso.
    /// </summary>
    [Fact]
    public void RegistrosSemIdentificacao_NaoSeFundemNumSoObjeto()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[]
            {
                MeetingAt("TeamsMeetingPolicy:#0", null, "EveryoneInCompanyExcludingGuests"),
                MeetingAt("TeamsMeetingPolicy:#1", "", "OrganizerOnly"),
                MeetingAt("TeamsMeetingPolicy:#2", "   ", "EveryoneInCompanyExcludingGuests"),
            },
            Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.EvidenceObjects.Should().HaveCount(3);
        o.EvidenceObjects.Select(e => e.ExternalId).Distinct().Should().HaveCount(3);
        o.EvidenceObjects.Should().OnlyContain(e => e.Detail!.Contains("Alcance não demonstrado"));
    }

    /// <summary>
    /// Com a política padrão presente e conforme, um registro NÃO IDENTIFICADO e conforme ainda assim impede a
    /// aprovação: não se sabe a quem ele se aplica. A limitação é dita, e não inventada.
    /// </summary>
    [Fact]
    public void PadraoConformeMaisRegistroSemIdentificacao_NaoAprova_EDeclaraALimitacao()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[]
            {
                MeetingAt("TeamsMeetingPolicy:Global", "Global", "EveryoneInCompanyExcludingGuests"),
                MeetingAt("TeamsMeetingPolicy:#1", null, "EveryoneInCompanyExcludingGuests"),
            },
            Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.NotEvaluatedReason.Should().Contain("SEM identificação").And.Contain("exigiria supor");
    }

    /// <summary>
    /// Um registro não identificado que VIOLA o critério continua sendo achado: o defeito de configuração é um
    /// fato demonstrado, e não desaparece por causa da identidade. O que não se afirma é o alcance dele.
    /// </summary>
    [Fact]
    public void RegistroSemIdentificacaoQueViola_ContinuaSendoAchado_SemAlcanceInventado()
    {
        var o = Eval("AK-TEAMS-010", Ctx(
            new[]
            {
                MeetingAt("TeamsMeetingPolicy:Global", "Global", "EveryoneInCompanyExcludingGuests"),
                MeetingAt("TeamsMeetingPolicy:#1", null, "Everyone"),
            },
            Policies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        var afetado = o.Affected.Should().ContainSingle().Subject;
        afetado.DisplayName.Should().Contain("sem identificação");
        afetado.Detail.Should().Contain("Alcance não demonstrado");
    }

    // ======================================================================================================
    //  [Revisão dirigida] AK-TEAMS-007 NÃO CONCLUI: a leitura que diria qual modelo governa não é acessível
    // ======================================================================================================

    private static KnightConfigurationDocument AppPolicy(string identity, string globalType) =>
        KnightTenantConfiguration.Document(TeamsAppPermissionPolicyConfiguration.PolicyType + ":" + identity, null,
            new TeamsAppPermissionPolicyConfiguration(identity, "AllowedAppList", 12, globalType, 4, "AllowedAppList", 2));

    private static readonly KnightCapabilityStatus AppPolicies = Cap(KnightCapability.TeamsAppPermissionPolicies);

    /// <summary>
    /// DEFEITO REPRODUZIDO (1ª revisão): uma política de permissão “com lista de permitidos” APROVAVA o critério,
    /// embora a documentação oficial diga que, em locatário migrado para ACM/UAM, essas políticas não podem mais
    /// ser acessadas, editadas nem usadas — ou seja, o que foi lido pode não governar nada.
    ///
    /// DEFEITO REPRODUZIDO (2ª revisão): a condição de aplicabilidade foi construída sobre Get-AllM365TeamsApps,
    /// comando que a documentação oficial lista entre os NÃO SUPORTADOS com autenticação de aplicativo — a única
    /// que este conector usa. A leitura nunca funcionaria no fluxo real; só nos dublês do teste.
    /// https://learn.microsoft.com/en-us/microsoftteams/teams-powershell-application-authentication
    ///
    /// O desfecho correto, hoje, é um só: NÃO AVALIADO — para QUALQUER valor da configuração legada, inclusive o
    /// que antes aprovava e o que antes reprovava. A configuração é preservada como evidência.
    /// </summary>
    [Theory]
    [InlineData("AllowedAppList")]   // antes: aprovava
    [InlineData("BlockedAppList")]   // antes: reprovava
    public void AcessoAAplicativos_NaoConclui_QualquerQueSejaAConfiguracaoLegada(string globalType)
    {
        var o = Eval("AK-TEAMS-007", Ctx(new[] { AppPolicy("Global", globalType) }, AppPolicies, Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        o.Status.Should().NotBe(KnightIndicatorStatus.Passed);
        o.Status.Should().NotBe(KnightIndicatorStatus.Exposed);

        // O motivo nomeia a causa REAL — incompatibilidade de autenticação — e não uma falta de permissão.
        o.NotEvaluatedReason.Should().Contain("não é possível determinar qual modelo governa")
            .And.Contain("NÃO SUPORTADOS com autenticação de aplicativo");
        o.NotEvaluatedReason.Should().NotContain("Leitor do Teams");
        o.NotEvaluatedReason.Should().NotContain("permissão insuficiente");

        // E a configuração lida não é descartada: ela vira evidência, com a ressalva de que não sustenta veredito.
        o.EvidenceObjects.Should().Contain(e => e.Detail!.Contains("preservada como evidência"));
    }

    /// <summary>
    /// Ausência de políticas legadas também não conclui: não é migração comprovada nem conformidade.
    /// </summary>
    [Fact]
    public void AcessoAAplicativos_SemPoliticaAlguma_TambemNaoConclui()
    {
        var o = Eval("AK-TEAMS-007", Ctx(Array.Empty<KnightConfigurationDocument>(), AppPolicies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ======================================================================================================
    //  [Revisão dirigida] Textos de risco alinhados à evidência
    // ======================================================================================================

    /// <summary>
    /// AK-TEAMS-013 não pode dizer “todos os participantes” quando o valor encontrado promove um conjunto menor.
    /// </summary>
    [Fact]
    public void ApresentadorPorPadrao_NomeiaOConjuntoEncontrado_ENaoTodosOsParticipantes()
    {
        KnightConfigurationDocument Presenter(string mode) =>
            KnightTenantConfiguration.Document("TeamsMeetingPolicy:Global", null,
                new TeamsMeetingPolicyConfiguration("Global", false, false, "EveryoneInCompanyExcludingGuests", false,
                    "EnabledExceptAnonymous", mode, false, false, false));

        var daOrganizacao = Eval("AK-TEAMS-013",
            Ctx(new[] { Presenter("EveryoneInCompanyUserOverride") }, Policies, Assignments));
        daOrganizacao.Status.Should().Be(KnightIndicatorStatus.Exposed);
        daOrganizacao.Evidence.Should().NotContain("Todos os participantes");
        daOrganizacao.Affected.Should().ContainSingle()
            .Which.Detail.Should().Contain("todas as pessoas da organização presentes")
            .And.NotContain("inclusive os de fora");

        var todos = Eval("AK-TEAMS-013", Ctx(new[] { Presenter("EveryoneUserOverride") }, Policies, Assignments));
        todos.Affected.Should().ContainSingle()
            .Which.Detail.Should().Contain("todos os participantes, inclusive os de fora da organização");

        var confiaveis = Eval("AK-TEAMS-013",
            Ctx(new[] { Presenter("EveryoneInSameAndFederatedCompanyUserOverride") }, Policies, Assignments));
        confiaveis.Affected.Should().ContainSingle()
            .Which.Detail.Should().Contain("organizações confiáveis");
    }

    /// <summary>
    /// AK-TEAMS-009 não pode afirmar início de reunião por anônimos a partir do booleano sozinho: a documentação
    /// oficial condiciona o efeito ao lobby e à entrada de anônimos. O achado diz a condição.
    /// </summary>
    [Fact]
    public void InicioDeReuniaoSemParticipanteVerificado_DeclaraACondicaoDocumentada()
    {
        var doc = KnightTenantConfiguration.Document("TeamsMeetingPolicy:Global", null,
            new TeamsMeetingPolicyConfiguration("Global", false, true, "EveryoneInCompanyExcludingGuests", false,
                "EnabledExceptAnonymous", "OrganizerOnlyUserOverride", false, false, false));

        var o = Eval("AK-TEAMS-009", Ctx(new[] { doc }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Evidence.Should().Contain("depende de duas condições")
            .And.Contain("DISCAGEM TELEFÔNICA")
            .And.Contain("AK-TEAMS-010");
    }

    /// <summary>AK-TEAMS-015 não afirma ausência de todos os controles de segurança.</summary>
    [Fact]
    public void ChatExternoNaoConfiavel_NaoAfirmaAusenciaDeTodosOsControles()
    {
        var doc = KnightTenantConfiguration.Document("TeamsMeetingPolicy:Global", null,
            new TeamsMeetingPolicyConfiguration("Global", false, false, "EveryoneInCompanyExcludingGuests", false,
                "EnabledExceptAnonymous", "OrganizerOnlyUserOverride", false, true, false));

        var o = Eval("AK-TEAMS-015", Ctx(new[] { doc }, Policies, Assignments));
        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Evidence.Should().Contain("ADMINISTRADO POR TERCEIROS")
            .And.Contain("continuam valendo")
            .And.NotContain("não passam pelos controles da organização");
    }

    /// <summary>
    /// AK-TEAMS-017 não conclui que a equipe de segurança só descobrirá o problema depois de alguém agir sobre a
    /// mensagem: afirma a ausência DESTE caminho de relato.
    /// </summary>
    [Fact]
    public void RelatoDeMensagemSuspeita_NaoConcluiQuandoASegurancaDescobre()
    {
        var doc = KnightTenantConfiguration.Document("TeamsMessagingPolicy:Global", null,
            new TeamsMessagingPolicyConfiguration("Global", false));

        var o = Eval("AK-TEAMS-017", Ctx(new[] { doc },
            Cap(KnightCapability.TeamsMessagingPolicies), Assignments));

        o.Status.Should().Be(KnightIndicatorStatus.Exposed);
        o.Evidence.Should().Contain("outras fontes de detecção")
            .And.NotContain("só fica sabendo quando alguém já agiu");
    }
}
