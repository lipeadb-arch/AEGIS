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
        var emAndamento = await svc.UpdateAsync(primeira!.Id, Status(primeira.Version, ActionPlanStatus.EmAndamento), Actor);
        var aguardando = await svc.UpdateAsync(emAndamento!.Id, Status(emAndamento.Version, ActionPlanStatus.AguardandoValidacao), Actor);
        var concluida = await svc.UpdateAsync(aguardando!.Id, Status(aguardando.Version, ActionPlanStatus.Concluido), Actor);
        concluida!.IsActive.Should().BeFalse();

        var segundoCiclo = await svc.CreateForFindingAsync(Create(run.Id, "AK-ENTRA-001"), Actor);

        segundoCiclo.Should().NotBeNull("um problema que reaparece merece um ciclo novo, não a reabertura forçada");
        segundoCiclo!.Id.Should().NotBe(primeira.Id);
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
            Guid.NewGuid(), null, 3, 1, 2, true, "razão", T0, "Analista");

        var dto = new ActionPlanDto(
            Guid.NewGuid(), "AK-ENTRA-001", Guid.NewGuid(), 3, "Título", null, null, null,
            new DateOnly(2026, 10, 1),
            nameof(ActionPlanStatus.AguardandoValidacao), IsOverdue: false, IsActive: true,
            "Próxima providência", null, null, null, null, T0, 2,
            validacao, new[] { validacao },
            new[] { new ActionPlanEventDto(nameof(ActionPlanEventKind.StatusChanged), T0, "Analista",
                nameof(ActionPlanStatus.Aberto), nameof(ActionPlanStatus.EmAndamento), null) });

        var json = System.Text.Json.JsonSerializer.Serialize(dto, ApiJson);

        json.Should().Contain("\"status\":\"AguardandoValidacao\"");
        json.Should().Contain("\"outcome\":\"ReductionObserved\"");
        json.Should().Contain("\"method\":\"NewAssessment\"");
        json.Should().Contain("\"kind\":\"StatusChanged\"");
        json.Should().NotContain("\"status\":4", "ordinal é justamente o que a tela não sabe ler");
        json.Should().Contain("\"dueDate\":\"2026-10-01\"", "o prazo é uma data, não um instante com fuso");
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

    // ---- Helpers ----------------------------------------------------------------------------------

    private static readonly RemediationActor Actor = new(Guid.Parse("cccccccc-5555-5555-5555-555555555555"), "Analista");

    private static CreateFindingActionPlanCommand Create(Guid runId, string indicatorId) =>
        new(runId, indicatorId, "Registrar segundo fator", "Ação proposta.", "Equipe de Identidade", "TI", null);

    private static UpdateActionPlanCommand Status(int version, ActionPlanStatus status) =>
        new(version, null, null, null, null, null, status);

    private static KnightCapabilityStatus[] Collected(params KnightCapability[] capabilities) =>
        capabilities.Select(c => new KnightCapabilityStatus(c, KnightCapabilityOutcome.Collected)).ToArray();

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

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
