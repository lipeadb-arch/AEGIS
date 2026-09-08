using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Remediation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Remediation;

/// <summary>
/// [AEGIS-MVP-PRODUCT-03] Testes da JORNADA de remediação, concentrados nos riscos DESTA entrega — sem
/// repetir o que as suítes do catálogo, do coletor e dos afetados já cobrem:
///
///   (1) duplicação por clique repetido e reabertura de ciclo quando o problema reaparece;
///   (2) transições do fluxo mínimo e atraso derivado do PRAZO (nunca uma etapa que apaga onde parou);
///   (3) as referências corretas: a avaliação de ORIGEM não é a de VALIDAÇÃO;
///   (4) evidência que NÃO comprova correção — ordem temporal, fonte, regras e suficiência POR INDICADOR;
///   (5) publicação da avaliação PEDIDA, sem troca silenciosa pela mais recente;
///   (6) isolamento por tenant e autorização das mutações;
///   (7) compatibilidade: plano legado de risco intocado e snapshot legado com hash preservado;
///   (8) serialização EFETIVA dos contratos novos com as opções REAIS do MVC (enums por NOME).
///
/// LIMITE declarado: o alvo é a ação do controller com dependências reais. O repositório não tem harness de
/// integração HTTP (<c>WebApplicationFactory</c>), então o pipeline HTTP e a autenticação JWT NÃO são
/// executados — a exigência de papel é provada por REFLEXÃO sobre os atributos, como no Dia 1.
/// </summary>
public sealed class RemediationJourneyTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3333-3333-3333-333333333333");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public RemediationJourneyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.Tenants.Add(new Tenant { Id = TenantB, Name = "Cliente Beta", Slug = "beta", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ================================================================================================
    // (1) Duplicação e ciclo novo
    // ================================================================================================

    [Fact]
    public async Task CliqueRepetido_NaoDuplicaAcaoAtiva_EInformaAExistente()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var criada = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        criada.Should().NotBeNull();

        var segundoClique = async () => await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        var ex = await segundoClique.Should().ThrowAsync<ActionPlanConflictException>(
            "um segundo clique abre a ação existente — não cria uma paralela para o mesmo achado");
        ex.Which.ExistingActionPlanId.Should().Be(criada!.Id, "a tela precisa saber QUAL ação abrir");

        (await svc.ListAsync(new ActionPlanFilter("AK-ENTRA-001"))).Should().HaveCount(1);
    }

    [Fact]
    public async Task AcaoEncerrada_LiberaAOrigem_ParaUmNovoCicloQuandoOProblemaReaparece()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var primeira = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        // O encerramento percorre a jornada INTEIRA: relato de execução e validação sobre esta execução. A
        // versão anterior deste teste subia a etapa por cliques — e era justamente o atalho que o pacote
        // existe para fechar.
        var concluida = await ConcluirComBaseAsync(svc, primeira!, "MFA registrado nas contas listadas.");
        concluida.IsActive.Should().BeFalse();

        var segundoCiclo = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        segundoCiclo.Should().NotBeNull("um problema que reaparece merece um ciclo novo, não a reabertura forçada");
        segundoCiclo!.Id.Should().NotBe(primeira!.Id);
        (await svc.ListAsync(new ActionPlanFilter("AK-ENTRA-001"))).Should().HaveCount(2);
    }

    // ================================================================================================
    // (2) Transições e atraso
    // ================================================================================================

    [Fact]
    public async Task ConcluirDiretoDeAberto_ERecusado_PorqueNadaFoiSequerRelatado()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);
        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        var pular = async () => await svc.UpdateAsync(p!.Id, Status(p.Version, ActionPlanStatus.Concluido), Actor);

        await pular.Should().ThrowAsync<ActionPlanValidationException>(
            "concluir sem execução nem validação é exatamente o atalho que o fluxo existe para impedir");
    }

    [Fact]
    public async Task RegistrarExecucao_LevaParaAguardandoValidacao_ENaoParaConcluido()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);
        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        var apos = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "MFA registrado nas duas contas.", "CHAMADO-4711"), Actor);

        apos!.Status.Should().Be(ActionPlanStatus.AguardandoValidacao, "relato não é comprovação");
        apos.ExecutionNotes.Should().Be("MFA registrado nas duas contas.");
        apos.ExecutionEvidenceRef.Should().Be("CHAMADO-4711");
        apos.LatestValidation.Should().BeNull("registrar execução NÃO cria validação");
        apos.Events.Should().Contain(e => e.Kind == ActionPlanEventKind.ExecutionRecorded);
    }

    [Fact]
    public async Task ExecucaoSemDescricao_ERecusada()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);
        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        var vazio = async () => await svc.RecordExecutionAsync(p!.Id, new RecordExecutionCommand(p.Version, "   ", null), Actor);

        await vazio.Should().ThrowAsync<ActionPlanValidationException>(
            "um registro de execução sem descrição não serve para validar depois");
    }

    [Fact]
    public async Task AtrasoVemDoPrazo_ENaoApagaAEtapaOperacional()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var vencido = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);
        var p = await svc.CreateForFindingAsync(
            Create(run.Id, "AK-ENTRA-001") with { DueDate = vencido }, Actor);
        var andamento = await svc.UpdateAsync(p!.Id, Status(p.Version, ActionPlanStatus.EmAndamento), Actor);

        andamento!.IsOverdue.Should().BeTrue();
        andamento.Status.Should().Be(ActionPlanStatus.EmAndamento,
            "saber que está atrasado sem saber onde o trabalho parou não destrava nada");
        andamento.Status.Should().NotBe(ActionPlanStatus.Vencido, "'Vencido' é etapa legada, nunca destino do fluxo novo");
        andamento.NextStep.Should().Contain("repactuar");
    }

    [Fact]
    public async Task VersaoDivergente_ERecusada_EmVezDeSobrescreverOTrabalhoDeOutraPessoa()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);
        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        // Alguém gravou antes: a versão do plano avançou e a tela ainda tem a antiga.
        await svc.UpdateAsync(p!.Id, Status(p.Version, ActionPlanStatus.EmAndamento), Actor);

        var telaVelha = async () => await svc.UpdateAsync(
            p.Id, new UpdateActionPlanCommand(p.Version, "Título novo", null, null, null, null, null), Actor);

        await telaVelha.Should().ThrowAsync<ActionPlanConflictException>(
            "gravar sobre versão vencida descartaria silenciosamente o trabalho da outra pessoa");
    }

    // ================================================================================================
    // (3) A referência de ORIGEM não é a de VALIDAÇÃO
    // ================================================================================================

    [Fact]
    public async Task Origem_EValidacao_SaoReferenciasSeparadas()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var nova = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(7));
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        p!.OriginRunId.Should().Be(origem.Id);
        p.OriginAffectedCount.Should().Be(2, "o ponto de partida da melhora é registrado na criação");

        var validado = await svc.ValidateAsync(p.Id, new ValidateActionPlanCommand(p.Version, nova.Id, null, null), Actor);

        validado!.OriginRunId.Should().Be(origem.Id, "a origem NÃO é reescrita pela validação");
        validado.LatestValidation!.ValidationRunId.Should().Be(nova.Id,
            "a prova aponta para a coleta POSTERIOR, não para a que revelou o problema");
    }

    // ================================================================================================
    // (4) Evidência que NÃO comprova correção
    // ================================================================================================

    [Fact]
    public async Task NovaColeta_ComQuedaParcial_EReducaoObservada_NuncaResolucao()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0);
        var nova = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(7));
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var v = (await svc.ValidateAsync(p!.Id, new ValidateActionPlanCommand(p.Version, nova.Id, null, null), Actor))!
            .LatestValidation!;

        v.Outcome.Should().Be(ActionPlanValidationOutcome.ReductionObserved,
            "o achado continua exposto — contagem menor é redução, não resolução");
        v.ObservedBefore.Should().Be(3);
        v.ObservedAfter.Should().Be(1);
        v.Rationale.Should().Contain("continua exposto");
    }

    [Fact]
    public async Task ColetaAnterior_NaoComprovaCorrecao_MesmoComNumeroMenor()
    {
        await using var db = NewContext(TenantA);
        // A "evidência" é ANTERIOR à origem: descreve o mundo antes do trabalho, e ainda por cima tem menos.
        var anterior = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(-7));
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var v = (await svc.ValidateAsync(p!.Id, new ValidateActionPlanCommand(p.Version, anterior.Id, null, null), Actor))!
            .LatestValidation!;

        v.Outcome.Should().Be(ActionPlanValidationOutcome.EvidenceInsufficient);
        v.Rationale.Should().Contain("anterior");
        v.ObservedAfter.Should().BeNull("não há observação válida a registrar quando a comparação é recusada");
    }

    [Fact]
    public async Task AMesmaAvaliacao_NaoComprovaASiMesma()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var v = (await svc.ValidateAsync(p!.Id, new ValidateActionPlanCommand(p.Version, origem.Id, null, null), Actor))!
            .LatestValidation!;

        v.Outcome.Should().Be(ActionPlanValidationOutcome.EvidenceInsufficient);
        v.Rationale.Should().Contain("MESMA");
    }

    [Fact]
    public async Task CapacidadeNECESSARIA_Ausente_NaoComprovaMelhora()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0,
            capabilities: Collected(KnightCapability.PrivilegedRoleInventory, KnightCapability.MfaRegistration));
        // A nova coleta perdeu justamente o relatório de MFA — o número "melhor" não prova nada.
        var nova = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(7),
            capabilities: new[]
            {
                new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.MfaRegistration, KnightCapabilityOutcome.InsufficientPermission),
            });
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var v = (await svc.ValidateAsync(p!.Id, new ValidateActionPlanCommand(p.Version, nova.Id, null, null), Actor))!
            .LatestValidation!;

        v.Outcome.Should().Be(ActionPlanValidationOutcome.EvidenceInsufficient,
            "ausência da capacidade que ESTE achado consome não pode virar prova de melhora");
        v.Rationale.Should().Contain("MfaRegistration");
    }

    [Fact]
    public async Task ParcialidadeGlobal_DeCapacidadeQueOAchadoNaoConsome_NaoInvalidaAEvidencia()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0,
            capabilities: Collected(KnightCapability.PrivilegedRoleInventory, KnightCapability.MfaRegistration));
        // Coleta GLOBALMENTE parcial: as permissões opcionais de risco de identidade não foram concedidas.
        // Papéis e registro de MFA vieram completos — e é só disso que AK-ENTRA-001 depende.
        var nova = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(7),
            state: KnightSourceState.PartialCollection,
            capabilities: new[]
            {
                new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.IdentityRiskyUsers, KnightCapabilityOutcome.InsufficientPermission),
                new KnightCapabilityStatus(KnightCapability.IdentityRiskDetections, KnightCapabilityOutcome.LimitedByLicense),
            });
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var v = (await svc.ValidateAsync(p!.Id, new ValidateActionPlanCommand(p.Version, nova.Id, null, null), Actor))!
            .LatestValidation!;

        v.Outcome.Should().Be(ActionPlanValidationOutcome.ReductionObserved,
            "descartar uma coleta completa de MFA por causa de Identity Risk seria jogar fora evidência válida");
    }

    [Fact]
    public async Task ExposicaoEncerrada_ComConjuntosPreservados_DizQuantosSairam()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var nova = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 0, at: T0.AddDays(7));
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var v = (await svc.ValidateAsync(p!.Id, new ValidateActionPlanCommand(p.Version, nova.Id, null, null), Actor))!
            .LatestValidation!;

        v.Outcome.Should().Be(ActionPlanValidationOutcome.ExposureCleared);
        v.ComparedBySets.Should().BeTrue("os dois lados preservaram a lista inteira");
        v.ObjectsNoLongerPresent.Should().Be(2, "aí sim é possível dizer QUANTOS objetos saíram do conjunto");
    }

    [Fact]
    public async Task ValidacaoHumana_ExigeEvidencia_EJamaisViraComprovacaoTecnica()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);
        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);

        var semReferencia = async () => await svc.ValidateAsync(
            p!.Id, new ValidateActionPlanCommand(p.Version, null, "   ", "feito"), Actor);
        await semReferencia.Should().ThrowAsync<ActionPlanValidationException>(
            "um 'feito' sem referência é comentário, não comprovação");

        var comReferencia = await svc.ValidateAsync(
            p!.Id, new ValidateActionPlanCommand(p.Version, null, "ATA-2026-09", "Confirmado com a área."), Actor);
        var v = comReferencia!.LatestValidation!;

        v.Method.Should().Be(ActionPlanValidationMethod.HumanEvidence);
        v.Outcome.Should().Be(ActionPlanValidationOutcome.HumanAttested);
        v.ValidationRunId.Should().BeNull("não houve coleta a apontar");
        RemediationReading.IsTechnicallyProven(v.Method, v.Outcome).Should().BeFalse(
            "aceite de risco e encerramento administrativo NUNCA entram na melhora comprovada");
        v.Rationale.Should().Contain("não verificou o ambiente");
    }

    [Fact]
    public async Task ProximaProvidencia_SegueODESFECHO_ENaoAMeraExistenciaDeUmaValidacao()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var nova = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 0, at: T0.AddDays(7));
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "MFA registrado.", null), Actor);
        executado!.NextStep.Should().Contain("validar com uma nova avaliação",
            "sem validação, a providência é obter a comprovação");

        var validado = await svc.ValidateAsync(
            executado.Id, new ValidateActionPlanCommand(executado.Version, nova.Id, null, null), Actor);

        validado!.LatestValidation!.Outcome.Should().Be(ActionPlanValidationOutcome.ExposureCleared);
        validado.NextStep.Should().Contain("Encerrar a ação",
            "a providência de quem JÁ comprovou a melhora é encerrar");
        validado.NextStep.Should().NotContain("não comprovou",
            "derivar a frase da mera EXISTÊNCIA de uma validação faria a tela negar, logo abaixo, a " +
            "comprovação que ela mesma acabou de exibir");
    }

    [Fact]
    public async Task ProximaProvidencia_DeEvidenciaInsuficiente_MandaObterEvidenciaAdequada()
    {
        await using var db = NewContext(TenantA);
        var anterior = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 0, at: T0.AddDays(-7));
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "MFA registrado.", null), Actor);
        var validado = await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, anterior.Id, null, null), Actor);

        validado!.LatestValidation!.Outcome.Should().Be(ActionPlanValidationOutcome.EvidenceInsufficient);
        validado.NextStep.Should().Contain("evidência adequada");
        validado.NextStep.Should().NotContain("Encerrar a ação", "não há o que encerrar sem comprovação");
    }

    // ================================================================================================
    // (5) Publicação: a avaliação PEDIDA, sem troca silenciosa
    // ================================================================================================

    [Fact]
    public async Task PublicarComRunId_CongelaAquelaAvaliacao_NuncaAMaisRecente()
    {
        await using var db = NewContext(TenantA);
        var antiga = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0);
        var recente = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(7));

        var detail = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, antiga.Id);

        detail.Summary.SourceRunId.Should().Be(antiga.Id, "publicar por link é publicar AQUELA coleta");
        detail.Summary.SourceRunId.Should().NotBe(recente.Id);
        detail.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-001").AffectedObjectCount.Should().Be(3,
            "os números congelados são os da avaliação pedida");
        detail.Summary.ClientName.Should().Be("Cliente Alfa", "o cliente é congelado, não buscado na exportação");
    }

    [Fact]
    public async Task PublicarSemRunId_PreservaOComportamentoExistente_AMaisRecente()
    {
        await using var db = NewContext(TenantA);
        await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0);
        var recente = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 1, at: T0.AddDays(7));

        var detail = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null);

        detail.Summary.SourceRunId.Should().Be(recente.Id, "quem não indica avaliação continua recebendo a atual");
    }

    [Fact]
    public async Task PublicarAvaliacaoInexistente_ERecusada_EmVezDeCairParaAMaisRecente()
    {
        await using var db = NewContext(TenantA);
        await RunAsync(db, TenantA, privileged: 12, withoutMfa: 3, at: T0);

        var publicar = async () => await PostureFor(db, TenantA)
            .PublishAsync(PostureSnapshotType.Knight, null, Guid.NewGuid());

        (await publicar.Should().ThrowAsync<PostureSnapshotNotAvailableException>())
            .Which.Message.Should().Contain("não se substituem");
    }

    [Fact]
    public async Task AvaliacaoDeOutroTenant_NaoPodeSerPublicada_NemCaiParaAMaisRecenteDoProprio()
    {
        await using var dbB = NewContext(TenantB);
        var doB = await RunAsync(dbB, TenantB, privileged: 12, withoutMfa: 2, at: T0);

        await using var dbA = NewContext(TenantA);
        await RunAsync(dbA, TenantA, privileged: 12, withoutMfa: 5, at: T0.AddDays(1));

        var publicar = async () => await PostureFor(dbA, TenantA)
            .PublishAsync(PostureSnapshotType.Knight, null, doB.Id);

        await publicar.Should().ThrowAsync<PostureSnapshotNotAvailableException>(
            "a avaliação de outro tenant é indistinguível de inexistente — e não vira publicação da própria");
    }

    [Fact]
    public async Task AcoesSaoCONGELADAS_NaPublicacao_ERelatorioHistoricoNaoAbsorveMudancasPosteriores()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        var publicado = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, run.Id);

        var congelada = publicado.ActionItems!.Single(a => a.ActionPlanId == p!.Id);
        congelada.Status.Should().Be(nameof(ActionPlanStatus.Aberto));
        congelada.ValidationOutcome.Should().BeNull();

        // Depois da publicação, o plano avança. O relatório JÁ PUBLICADO não pode mudar por causa disso.
        var andamento = await svc.UpdateAsync(p!.Id, Status(p.Version, ActionPlanStatus.EmAndamento), Actor);
        await svc.RecordExecutionAsync(andamento!.Id, new RecordExecutionCommand(andamento.Version, "Feito.", null), Actor);

        var relido = await PostureFor(db, TenantA).GetAsync(publicado.Summary.Id);

        relido!.ActionItems!.Single().Status.Should().Be(nameof(ActionPlanStatus.Aberto),
            "injetar o estado ATUAL num relatório histórico seria mudar um documento já emitido");
    }

    [Fact]
    public async Task RelatorioExecutivo_NaoApresentaAtestacaoHumanaComoComparacao_ENaoImprimeOScoreKnightComoPorcentagem()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "Feito.", null), Actor);
        await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, null, "ATA-2026-09", null), Actor);

        var publicado = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, run.Id);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Indicators).Include(s => s.ActionItems)
            .FirstAsync(s => s.Id == publicado.Summary.Id);

        var congelada = snapshot.ActionItems.Single();
        congelada.ValidationMethod.Should().Be(ActionPlanValidationMethod.HumanEvidence);

        // As duas invariantes são de TEXTO, e é nas funções que o compõem que elas se testam. A extração de um
        // PDF INTERCALA as colunas de uma tabela, e o recorte muda com a fonte instalada — asserção sobre o
        // dump de glifos passa no Windows e falha no Linux do CI sem que nada de real tenha mudado.
        PostureSnapshotPdfWriter.ValidationBasisText(congelada.ValidationMethod, congelada.ComparedBySets)
            .Should().Be("Registro humano com evidência referenciada. O AEGIS não verificou o ambiente para este registro.",
                "a BASE da conclusão segue o MÉTODO: uma atestação humana nunca foi uma comparação");
        PostureSnapshotPdfWriter.ValidationBasisText(congelada.ValidationMethod, congelada.ComparedBySets)
            .Should().NotContain("QUANTIDADE",
                "descrever uma atestação como comparação de quantidade afirmaria uma comparação que não existiu");

        PostureSnapshotPdfWriter.ScoreText(snapshot.Score, PostureSnapshotType.Knight)
            .Should().EndWith(" / 100",
                "o score do KNIGHT é nota em escala PRÓPRIA — imprimi-lo com '%' convidaria a lê-lo como o AEGIS Score/NIST");
        PostureSnapshotPdfWriter.ScoreText(66.7, PostureSnapshotType.AegisScoreNist)
            .Should().EndWith("%", "o AEGIS Score/NIST continua sendo percentual — os instrumentos não se misturam");
        PostureSnapshotPdfWriter.ScoreText(null, PostureSnapshotType.Knight)
            .Should().Be("Não avaliado", "score ausente nunca vira 0");

        // O PDF em si é renderizado e verificado no que é ESTÁVEL: texto de PARÁGRAFO (que não intercala
        // colunas) e a ausência de qualquer identidade nominal.
        var texto = Deaccent(ExtractPdfText(PostureSnapshotPdfWriter.Write(snapshot)));
        texto.Should().Contain("Este relatorio nao apresenta tendencia entre avaliacoes",
            "o relatório declara que não infere tendência — parágrafo, não célula de tabela");
        texto.Should().NotContain("demo.example.com",
            "o PDF executivo circula por e-mail; a lista nominal fica no produto, sob autorização");
    }

    /// <summary>Extração textual do PDF — a MESMA abordagem (PdfPig) da suíte de exportação já existente.</summary>
    private static string ExtractPdfText(byte[] bytes)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(x => string.Join(" ", x.GetWords().Select(w => w.Text))));
    }

    /// <summary>Remove acentos: a extração quebra a acentuação, e a asserção é sobre o CONTEÚDO.</summary>
    private static string Deaccent(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    // ================================================================================================
    // (6) Isolamento e autorização
    // ================================================================================================

    [Fact]
    public async Task AcaoDeOutroTenant_EIndistinguivelDeInexistente()
    {
        await using var dbA = NewContext(TenantA);
        var run = await RunAsync(dbA, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var plano = await RemediationFor(dbA, TenantA).CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        await using var dbB = NewContext(TenantB);
        var svcB = RemediationFor(dbB, TenantB);

        (await svcB.GetAsync(plano!.Id)).Should().BeNull();
        (await svcB.ListAsync(new ActionPlanFilter())).Should().BeEmpty();
        (await svcB.UpdateAsync(plano.Id, Status(plano.Version, ActionPlanStatus.EmAndamento), Actor)).Should().BeNull();
        (await svcB.RecordExecutionAsync(plano.Id, new RecordExecutionCommand(plano.Version, "invasão", null), Actor))
            .Should().BeNull();
        (await svcB.ValidateAsync(plano.Id, new ValidateActionPlanCommand(plano.Version, null, "X", null), Actor))
            .Should().BeNull();
    }

    [Fact]
    public async Task AchadoDeOutroTenant_NaoPodeOriginarAcao()
    {
        await using var dbA = NewContext(TenantA);
        var runA = await RunAsync(dbA, TenantA, privileged: 12, withoutMfa: 2, at: T0);

        await using var dbB = NewContext(TenantB);
        var criada = await RemediationFor(dbB, TenantB).CreateForFindingAsync(Create(runA.Id, "AK-ENTRA-001"), Actor);

        criada.Should().BeNull("uma avaliação de outro tenant não existe para este — 404, não uma ação órfã");
    }

    [Fact]
    public void MutacoesDaRemediacao_ExigemPapelDeGestao_ELeituraSegueAutenticada()
    {
        var controller = typeof(RemediationController);

        controller.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(
            "a superfície inteira é autenticada — nenhuma rota anônima");

        foreach (var name in new[] { nameof(RemediationController.Create), nameof(RemediationController.Update),
                                     nameof(RemediationController.RecordExecution), nameof(RemediationController.Validate) })
        {
            var attr = controller.GetMethod(name)!.GetCustomAttribute<AuthorizeAttribute>();
            attr.Should().NotBeNull($"{name} escreve registro permanente sobre o cliente");
            attr!.Roles.Should().Be("Manager,TenantAdmin",
                "reutiliza os MESMOS papéis da publicação de fotografia — sem matriz de permissões nova");
        }

        foreach (var name in new[] { nameof(RemediationController.List), nameof(RemediationController.GetById) })
        {
            controller.GetMethod(name)!.GetCustomAttribute<AuthorizeAttribute>()
                .Should().BeNull($"{name} é leitura: herda o [Authorize] da classe e permanece acessível ao Analyst");
        }
    }

    // ================================================================================================
    // (7) Compatibilidade com o legado
    // ================================================================================================

    [Fact]
    public async Task PlanoLegadoDeRisco_PermaneceIntocado_EForaDaListaDeAchados()
    {
        await using var db = NewContext(TenantA);
        var risco = new Risk { TenantId = TenantA, Code = "SEC0001", Title = "Risco legado" };
        db.Risks.Add(risco);
        db.ActionPlans.Add(new ActionPlan
        {
            TenantId = TenantA,
            RiskId = risco.Id,                  // plano LEGADO: risco preenchido, origem KNIGHT nula
            Description = "Tratamento do risco legado",
            Status = ActionPlanStatus.Aberto,
        });
        await db.SaveChangesAsync();

        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        await RemediationFor(db, TenantA).CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        var doAchado = await RemediationFor(db, TenantA).ListAsync(new ActionPlanFilter());
        doAchado.Should().ContainSingle("a lista de remediação é a dos ACHADOS — não reapresenta o registro de riscos");
        doAchado.Single().KnightIndicatorId.Should().Be("AK-ENTRA-001");

        var legado = await db.ActionPlans.AsNoTracking().SingleAsync(p => p.RiskId == risco.Id);
        legado.KnightIndicatorId.Should().BeNull();
        legado.Version.Should().Be(0, "um plano nunca tocado por esta superfície mantém a versão inicial");

        // A consulta legada (INNER JOIN com Risks) continua vendo só os planos de risco.
        var porRisco = await (from r in db.Risks join ap in db.ActionPlans on r.Id equals ap.RiskId select ap)
            .AsNoTracking().ToListAsync();
        porRisco.Should().ContainSingle("a ação de achado tem RiskId nulo e não entra num indicador de risco");
    }

    [Fact]
    public async Task SnapshotLegado_SemContextoNovo_ContinuaComOHashPRESERVADO()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var publicado = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, run.Id);

        // Simula uma fotografia PUBLICADA ANTES desta entrega: sem cliente, sem execução de origem, sem
        // limitações e sem ações. O hash dela precisa continuar re-derivável — senão a exportação de todo o
        // histórico passaria a falhar por "integridade divergente".
        var legado = await db.PostureSnapshots.IgnoreQueryFilters()
            .Include(s => s.Indicators).Include(s => s.ActionItems)
            .FirstAsync(s => s.Id == publicado.Summary.Id);
        legado.ClientName = null;
        legado.SourceRunId = null;
        legado.CollectionLimitations.Clear();
        legado.ActionItems.Clear();

        var hashLegado = PostureSnapshotHasher.Compute(legado);

        legado.ContentHash = hashLegado;
        PostureSnapshotHasher.Verify(legado).Should().BeTrue(
            "a extensão do hash só é escrita quando há conteúdo novo — o histórico segue verificável");

        // E o contexto novo, quando existe, ENTRA no hash: alterá-lo tem de ser detectável.
        legado.ClientName = "Cliente Alfa";
        PostureSnapshotHasher.Verify(legado).Should().BeFalse(
            "se o cliente ficasse fora do conteúdo assinado, mudá-lo no banco passaria despercebido");
    }

    // ================================================================================================
    // (8) Serialização EFETIVA com as opções REAIS do MVC
    // ================================================================================================
    //
    // Program.cs chama AddControllers() sem configurar JSON, então o MVC serializa com
    // JsonSerializerDefaults.Web e SEM conversor global de enums. Foi exatamente esse detalhe que fez um
    // estado viajar como ORDINAL no Dia 1, enquanto o frontend compara com o NOME.

    private static readonly System.Text.Json.JsonSerializerOptions ApiJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void ContratosDaRemediacao_ViajamComEnumsTextuais_NasOpcoesReaisDaApi()
    {
        var validacao = new ActionPlanValidationDto(
            nameof(ActionPlanValidationMethod.NewAssessment),
            nameof(ActionPlanValidationOutcome.ReductionObserved),
            Guid.NewGuid(), null, T0, PrecedesReportedExecution: false, AppliesToCurrentCycle: true,
            3, 1, 2, true, "razão", T0, "Analista");

        var dto = new ActionPlanDto(
            Guid.NewGuid(), "AK-ENTRA-001", Guid.NewGuid(), 3,
            nameof(KnightSourceType.MicrosoftEntraId), nameof(KnightAssessmentMode.Live),
            "Título", null, null, null,
            new DateOnly(2026, 10, 1),
            nameof(ActionPlanStatus.AguardandoValidacao), IsOverdue: false, IsActive: true,
            "Próxima providência", null, null, null, null, T0, T0, 2,
            validacao, validacao,
            new[] { nameof(ActionPlanStatus.EmAndamento), nameof(ActionPlanStatus.Concluido) },
            null, new[] { validacao },
            new[] { new ActionPlanEventDto(nameof(ActionPlanEventKind.StatusChanged), T0, "Analista",
                nameof(ActionPlanStatus.Aberto), nameof(ActionPlanStatus.EmAndamento), null) });

        var json = System.Text.Json.JsonSerializer.Serialize(dto, ApiJson);

        json.Should().Contain("\"status\":\"AguardandoValidacao\"");
        json.Should().Contain("\"outcome\":\"ReductionObserved\"");
        json.Should().Contain("\"method\":\"NewAssessment\"");
        json.Should().Contain("\"kind\":\"StatusChanged\"");
        json.Should().NotContain("\"status\":4", "ordinal é justamente o que a tela não sabe ler");
        json.Should().Contain("\"dueDate\":\"2026-10-01\"", "o prazo é uma data, não um instante com fuso");

        // A PROCEDÊNCIA e as transições também viajam por NOME: é o que a tela usa para não misturar ação de
        // demonstração com ação real, e para oferecer só as etapas que o servidor aceitaria.
        json.Should().Contain("\"originSourceType\":\"MicrosoftEntraId\"");
        json.Should().Contain("\"originMode\":\"Live\"");
        json.Should().Contain("\"allowedTransitions\":[\"EmAndamento\",\"Concluido\"]");
        json.Should().Contain("\"appliesToCurrentCycle\":true");
    }

    [Fact]
    public async Task AcaoCongeladaNoSnapshot_TambemViajaComEnumsTextuais()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        await RemediationFor(db, TenantA).CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        var detail = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, run.Id);

        var json = System.Text.Json.JsonSerializer.Serialize(detail, ApiJson);

        json.Should().Contain("\"status\":\"Aberto\"");
        json.Should().Contain("\"actionItems\"");
        json.Should().Contain("\"collectionLimitations\"");
    }

    // ================================================================================================
    // (9) [correção dirigida] Conclusão com BASE REGISTRADA
    // ================================================================================================
    // A versão anterior deste pacote permitia Aberto -> Aguardando validação -> Concluído por cliques de
    // etapa: a jornada inteira podia ser encenada sem que ninguém descrevesse trabalho algum nem apresentasse
    // evidência. Estes testes fecham esse caminho e as suas variações.

    [Fact]
    public async Task AguardarValidacao_SemExecucaoRELATADA_ERecusado_PorqueFabricariaAExecucao()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);
        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        var pular = async () => await svc.UpdateAsync(
            p!.Id, Status(p.Version, ActionPlanStatus.AguardandoValidacao), Actor);

        await pular.Should().ThrowAsync<ActionPlanValidationException>(
            "aguardar validação pressupõe execução RELATADA — chegar lá por um clique afirmaria um trabalho " +
            "que ninguém descreveu");

        (await svc.GetAsync(p!.Id))!.AllowedTransitions.Should()
            .NotContain(ActionPlanStatus.AguardandoValidacao, "a tela não pode oferecer o que o servidor recusa");
    }

    [Fact]
    public async Task ConcluirPorSEQUENCIA_DeEtapas_ERecusado_ExigeExecucaoEValidacao()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        var andamento = await svc.UpdateAsync(p!.Id, Status(p.Version, ActionPlanStatus.EmAndamento), Actor);

        var atalho = async () => await svc.UpdateAsync(
            andamento!.Id, Status(andamento.Version, ActionPlanStatus.Concluido), Actor);
        await atalho.Should().ThrowAsync<ActionPlanValidationException>();

        // Com execução relatada, ainda falta a decisão de validação — e a recusa DIZ o que falta.
        var executado = await svc.RecordExecutionAsync(
            andamento!.Id, new RecordExecutionCommand(andamento.Version, "MFA registrado.", null), Actor);
        executado!.ClosureBlockedReason.Should().Contain("decisão de validação");
        executado.AllowedTransitions.Should().NotContain(ActionPlanStatus.Concluido);

        var semValidacao = async () => await svc.UpdateAsync(
            executado.Id, Status(executado.Version, ActionPlanStatus.Concluido), Actor);
        await semValidacao.Should().ThrowAsync<ActionPlanValidationException>(
            "relatar execução não comprova nada — encerrar exige a decisão de validação sobre ela");
    }

    [Fact]
    public async Task ValidacaoQueNaoCOMPROVA_NaoAutorizaEncerrar_ESemAtalhoAdministrativo()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "Ajuste aplicado.", null), Actor);

        // Nova coleta SEM melhora, POSTERIOR ao relato: fala por esta execução e não sustenta encerrar.
        var semMelhora = await RunAsync(
            db, TenantA, privileged: 12, withoutMfa: 2, at: DateTimeOffset.UtcNow.AddMinutes(5));
        var validado = await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, semMelhora.Id, null, null), Actor);

        validado!.LatestValidation!.Outcome.Should().Be(ActionPlanValidationOutcome.NoChangeObserved);
        validado.ApplicableValidation.Should().NotBeNull("ela fala por esta execução — só não sustenta encerrar");
        validado.ClosureBlockedReason.Should().Contain("não sustenta o encerramento");
        validado.AllowedTransitions.Should().NotContain(ActionPlanStatus.Concluido);

        var encerrar = async () => await svc.UpdateAsync(
            validado.Id, Status(validado.Version, ActionPlanStatus.Concluido), Actor);
        await encerrar.Should().ThrowAsync<ActionPlanValidationException>(
            "sem melhora não há encerramento com base — e não existe atalho administrativo para contorná-lo");

        // A saída honesta continua aberta: voltar para a execução.
        validado.AllowedTransitions.Should().Contain(ActionPlanStatus.EmAndamento);
    }

    [Fact]
    public async Task ColetaANTERIOR_AoRelatoDeExecucao_NaoAtribuiCausalidade_NemAutorizaEncerrar()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        // Posterior à ORIGEM, porém anterior ao relato de execução (que ocorre no relógio real, agora).
        var noMeio = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 0, at: T0.AddHours(1));
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "MFA registrado.", null), Actor);

        var validado = await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, noMeio.Id, null, null), Actor);

        // A OBSERVAÇÃO permanece verdadeira — a exposição de fato deixou de ser sinalizada.
        validado!.LatestValidation!.Outcome.Should().Be(ActionPlanValidationOutcome.ExposureCleared);
        validado.LatestValidation.PrecedesReportedExecution.Should().BeTrue();
        validado.LatestValidation.Rationale.Should().Contain("não pode ser atribuído a este trabalho");

        // Mas ela NÃO fala por esta execução: correlação temporal invertida não é causalidade.
        validado.LatestValidation.AppliesToCurrentCycle.Should().BeFalse();
        validado.ApplicableValidation.Should().BeNull();
        validado.AllowedTransitions.Should().NotContain(ActionPlanStatus.Concluido);

        var encerrar = async () => await svc.UpdateAsync(
            validado.Id, Status(validado.Version, ActionPlanStatus.Concluido), Actor);
        await encerrar.Should().ThrowAsync<ActionPlanValidationException>();
    }

    [Fact]
    public async Task ValidacaoANTIGA_NaoAutorizaOCicloNOVO_NemAposReabertura_NemAposNovaExecucao()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var concluida = await ConcluirComBaseAsync(svc, p!, "Primeira execução.");
        concluida.Validations.Should().HaveCount(1);

        // ---- Reabertura: o problema voltou. A comprovação do ciclo anterior permanece no histórico...
        var reaberta = await svc.UpdateAsync(
            concluida.Id, Status(concluida.Version, ActionPlanStatus.EmAndamento), Actor);

        reaberta!.Validations.Should().HaveCount(1, "reabrir NÃO apaga o que aconteceu");
        reaberta.ApplicableValidation.Should().BeNull("...mas ela julgou um ciclo que já foi encerrado");
        reaberta.CycleStartedAt.Should().BeOnOrAfter(concluida.CycleStartedAt, "o ciclo foi repactuado");

        // ---- ...e uma nova execução exige uma nova comprovação.
        var reexecutada = await svc.RecordExecutionAsync(
            reaberta.Id, new RecordExecutionCommand(reaberta.Version, "Segunda execução.", null), Actor);

        reexecutada!.Status.Should().Be(ActionPlanStatus.AguardandoValidacao);
        reexecutada.ApplicableValidation.Should().BeNull(
            "a validação antiga não pode continuar autorizando automaticamente a conclusão");
        reexecutada.AllowedTransitions.Should().NotContain(ActionPlanStatus.Concluido);
        reexecutada.NextStep.Should().Contain("não fala por esta execução");

        var encerrarDeNovo = async () => await svc.UpdateAsync(
            reexecutada.Id, Status(reexecutada.Version, ActionPlanStatus.Concluido), Actor);
        await encerrarDeNovo.Should().ThrowAsync<ActionPlanValidationException>();

        // Uma validação NOVA sobre a segunda execução reabre o encerramento.
        var revalidada = await svc.ValidateAsync(
            reexecutada.Id, new ValidateActionPlanCommand(reexecutada.Version, null, "CHAMADO-9999", null), Actor);
        revalidada!.ApplicableValidation.Should().NotBeNull();
        revalidada.Validations.Should().HaveCount(2, "o histórico inteiro é preservado");
        (await svc.UpdateAsync(revalidada.Id, Status(revalidada.Version, ActionPlanStatus.Concluido), Actor))!
            .Status.Should().Be(ActionPlanStatus.Concluido);
    }

    [Fact]
    public async Task AtestacaoHumana_EncerraOCiclo_MasNuncaContaComoComprovacaoTecnica()
    {
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);
        var concluida = await ConcluirComBaseAsync(svc, p!, "Tratado fora do AEGIS.");

        concluida.Status.Should().Be(ActionPlanStatus.Concluido);
        var v = concluida.LatestValidation!;
        RemediationReading.IsTechnicallyProven(v.Method, v.Outcome).Should().BeFalse(
            "encerrar sobre a palavra de alguém é decisão legítima — e continua sendo atestação, não prova");
        v.Rationale.Should().Contain("não verificou o ambiente");
    }

    [Fact]
    public void ReducaoObservada_NaoSeConfundeComExposicaoEncerrada_NemNoQueAutorizaEncerrar()
    {
        RemediationReading.SupportsClosure(ActionPlanValidationOutcome.ReductionObserved).Should().BeTrue();
        RemediationReading.SupportsClosure(ActionPlanValidationOutcome.ExposureCleared).Should().BeTrue();
        RemediationReading.SupportsClosure(ActionPlanValidationOutcome.NoChangeObserved).Should().BeFalse();
        RemediationReading.SupportsClosure(ActionPlanValidationOutcome.EvidenceInsufficient).Should().BeFalse();

        // Os dois autorizam encerrar, mas dizem coisas DIFERENTES — e é a frase que impede a leitura errada.
        RemediationReading.OutcomeLabel(ActionPlanValidationOutcome.ReductionObserved)
            .Should().Contain("ainda exposto");
        RemediationReading.OutcomeLabel(ActionPlanValidationOutcome.ExposureCleared)
            .Should().NotContain("ainda exposto");
    }

    // ================================================================================================
    // (10) [correção dirigida] Separação de ORIGEM: demonstração não responde por coleta real
    // ================================================================================================

    [Fact]
    public async Task AcaoDeDEMONSTRACAO_NaoBloqueiaAcaoREAL_DoMesmoIndicador()
    {
        await using var db = NewContext(TenantA);
        var demo = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var real = await RealRunAsync(db, TenantA, affected: 5, at: T0.AddDays(1));
        var svc = RemediationFor(db, TenantA);

        var acaoDemo = await svc.CreateForFindingAsync(Create(demo.Id, "AK-ENTRA-001"), Actor);
        acaoDemo.Should().NotBeNull();

        var acaoReal = await svc.CreateForFindingAsync(Create(real, "AK-ENTRA-001"), Actor);

        acaoReal.Should().NotBeNull(
            "o indicador SOZINHO não identifica o problema: um treino não pode ocupar a origem do real");
        acaoReal!.Id.Should().NotBe(acaoDemo!.Id);
        acaoReal.OriginSourceType.Should().Be(KnightSourceType.MicrosoftEntraId);
        acaoReal.OriginMode.Should().Be(KnightAssessmentMode.Live);

        // E a repetição DENTRO de cada procedência continua barrada.
        var segundoCliqueReal = async () => await svc.CreateForFindingAsync(Create(real, "AK-ENTRA-001"), Actor);
        await segundoCliqueReal.Should().ThrowAsync<ActionPlanConflictException>();
    }

    [Fact]
    public async Task ListaPorFONTE_NaoMisturaAcaoDeDemonstracaoComAchadoReal()
    {
        await using var db = NewContext(TenantA);
        var demo = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var real = await RealRunAsync(db, TenantA, affected: 5, at: T0.AddDays(1));
        var svc = RemediationFor(db, TenantA);

        var acaoDemo = await svc.CreateForFindingAsync(Create(demo.Id, "AK-ENTRA-001"), Actor);
        var acaoReal = await svc.CreateForFindingAsync(Create(real, "AK-ENTRA-001"), Actor);

        var noReal = await svc.ListAsync(new ActionPlanFilter(
            SourceType: KnightSourceType.MicrosoftEntraId, Mode: KnightAssessmentMode.Live));
        var noDemo = await svc.ListAsync(new ActionPlanFilter(
            SourceType: KnightSourceType.Demo, Mode: KnightAssessmentMode.Demo));

        noReal.Select(x => x.Id).Should().Equal(acaoReal!.Id);
        noDemo.Select(x => x.Id).Should().Equal(acaoDemo!.Id);
        (await svc.ListAsync(new ActionPlanFilter())).Should().HaveCount(2, "sem recorte, o produto mostra as duas");
    }

    [Fact]
    public async Task RelatorioREAL_ExcluiAcaoDeDemonstracao_EPreservaAProveniencia()
    {
        await using var db = NewContext(TenantA);
        var demo = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var real = await RealRunAsync(db, TenantA, affected: 5, at: T0.AddDays(1));
        var svc = RemediationFor(db, TenantA);

        await svc.CreateForFindingAsync(Create(demo.Id, "AK-ENTRA-001"), Actor);
        var acaoReal = await svc.CreateForFindingAsync(Create(real, "AK-ENTRA-001"), Actor);
        var executada = await svc.RecordExecutionAsync(
            acaoReal!.Id, new RecordExecutionCommand(acaoReal.Version, "MFA registrado.", null), Actor);
        await svc.ValidateAsync(
            executada!.Id, new ValidateActionPlanCommand(executada.Version, null, "CHAMADO-4321", null), Actor);

        await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, real);

        var snapshot = (await db.PostureSnapshots.AsNoTracking()
                .Include(x => x.ActionItems).Include(x => x.Indicators).ToListAsync())
            .OrderByDescending(x => x.CapturedAt).First();

        snapshot.ActionItems.Should().HaveCount(1,
            "uma ação de demonstração no relatório de uma coleta real seria apresentada como trabalho real");
        var item = snapshot.ActionItems.Single();
        item.ActionPlanId.Should().Be(acaoReal.Id);

        // Proveniência CONGELADA: origem e evidência humana continuam identificáveis no papel, sem depender
        // de consultar dados que podem mudar depois da publicação.
        item.OriginRunId.Should().Be(real);
        item.ValidationEvidenceReference.Should().Be("CHAMADO-4321");
        item.ValidationMethod.Should().Be(ActionPlanValidationMethod.HumanEvidence);

        PostureSnapshotHasher.Verify(snapshot).Should().BeTrue("a proveniência entra no conteúdo assinado");
    }

    [Fact]
    public async Task ProvenienciaDaValidacaoAutomatica_ECongelada_ComAAvaliacaoDeEvidencia()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "MFA registrado.", null), Actor);

        // Coleta POSTERIOR ao relato de execução: é a que comprova.
        var nova = await RunAsync(
            db, TenantA, privileged: 12, withoutMfa: 0, at: DateTimeOffset.UtcNow.AddMinutes(5));
        var validado = await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, nova.Id, null, null), Actor);

        validado!.ApplicableValidation.Should().NotBeNull("a evidência é posterior ao trabalho relatado");
        await svc.UpdateAsync(validado.Id, Status(validado.Version, ActionPlanStatus.Concluido), Actor);

        await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, nova.Id);
        var snapshot = (await db.PostureSnapshots.AsNoTracking().Include(x => x.ActionItems).ToListAsync())
            .OrderByDescending(x => x.CapturedAt).First();

        var item = snapshot.ActionItems.Single();
        item.OriginRunId.Should().Be(origem.Id, "de onde veio o 'antes'");
        item.ValidationRunId.Should().Be(nova.Id, "de onde veio o 'depois' — e são referências DISTINTAS");
        item.EvidenceCollectedAt.Should().NotBeNull();
        item.PrecedesReportedExecution.Should().BeFalse();

        PostureSnapshotPdfWriter.ProvenanceText(item).Should()
            .Contain("origem").And.Contain("evidência", "o PDF precisa dizer o que comparou com o quê");
    }

    [Fact]
    public async Task RelatorioPUBLICADO_ImprimeAProvenienciaCongelada_EARessalvaDeCausalidade()
    {
        await using var db = NewContext(TenantA);
        var origem = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 2, at: T0);
        // Posterior à ORIGEM, porém ANTERIOR ao relato de execução (que ocorre no relógio real, agora).
        var noMeio = await RunAsync(db, TenantA, privileged: 12, withoutMfa: 0, at: T0.AddHours(1));
        var svc = RemediationFor(db, TenantA);

        var p = await svc.CreateForFindingAsync(Create(origem.Id, "AK-ENTRA-001"), Actor);
        var executado = await svc.RecordExecutionAsync(
            p!.Id, new RecordExecutionCommand(p.Version, "MFA registrado.", null), Actor);
        await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, noMeio.Id, null, null), Actor);

        var publicado = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, origem.Id);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(x => x.Indicators).Include(x => x.ActionItems)
            .FirstAsync(x => x.Id == publicado.Summary.Id);

        var item = snapshot.ActionItems.Single();

        // PROVENIÊNCIA CONGELADA: as duas avaliações e o instante da coleta ficam NO relatório. Guardar só o
        // resultado numérico obrigaria a consultar depois dados que podem ter mudado — que é exatamente o que
        // uma fotografia auditável existe para dispensar.
        item.OriginRunId.Should().Be(origem.Id);
        item.ValidationRunId.Should().Be(noMeio.Id);
        item.EvidenceCollectedAt.Should().NotBeNull();
        item.PrecedesReportedExecution.Should().BeTrue();

        var prov = PostureSnapshotPdfWriter.ProvenanceText(item)!;
        prov.Should().Contain("origem " + origem.Id.ToString("D")[..8])
            .And.Contain("evidência " + noMeio.Id.ToString("D")[..8])
            .And.Contain("coletada em", "sem o instante da coleta a ordem temporal não é verificável no papel");

        PostureSnapshotPdfWriter.CausalityCaveatText.Should()
            .Contain("ANTERIOR ao relato de execução").And.Contain("não é atribuível",
                "o relatório precisa dizer que a melhora observada não é atribuída a esta ação");

        // E o PDF REAL é renderizado. A asserção é por PALAVRAS distintivas: a extração de glifos intercala
        // as colunas de uma tabela, e cobrar uma frase inteira quebraria no CI sem que nada tivesse mudado.
        var texto = Deaccent(ExtractPdfText(PostureSnapshotPdfWriter.Write(snapshot)));
        texto.Should().Contain("ANTERIOR", "a ressalva de causalidade é impressa, não omitida");
        texto.Should().Contain(origem.Id.ToString("D")[..8], "a avaliação de origem é identificável no papel");
        texto.Should().Contain(noMeio.Id.ToString("D")[..8], "a avaliação usada como evidência também");

        PostureSnapshotHasher.Verify(snapshot).Should().BeTrue("a proveniência entra no conteúdo assinado");
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static readonly RemediationActor Actor = new(Guid.Parse("cccccccc-5555-5555-5555-555555555555"), "Analista");

    private static CreateFindingActionPlanCommand Create(Guid runId, string indicatorId) =>
        new(runId, indicatorId, "Registrar segundo fator", "Ação proposta.", "Equipe de Identidade", "TI", null);

    private static UpdateActionPlanCommand Status(int version, ActionPlanStatus status) =>
        new(version, null, null, null, null, null, status);

    /// <summary>
    /// Encerra uma ação pelo caminho LEGÍTIMO: relata a execução e registra uma atestação humana com
    /// evidência referenciada sobre ESSA execução. É o mínimo que o servidor aceita — e o teste que precisa
    /// de uma ação encerrada usa isto em vez de empurrar a etapa, que é o atalho recusado.
    /// </summary>
    private static async Task<ActionPlanView> ConcluirComBaseAsync(
        IRemediationService svc, ActionPlanView plano, string relato)
    {
        var executado = await svc.RecordExecutionAsync(
            plano.Id, new RecordExecutionCommand(plano.Version, relato, null), Actor);
        var validado = await svc.ValidateAsync(
            executado!.Id, new ValidateActionPlanCommand(executado.Version, null, "CHAMADO-1234", null), Actor);
        var concluida = await svc.UpdateAsync(
            validado!.Id, Status(validado.Version, ActionPlanStatus.Concluido), Actor);
        return concluida!;
    }

    private static KnightCapabilityStatus[] Collected(params KnightCapability[] capabilities) =>
        capabilities.Select(c => new KnightCapabilityStatus(c, KnightCapabilityOutcome.Collected)).ToArray();

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>
    /// Grava uma execução de coleta REAL (Entra/Live) diretamente, com o mesmo catálogo das demais. Não há
    /// coletor real neste ambiente de teste, e simular um não acrescentaria nada ao que está sob prova: o que
    /// importa é existir uma avaliação cuja PROCEDÊNCIA seja diferente da demonstração.
    /// </summary>
    private static async Task<Guid> RealRunAsync(
        AegisScoreDbContext db, Guid tenantId, int affected, DateTimeOffset at)
    {
        var run = new KnightAssessmentRun
        {
            TenantId = tenantId,
            Mode = KnightAssessmentMode.Live,
            SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID",
            Status = KnightRunStatus.Completed,
            CatalogVersion = KnightCatalog.Version,
            StartedAt = at,
            CompletedAt = at,
            Score = 60,
            Coverage = 100,
            ScoreFormulaVersion = "knight-score-v1",
            ExposedCount = 1,
            CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(
                Collected(KnightCapability.PrivilegedRoleInventory, KnightCapability.MfaRegistration),
                KnightCapabilitiesJson.Options),
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            TenantId = tenantId,
            IndicatorId = "AK-ENTRA-001",
            Title = "Contas privilegiadas sem MFA",
            Category = KnightIndicatorCategory.PrivilegedAccess,
            Severity = SeverityLevel.Critical,
            Status = KnightIndicatorStatus.Exposed,
            AffectedObjectCount = affected,
            Evidence = affected + " conta(s) privilegiada(s) sem metodo de MFA registrado.",
            SourceType = KnightSourceType.MicrosoftEntraId,
            CollectedAt = at,
        });

        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static IRemediationService RemediationFor(AegisScoreDbContext db, Guid tenantId) =>
        new RemediationService(db, new SystemTenantContext(tenantId), TimeProvider.System);

    private static IPostureSnapshotService PostureFor(AegisScoreDbContext db, Guid tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId),
            new AegisScore.Infrastructure.Connectors.NistSignalMapper(db));

    /// <summary>
    /// Executa uma avaliação com fatos, capacidades e INSTANTE controlados pelo teste — a ordem temporal é
    /// justamente uma das coisas sob prova, e não pode depender do relógio da máquina.
    /// </summary>
    private static Task<KnightAssessment> RunAsync(
        AegisScoreDbContext db, Guid tenantId, int privileged, int withoutMfa, DateTimeOffset at,
        KnightSourceState state = KnightSourceState.Completed,
        IReadOnlyList<KnightCapabilityStatus>? capabilities = null)
    {
        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, privileged),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, withoutMfa),
        });

        // Os objetos preservados são um PREFIXO estável do mesmo universo: assim, quando a contagem cai, os
        // que saíram são identificáveis pela comparação dos conjuntos — e o teste prova a diferença entre
        // "quantos saíram" e "a quantidade caiu".
        var objetos = Enumerable.Range(1, withoutMfa)
            .Select(i => new KnightAffectedObjectFact(
                $"conta-{i:00}", KnightAffectedObjectKind.User, $"Conta {i:00}", $"conta{i:00}@demo.example.com",
                new[] { "Administrador Global" }, "Sem método capaz de MFA no relatório de registro."))
            .ToList();

        var result = new KnightCollectionResult(
            KnightSourceType.Demo, state, "Provedor de Demonstração AEGIS KNIGHT", facts,
            capabilities ?? Collected(KnightCapability.PrivilegedRoleInventory, KnightCapability.MfaRegistration),
            at,
            AffectedObjects: new[]
            {
                new KnightAffectedObjectEvidence(KnightSignalKey.PrivilegedAccountsWithoutMfa, objetos, IsComplete: true),
            });

        return ServiceFor(db, tenantId, new StubCollector(result)).RunDemoAssessmentAsync();
    }

    private static IAegisKnightAssessmentService ServiceFor(
        AegisScoreDbContext db, Guid tenantId, IKnightCollector collector)
    {
        var registry = new KnightCollectorRegistry(new[] { collector });
        var tenant = new SystemTenantContext(tenantId);
        var config = new DemoOnlyConfigProvider();
        var evidence = new AegisScore.Infrastructure.Identity.IdentityEvidenceService(db, registry, config, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new NoAdvisoryGenerator(), evidence, tenant);
    }

    private sealed class StubCollector : IKnightCollector
    {
        private readonly KnightCollectionResult _result;
        public StubCollector(KnightCollectionResult result) => _result = result;
        public KnightSourceType Source => KnightSourceType.Demo;
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private sealed class DemoOnlyConfigProvider : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(new KnightDemoConfiguration());
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed class NoAdvisoryGenerator : IKnightAdvisoryGenerator
    {
        public Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default) =>
            Task.FromResult(new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false));
    }
}
