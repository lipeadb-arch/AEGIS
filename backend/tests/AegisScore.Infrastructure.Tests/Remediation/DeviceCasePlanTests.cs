using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Remediation;
using AegisScore.Infrastructure.Tests.Connectors;   // SyntheticDeviceSources
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Remediation;

/// <summary>
/// [AEGIS-JOURNEY-01] Plano de ação originado de um CASO de vulnerabilidade em dispositivo (ativo × CVE), pelo caminho
/// real: conectores reais do Defender e do Intune sobre HTTP SINTÉTICO → executor de ingestão → resolução por chave forte
/// → SQLite descartável → autoridade da prioridade → serviço de remediação. Prova os contratos que a nova origem mudou:
/// o servidor obtém o caso e o contexto; a chave do caso é canônica (duplicidade, novo ciclo, reabertura); o registro de
/// origem sobrevive à mudança da evidência e à saída da fila; a comparação de avaliações do KNIGHT é recusada; e nada do
/// ciclo do plano altera a situação técnica. Tudo é sintético (GUIDs inventados, demo.example.com). Concorrência real,
/// papéis por JWT e a jornada por HTTP ficam em <c>DeviceCasePlanHttpTests</c> (PostgreSQL).
/// </summary>
public sealed class DeviceCasePlanTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("e8000000-0000-0000-0000-0000000000e8");
    private static readonly Guid TenantB = Guid.Parse("f9000000-0000-0000-0000-0000000000f9");

    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DevX = SyntheticDeviceSources.DevX;
    private const string CveCritica = "CVE-2024-3001";
    private const string CveMedia = "CVE-2024-3002";

    private static readonly RemediationActor Gestora = new(Guid.Parse("a8000000-0000-0000-0000-0000000000a8"), "Gestora Sintética");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly DateTimeOffset _base;
    private readonly SyntheticDeviceSources _src;
    private readonly FakeTimeProvider _clock;

    public DeviceCasePlanTests()
    {
        _base = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        _src = new SyntheticDeviceSources { IntuneNow = _base };
        _clock = new FakeTimeProvider(_base.AddHours(2));
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Sintético G", Slug = "sintetico-g", Status = TenantStatus.Active });
        ctx.Tenants.Add(new Tenant { Id = TenantB, Name = "Cliente Sintético H", Slug = "sintetico-h", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ================= Criação pelo servidor e ciclo sem efeito técnico ===============================================

    [Fact]
    public async Task Criacao_ObtemCasoEContextoPelaAutoridade_EOCicloNaoAlteraASituacaoTecnica()
    {
        var (_, a, _) = await CenarioAsync();
        var antes = await EstadoTecnicoAsync();
        var prioridadeAntes = await DetalheAsync(a);
        prioridadeAntes.Band.Should().Be(DevicePriorityBands.P1);
        prioridadeAntes.DeterminingCase!.CveId.Should().Be(CveCritica);

        // A CVE chega com espaço e em minúsculas: a chave do caso é a forma canônica, não o texto do navegador.
        var plano = await CriarAsync(a, "  cve-2024-3001 ");

        plano.OriginKind.Should().Be(ActionPlanOriginKind.DeviceVulnerability);
        plano.KnightIndicatorId.Should().BeNull("nenhum indicador KNIGHT é inventado para encaixar o caso");
        plano.OriginRunId.Should().BeNull("nenhuma avaliação fictícia é criada");
        var o = plano.DeviceOrigin!;
        o.Schema.Should().Be(DeviceCaseOrigin.SchemaV1);
        o.AssetId.Should().Be(a);
        o.CveId.Should().Be(CveCritica);
        o.CaseBand.Should().Be(prioridadeAntes.DeterminingCase.Band, "a faixa vem da autoridade, na leitura do servidor");
        o.CaseBandLabel.Should().Be(prioridadeAntes.DeterminingCase.BandLabel);
        o.WasDeterminingCase.Should().BeTrue();
        o.DeviceBand.Should().Be(DevicePriorityBands.P1);
        o.PolicyCode.Should().Be(DevicePriorityPolicy.Code);
        o.PolicyVersion.Should().Be(DevicePriorityPolicy.Version);
        o.Source.Should().Be(prioridadeAntes.DeterminingCase.Source);
        o.AcquiredAt.Should().Be(prioridadeAntes.DeterminingCase.AcquiredAt);
        o.FirstSeenAt.Should().Be(prioridadeAntes.DeterminingCase.FirstSeenAt);
        o.Factors.Single(f => f.Code == "technicalSeverity").Label.Should().Be("Severidade técnica (este caso)");
        o.Factors.Single(f => f.Code == "technicalSeverity").Value.Should().Be("Crítica (CVSS 9.8)");
        o.Factors.Single(f => f.Code == "deviceManagement").Effect.Should().Be(DevicePriorityEffects.Aggravating);
        o.Factors.Should().Contain(f => f.Code == "assetCriticality");
        plano.Events.Single().Note.Should().Contain(CveCritica).And.Contain("AEGIS-PRIO-DEV v1");

        // Vínculos verificáveis: a entrada do catálogo e a exposição consolidada que sustentavam o caso.
        await using (var db = NewContext(TenantA))
        {
            var linha = await db.ActionPlans.AsNoTracking().SingleAsync(p => p.Id == plano.Id);
            var threat = await db.Threats.AsNoTracking().SingleAsync(t => t.Code == CveCritica);
            var exposure = await db.AssetThreatExposures.AsNoTracking().SingleAsync(x => x.AssetId == a && x.ThreatId == threat.Id);
            linha.OriginCveId.Should().Be(CveCritica);
            linha.OriginAssetId.Should().Be(a);
            linha.OriginThreatId.Should().Be(threat.Id);
            linha.OriginExposureId.Should().Be(exposure.Id);
            linha.RiskId.Should().BeNull("nenhum risco fictício é criado para a FK");
        }

        // Ciclo inteiro: editar, relatar execução, atestar e concluir.
        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.UpdateAsync(plano.Id, new UpdateActionPlanCommand(
            plano.Version, null, null, "Ana Souza", "Estações de trabalho", DateOnly.FromDateTime(_base.UtcDateTime.AddDays(20)), null), Gestora)))!;
        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.RecordExecutionAsync(plano.Id,
            new RecordExecutionCommand(plano.Version, "Atualização do fornecedor aplicada.", "CHG-0001"), Gestora)))!;
        plano.Status.Should().Be(ActionPlanStatus.AguardandoValidacao);
        plano.NextStep.Should().Contain("atestação humana").And.Contain("ainda não está disponível");
        plano.ClosureBlockedReason.Should().Contain("atestação humana").And.NotContain("coleta posterior",
            "a providência não sugere um caminho de validação que a API recusa para esta origem");

        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.ValidateAsync(plano.Id,
            new ValidateActionPlanCommand(plano.Version, null, "CHG-0001 · relatório do fornecedor", null), Gestora)))!;
        plano.ApplicableValidation!.Method.Should().Be(ActionPlanValidationMethod.HumanEvidence);
        plano.ApplicableValidation.Outcome.Should().Be(ActionPlanValidationOutcome.HumanAttested);
        plano.AllowedTransitions.Should().Contain(ActionPlanStatus.Concluido);

        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.UpdateAsync(plano.Id,
            new UpdateActionPlanCommand(plano.Version, null, null, null, null, null, ActionPlanStatus.Concluido), Gestora)))!;
        plano.Status.Should().Be(ActionPlanStatus.Concluido);
        plano.NextStep.Should().Contain("não comprova que a CVE foi corrigida");

        // Nada do ciclo do plano tocou a situação técnica: exposições, disposições, observações, ativos, fontes, ledger.
        (await EstadoTecnicoAsync()).Should().Be(antes);
        var prioridadeDepois = await DetalheAsync(a);
        prioridadeDepois.Band.Should().Be(prioridadeAntes.Band);
        prioridadeDepois.DeterminingCase!.CveId.Should().Be(CveCritica);
        prioridadeDepois.CasesByBand.Select(c => (c.Band, c.Count)).Should().Equal(prioridadeAntes.CasesByBand.Select(c => (c.Band, c.Count)));

        // A situação na fonte continua sendo lida, separada do plano concluído.
        var leitura = (await Servico(TenantA, s => s.GetDeviceCaseReadingAsync(plano.Id)))!;
        leitura.State.Should().Be(DevicePriorityCaseStates.Open, "a fonte ainda reporta a CVE — plano concluído ≠ ambiente corrigido");
        leitura.Case!.Band.Should().Be(DevicePriorityBands.P1);
        leitura.VerificationNote.Should().Contain("pendente");
    }

    [Fact]
    public async Task ComparacaoDeAvaliacaoKnight_RecusadaParaCasoDeDispositivo_ENadaEhGravado()
    {
        var (_, a, _) = await CenarioAsync();
        var runKnight = await AvaliacaoKnightAsync();
        var plano = await CriarAsync(a, CveCritica);
        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.RecordExecutionAsync(plano.Id,
            new RecordExecutionCommand(plano.Version, "Atualização aplicada.", null), Gestora)))!;
        var versao = plano.Version;

        _clock.Advance(TimeSpan.FromMinutes(5));
        var recusa = async () => await Servico(TenantA, s => s.ValidateAsync(plano.Id,
            new ValidateActionPlanCommand(versao, runKnight, null, null), Gestora));
        (await recusa.Should().ThrowAsync<ActionPlanValidationException>())
            .Which.Message.Should().Contain("não se aplica").And.Contain("atestação humana");

        var relido = (await Servico(TenantA, s => s.GetAsync(plano.Id)))!;
        relido.Validations.Should().BeEmpty("a recusa não deixa validação registrada");
        relido.Version.Should().Be(versao);
        relido.Status.Should().Be(ActionPlanStatus.AguardandoValidacao);
    }

    // ================= Chave do caso, duplicidade, novo ciclo e reabertura ============================================

    [Fact]
    public async Task ChaveCanonica_SegundoCliqueAbreOExistente_ConcluidoLiberaNovoCiclo_EReaberturaNaoDuplica()
    {
        var (_, a, b) = await CenarioAsync();
        var primeiro = await CriarAsync(a, CveCritica);

        var segundo = async () => await CriarAsync(a, "CVE-2024-3001", titulo: "Segundo clique");
        (await segundo.Should().ThrowAsync<ActionPlanConflictException>())
            .Which.ExistingActionPlanId.Should().Be(primeiro.Id, "o segundo clique abre o plano existente");

        // Outro caso do mesmo dispositivo e a mesma CVE em outro dispositivo são casos DIFERENTES.
        (await CriarAsync(a, CveMedia)).Id.Should().NotBe(primeiro.Id);
        (await CriarAsync(b, CveCritica)).Id.Should().NotBe(primeiro.Id);

        // Encerrado o ciclo, a origem fica livre para um novo plano do mesmo caso.
        var concluido = await ConcluirAsync(primeiro);
        concluido.Status.Should().Be(ActionPlanStatus.Concluido);
        var novoCiclo = await CriarAsync(a, CveCritica, titulo: "Novo ciclo");
        novoCiclo.Id.Should().NotBe(primeiro.Id);

        // Reabrir o plano antigo enquanto o novo está ativo criaria dois ciclos simultâneos.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var reabrir = async () => await Servico(TenantA, s => s.UpdateAsync(concluido.Id,
            new UpdateActionPlanCommand(concluido.Version, null, null, null, null, null, ActionPlanStatus.EmAndamento), Gestora));
        (await reabrir.Should().ThrowAsync<ActionPlanConflictException>())
            .Which.ExistingActionPlanId.Should().Be(novoCiclo.Id);

        var doCaso = await Servico(TenantA, s => s.ListAsync(
            new ActionPlanFilter(Origin: ActionPlanOriginScope.DeviceVulnerability, AssetId: a, CveId: "cve-2024-3001")));
        doCaso.Select(p => p.Id).Should().BeEquivalentTo(new[] { primeiro.Id, novoCiclo.Id });
        doCaso.Count(p => p.IsActive).Should().Be(1);
    }

    [Fact]
    public async Task CasoForaDaLeitura_DisposicaoEOutroTenant_NaoCriamPlano_EEscritaExigeOsPapeisDeSempre()
    {
        var (_, a, _) = await CenarioAsync();

        // CVE que a fonte nunca reportou neste dispositivo: não há caso — nada é criado a partir do navegador.
        var inexistente = async () => await CriarAsync(a, "CVE-2099-0001");
        (await inexistente.Should().ThrowAsync<ActionPlanValidationException>())
            .Which.Message.Should().Contain("Não é possível abrir um plano");

        // Caso com disposição humana registrada: fora da fila; a leitura diz isso, e não abre plano.
        await DisposicaoAsync(a, CveMedia, ExposureStatus.Accepted);
        await using (var db = NewContext(TenantA))
        {
            var caso = (await Query(db).GetCaseAsync(a, CveMedia))!;
            caso.State.Should().Be(DevicePriorityCaseStates.OpenWithDisposition);
            caso.DispositionLabel.Should().Be("Risco aceito");
            caso.StateExplanation.Should().Contain("não correção");
        }
        var comDisposicao = async () => await CriarAsync(a, CveMedia);
        await comDisposicao.Should().ThrowAsync<ActionPlanValidationException>();

        // Isolamento: o ativo e o plano do tenant A são inexistentes para o tenant B.
        var plano = await CriarAsync(a, CveCritica);
        (await Servico(TenantB, s => s.CreateForDeviceCaseAsync(
            new CreateDeviceCaseActionPlanCommand(a, CveCritica, "Tentativa cruzada", null, null, null, null), Gestora)))
            .Should().BeNull("um ativo de outro tenant é indistinguível de inexistente");
        (await Servico(TenantB, s => s.GetAsync(plano.Id))).Should().BeNull();
        (await Servico(TenantB, s => s.GetDeviceCaseReadingAsync(plano.Id))).Should().BeNull();
        (await Servico(TenantB, s => s.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.All)))).Should().BeEmpty();
        (await Servico(TenantA, s => s.CreateForDeviceCaseAsync(
            new CreateDeviceCaseActionPlanCommand(Guid.NewGuid(), CveCritica, "Ativo inexistente", null, null, null, null), Gestora)))
            .Should().BeNull();

        // A escrita da nova origem usa a MESMA matriz: Manager e TenantAdmin; leitura, qualquer papel autenticado.
        Roles(nameof(RemediationController.CreateForDeviceCase)).Should().Be("Manager,TenantAdmin");
        Roles(nameof(RemediationController.SourceReading)).Should().BeNull();
        typeof(RemediationController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
    }

    // ================= Registro de origem × leitura atual ===========================================================

    [Fact]
    public async Task RegistroDeOrigemPreservado_QuandoAEvidenciaMuda_EPlanoSegueAcessivelForaDaFila()
    {
        var (defender, a, _) = await CenarioAsync();
        // O caso da CVE média: Média sem exploit (P4) antecipada para P3 pela situação entre fontes do dispositivo.
        var plano = await CriarAsync(a, CveMedia);
        var origem = plano.DeviceOrigin!;
        origem.CaseBand.Should().Be(DevicePriorityBands.P3);
        origem.WasDeterminingCase.Should().BeFalse("o plano pode nascer de qualquer caso do dispositivo, não só do determinante");

        // Nova coleta do Intune: o dispositivo passa a conforme e com criptografia — o agravante some e a faixa ATUAL cai;
        // a registrada na origem, não.
        Guid intune;
        await using (var db = NewContext(TenantA))
            intune = (await db.Connectors.AsNoTracking().SingleAsync(c => c.Capability == ConnectorCapability.ConfigAnalyzer)).Id;
        _src.IntuneDevices = SyntheticDeviceSources.Page(
            "{\"id\":\"int-a\",\"complianceState\":\"compliant\",\"operatingSystem\":\"Windows\",\"lastSyncDateTime\":" +
            Q(_base.AddHours(-1).ToString("O")) + ",\"isEncrypted\":true,\"azureADDeviceId\":" + Q(DevX) + "}");
        await Sync(intune);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var leitura = (await Servico(TenantA, s => s.GetDeviceCaseReadingAsync(plano.Id)))!;
        leitura.State.Should().Be(DevicePriorityCaseStates.Open);
        leitura.Case!.Band.Should().Be(DevicePriorityBands.P4, "sem a situação entre fontes, a CVE média volta à faixa base");
        leitura.ComparisonNote.Should().Contain("difere").And.Contain("não comprova correção");
        var relido = (await Servico(TenantA, s => s.GetAsync(plano.Id)))!;
        relido.DeviceOrigin.Should().BeEquivalentTo(origem, "o registro de origem nunca é reescrito pela leitura atual");
        relido.Status.Should().Be(ActionPlanStatus.Aberto);
        relido.Version.Should().Be(plano.Version);

        // Aquisição completa seguinte sem a CVE média neste dispositivo: o caso sai da fila — e isso NÃO é correção.
        // O catálogo devolve só as CVEs pedidas: uma CVE não pedida na resposta tornaria a coleta incompleta.
        DefenderData(Maquinas(), new[] { ("mde-a", CveCritica), ("mde-b", CveCritica) },
            Cve(CveCritica, "Critical", 9.8, publicExploit: true));
        await Sync(defender);
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await DetalheAsync(a)).Cases!.Items.Select(c => c.CveId).Should().NotContain(CveMedia);
        leitura = (await Servico(TenantA, s => s.GetDeviceCaseReadingAsync(plano.Id)))!;
        leitura.State.Should().Be(DevicePriorityCaseStates.NotReported);
        leitura.Case.Should().BeNull();
        leitura.Explanation.Should().Contain("não comprova a correção").And.Contain("não mais reportada");
        leitura.VerificationNote.Should().Contain("saída da fila");

        relido = (await Servico(TenantA, s => s.GetAsync(plano.Id)))!;
        relido.DeviceOrigin.Should().BeEquivalentTo(origem);
        relido.Status.Should().Be(ActionPlanStatus.Aberto, "saída da fila não muda a etapa do plano");
        relido.Validations.Should().BeEmpty("ausência na leitura não vira validação");
        (await Servico(TenantA, s => s.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.DeviceVulnerability))))
            .Select(p => p.Id).Should().Contain(plano.Id, "o plano continua acessível depois que o caso sai da fila");
    }

    // ================= Compatibilidade com as origens existentes =====================================================

    [Fact]
    public async Task ListaPadraoContinuaSoKnight_RecortesDeOrigem_ELegadoDeRiscoFora()
    {
        var (_, a, _) = await CenarioAsync();
        var run = await AvaliacaoKnightAsync();
        var knight = (await Servico(TenantA, s => s.CreateForFindingAsync(
            new CreateFindingActionPlanCommand(run, "AK-ENTRA-001", "Registrar segundo fator", null, null, null, null), Gestora)))!;
        Guid legado;
        await using (var db = NewContext(TenantA))
        {
            var risco = new Risk { TenantId = TenantA, Code = "SEC0001", Title = "Risco legado sintético" };
            var plano = new ActionPlan { TenantId = TenantA, RiskId = risco.Id, Description = "Plano legado de tratamento" };
            db.Risks.Add(risco);
            db.ActionPlans.Add(plano);
            await db.SaveChangesAsync();
            legado = plano.Id;
        }
        var dispositivo = await CriarAsync(a, CveCritica);

        knight.OriginKind.Should().Be(ActionPlanOriginKind.KnightFinding);
        knight.DeviceOrigin.Should().BeNull();
        (await Servico(TenantA, s => s.ListAsync(new ActionPlanFilter()))).Select(p => p.Id)
            .Should().Equal(new[] { knight.Id }, "quem já consultava a lista recebe exatamente o que recebia");
        (await Servico(TenantA, s => s.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.DeviceVulnerability))))
            .Select(p => p.Id).Should().Equal(dispositivo.Id);
        (await Servico(TenantA, s => s.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.All))))
            .Select(p => p.Id).Should().BeEquivalentTo(new[] { knight.Id, dispositivo.Id }, "o registro de riscos nunca entra");

        await using (var db = NewContext(TenantA))
        {
            var linha = await db.ActionPlans.AsNoTracking().SingleAsync(p => p.Id == legado);
            linha.OriginKind.Should().BeNull();
            linha.ResolveOriginKind().Should().Be(ActionPlanOriginKind.RiskTreatment);
        }
        (await Servico(TenantA, s => s.GetAsync(legado)))!.OriginKind.Should().Be(ActionPlanOriginKind.RiskTreatment);

        // O caminho de leitura da fonte é exclusivo da nova origem.
        var foraDeOrigem = async () => await Servico(TenantA, s => s.GetDeviceCaseReadingAsync(knight.Id));
        await foraDeOrigem.Should().ThrowAsync<ActionPlanValidationException>();
    }

    // ---- apoio ------------------------------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private DevicePriorityQuery Query(AegisScoreDbContext db) =>
        new(db, _clock, Options.Create(new CrossSourceCorrelationOptions()));

    private async Task<T> Servico<T>(Guid tenant, Func<IRemediationService, Task<T>> call)
    {
        await using var db = NewContext(tenant);
        return await call(new RemediationService(db, new SystemTenantContext(tenant), _clock, Query(db)));
    }

    private Task<ActionPlanView> CriarAsync(Guid assetId, string cve, string titulo = "Aplicar a atualização do fornecedor") =>
        Servico(TenantA, async s => (await s.CreateForDeviceCaseAsync(
            new CreateDeviceCaseActionPlanCommand(assetId, cve, titulo, "Aplicar a atualização indicada pela fonte.",
                "Equipe de Estações", "TI", DateOnly.FromDateTime(_base.UtcDateTime.AddDays(30))), Gestora))!);

    private async Task<ActionPlanView> ConcluirAsync(ActionPlanView plano)
    {
        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.RecordExecutionAsync(plano.Id,
            new RecordExecutionCommand(plano.Version, "Atualização aplicada.", null), Gestora)))!;
        _clock.Advance(TimeSpan.FromMinutes(5));
        plano = (await Servico(TenantA, s => s.ValidateAsync(plano.Id,
            new ValidateActionPlanCommand(plano.Version, null, "CHG-0002", null), Gestora)))!;
        _clock.Advance(TimeSpan.FromMinutes(5));
        return (await Servico(TenantA, s => s.UpdateAsync(plano.Id,
            new UpdateActionPlanCommand(plano.Version, null, null, null, null, null, ActionPlanStatus.Concluido), Gestora)))!;
    }

    private async Task<AssetDevicePriorityDto> DetalheAsync(Guid assetId)
    {
        await using var db = NewContext(TenantA);
        return (await Query(db).GetForAssetAsync(assetId, 1, 10))!;
    }

    private static string Roles(string action) =>
        typeof(RemediationController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>()?.Roles!;

    /// <summary>
    /// Dispositivo A (vinculado ao Intune não conforme e sem criptografia) com uma CVE crítica com exploit público (P1) e
    /// uma média sem exploit (P4 antecipada para P3); dispositivo B (só Defender) com a mesma CVE crítica.
    /// </summary>
    private async Task<(Guid Defender, Guid AssetA, Guid AssetB)> CenarioAsync()
    {
        var defender = Seed(ConnectorCapability.VulnerabilityScanner);
        var intune = Seed(ConnectorCapability.ConfigAnalyzer);
        DefenderData(Maquinas(),
            new[] { ("mde-a", CveCritica), ("mde-a", CveMedia), ("mde-b", CveCritica) },
            Cve(CveCritica, "Critical", 9.8, publicExploit: true), Cve(CveMedia, "Medium", 5.5));
        _src.IntuneDevices = SyntheticDeviceSources.Page(Device("int-a", DevX));
        await Sync(defender);
        await Sync(intune);
        return (defender, await AssetOf(defender, "mde-a"), await AssetOf(defender, "mde-b"));
    }

    private string[] Maquinas() => new[]
    {
        Machine("mde-a", "pc-a.demo.example.com", DevX),
        Machine("mde-b", "pc-b.demo.example.com", null),
    };

    private Guid Seed(ConnectorCapability capability)
    {
        using var db = NewContext(TenantA);
        var cfg = SyntheticDeviceSources.Connector(TenantA, capability, DirA);
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private Task Sync(Guid connectorId) => _src.SyncAsync(_options, TenantA, connectorId);

    private static string Q(string s) => SyntheticDeviceSources.Q(s);

    private string Machine(string id, string dns, string? deviceId) =>
        "{\"id\":" + Q(id) + ",\"osPlatform\":\"Windows11\",\"lastSeen\":" + Q(_base.AddHours(-1).ToString("O")) +
        ",\"computerDnsName\":" + Q(dns) + (deviceId is null ? "" : ",\"aadDeviceId\":" + Q(deviceId)) + "}";

    private string Device(string id, string deviceId) =>
        "{\"id\":" + Q(id) + ",\"complianceState\":\"noncompliant\",\"operatingSystem\":\"Windows\"" +
        ",\"lastSyncDateTime\":" + Q(_base.AddHours(-1).ToString("O")) + ",\"isEncrypted\":false,\"azureADDeviceId\":" + Q(deviceId) + "}";

    private static string Cve(string id, string severity, double cvss, bool? publicExploit = null)
    {
        var s = "{\"id\":" + Q(id) + ",\"name\":" + Q(id) + ",\"severity\":" + Q(severity) + ",\"cvssV3\":" +
            cvss.ToString(CultureInfo.InvariantCulture);
        if (publicExploit is { } pe) s += ",\"publicExploit\":" + (pe ? "true" : "false");
        return s + "}";
    }

    private void DefenderData(string[] machines, (string Machine, string Cve)[] relations, params string[] cves)
    {
        _src.DefenderMachines = SyntheticDeviceSources.Page(machines);
        _src.DefenderRelations = SyntheticDeviceSources.Page(relations.Select(r => SyntheticDeviceSources.Relation(r.Machine, r.Cve)).ToArray());
        _src.DefenderCves = SyntheticDeviceSources.Page(cves);
    }

    private async Task<Guid> AssetOf(Guid connectorId, string externalId)
    {
        await using var db = NewContext(TenantA);
        return (await db.AssetSourceBindings.AsNoTracking()
            .SingleAsync(b => b.ConnectorConfigId == connectorId && b.ExternalId == externalId)).AssetId;
    }

    private async Task DisposicaoAsync(Guid assetId, string cve, ExposureStatus status)
    {
        await using var db = NewContext(TenantA);
        var threat = await db.Threats.AsNoTracking().SingleAsync(t => t.Code == cve);
        var e = await db.AssetThreatExposures.SingleAsync(x => x.AssetId == assetId && x.ThreatId == threat.Id);
        e.Status = status;
        await db.SaveChangesAsync();
    }

    private static readonly JsonSerializerOptions Fotografia = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false,
    };

    /// <summary>
    /// Fotografia da situação TÉCNICA do tenant: exposições (com a disposição), observações da fonte, ativos (com a
    /// criticidade), vínculos e fontes, ledger de controles e sinais de evidência. Ordenada em memória, para não depender
    /// da ordenação de Guid do provedor.
    /// </summary>
    private async Task<string> EstadoTecnicoAsync()
    {
        await using var db = NewContext(TenantA);
        string Linhas<T>(IEnumerable<T> rows, Func<T, string> key) =>
            string.Join("\n", rows.Select(r => JsonSerializer.Serialize(r, Fotografia)).Zip(rows.Select(key))
                .OrderBy(x => x.Second, StringComparer.Ordinal).Select(x => x.First));

        var exposicoes = await db.AssetThreatExposures.AsNoTracking().ToListAsync();
        var observacoes = await db.AssetThreatObservations.AsNoTracking().ToListAsync();
        var ativos = await db.Assets.AsNoTracking().ToListAsync();
        var vinculos = await db.AssetSourceBindings.AsNoTracking().ToListAsync();
        var fontes = await db.Connectors.AsNoTracking().ToListAsync();
        var controles = await db.Set<TenantControlState>().AsNoTracking().ToListAsync();
        var sinais = await db.Set<EvidenceSignal>().AsNoTracking().ToListAsync();
        return string.Join("\n---\n",
            Linhas(exposicoes, x => x.Id.ToString()), Linhas(observacoes, x => x.Id.ToString()),
            Linhas(ativos, x => x.Id.ToString()), Linhas(vinculos, x => x.Id.ToString()),
            Linhas(fontes, x => x.Id.ToString()), Linhas(controles, x => x.Id.ToString()),
            Linhas(sinais, x => x.Id.ToString()));
    }

    /// <summary>Uma avaliação REAL do KNIGHT (Entra/Live) no tenant A — só para provar que ela não valida uma CVE.</summary>
    private async Task<Guid> AvaliacaoKnightAsync()
    {
        await using var db = NewContext(TenantA);
        var at = _base.AddHours(1);
        var run = new KnightAssessmentRun
        {
            TenantId = TenantA,
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
            CapabilitiesJson = JsonSerializer.Serialize(
                new[]
                {
                    new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                    new KnightCapabilityStatus(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected),
                },
                KnightCapabilitiesJson.Options),
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            TenantId = TenantA,
            IndicatorId = "AK-ENTRA-001",
            Title = "Contas privilegiadas sem MFA",
            Category = KnightIndicatorCategory.PrivilegedAccess,
            Severity = SeverityLevel.Critical,
            Status = KnightIndicatorStatus.Exposed,
            AffectedObjectCount = 2,
            Evidence = "2 conta(s) privilegiada(s) sem metodo de MFA registrado.",
            SourceType = KnightSourceType.MicrosoftEntraId,
            CollectedAt = at,
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }
}
