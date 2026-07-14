using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Testes da superfície de ingestão de telemetria — o fluxo ponta a ponta
/// <see cref="TelemetryIngestionService"/> → <see cref="AegisAiEvaluatorService"/> →
/// <see cref="ControlStateWriter"/>, sobre SQLite in-memory (banco relacional real: índice único,
/// Global Query Filter e stamping fail-closed de verdade).
///
/// Provam a REGRA DE OURO da arquitetura: a telemetria é a evidência autoritativa e pode levar um
/// controle a 100%, sobrescrevendo o teto documental de 50% — a precedência técnica que este webhook
/// finalmente destrava ao dar um chamador ao <c>EvaluateAsync</c>.
/// </summary>
public sealed class TelemetryIngestionServiceTests : IDisposable
{
    private const int MaxPoints = 20;              // par de propósito: 50% = 10 exato, sem arredondamento
    private const string SubCode = "PR.AA-01";

    private const int DataMaxPoints = 20;          // PR.DS (Data Security) — tier alto (cripto)
    private const string DataSubCode = "PR.DS-01";
    private const int PlatformMaxPoints = 15;      // PR.PS (Platform Security) — tier alto
    private const string PlatformSubCode = "PR.PS-01";
    private const int NetworkMaxPoints = 15;       // PR.IR (Infrastructure Resilience) — tier alto
    private const string NetworkSubCode = "PR.IR-01";

    private const int DetectMaxPoints = 15;        // peso de DE.CM (tier alto) no catálogo NIST CSF 2.0
    private const string DetectSubCode = "DE.CM-01";

    private const int ResilienceMaxPoints = 10;    // RS.MA / RS.MI / RC.RP (tier médio)
    private const string RespondAnalysisCode = "RS.MA-01";
    private const string RespondMitigationCode = "RS.MI-01";
    private const string RecoverExecutionCode = "RC.RP-01";

    private const int GovernScMaxPoints = 15;      // GV.SC (Supply Chain) — tier alto no catálogo NIST CSF 2.0
    private const string GovernScCode = "GV.SC-01";
    private const int GovernRrMaxPoints = 5;       // GV.RR (Roles) — tier de governança (peso 5)
    private const string GovernRrCode = "GV.RR-01";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public TelemetryIngestionServiceTests()
    {
        // Banco in-memory vivo enquanto a conexão estiver aberta; xUnit instancia a classe por caso de
        // teste, então cada teste recebe um banco limpo e isolado.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    // ---- A regra de ouro: telemetria > documento ------------------------------------

    [Fact]
    public async Task IngestAsync_TelemetriaCompliant_SobrescreveTetoDocumental_LedgerAtinge100()
    {
        // 1) O Govern já creditou o teto documental (50%) neste controle — um PDF de política vigente.
        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "documental: política vigente",
                VerdictSource.Documentary);

        // 2) Chega a telemetria: a ferramenta PROVA a implementação efetiva → Compliant (100%).
        await using (var db = NewContext(TenantA))
        {
            var ingestion = IngestionFor(db, TenantA, new FakeLlmClient(ControlStatus.Compliant, "MFA aplicado no host"));
            var verdict = await ingestion.IngestAsync(new TelemetrySignal(
                "Microsoft Defender", "MFA enforced", "High", SubCode, "{\"mfa\":true,\"result\":\"success\"}"));

            verdict.Status.Should().Be(ControlStatus.Compliant);
            verdict.AwardedScore.Should().Be(MaxPoints, "a telemetria é autoritativa e concede 100%");
            verdict.MaxScorePoints.Should().Be(MaxPoints);
        }

        // 3) O ledger reflete a PRECEDÊNCIA técnica: 100%, fonte Telemetry — o documento foi superado.
        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.Compliant);
        state.CurrentScore.Should().Be(MaxPoints, "telemetria sobrescreve o teto documental de 50% e vai a 100%");
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry, "a procedência técnica assume o estado");
        state.AiEvidence.Should().Contain("MFA aplicado no host");
    }

    [Fact]
    public async Task IngestAsync_ComStubLlmClientReal_PayloadDeBloqueio_ResultaCompliant()
    {
        // Prova o caminho de DEV (sem chave Gemini): o StubLlmClient reconhece 'blocked'/'success' como
        // evidência de controle efetivo. É exatamente o que o curl de demonstração dispara para bater 100%.
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestAsync(new TelemetrySignal(
            "Microsoft Defender", "Threat blocked", "High", SubCode, "{\"action\":\"blocked\",\"result\":\"success\"}"));

        verdict.Status.Should().Be(ControlStatus.Compliant);
        verdict.AwardedScore.Should().Be(MaxPoints);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.CurrentScore.Should().Be(MaxPoints);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    // ---- Segurança: fail-closed na borda de ingestão --------------------------------

    [Fact]
    public async Task IngestAsync_SemTenantNoContexto_LancaTenantSecurityException_ENaoPersiste()
    {
        // Contexto sem tenant (ex.: pipeline sem claim resolvida): a ingestão barra antes do motor.
        await using var db = NewContext(null);
        var ingestion = IngestionFor(db, null, new FakeLlmClient(ControlStatus.Compliant));

        var acao = () => ingestion.IngestAsync(new TelemetrySignal("EDR", "evt", "High", SubCode, "success"));

        await acao.Should().ThrowAsync<TenantSecurityException>();
        (await db.TenantControlStates.IgnoreQueryFilters().CountAsync())
            .Should().Be(0, "um sinal sem tenant resolvido jamais toca o ledger");
    }

    // ---- Protect (PR.AA): Tolerância Zero — privilégio sem MFA é falha crítica ------

    [Fact]
    public async Task IngestCategory_Protect_MfaPrivilegiadoAbaixoDe100_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // Administradores sem MFA integral (50%), mesmo com Conditional Access ativo: falha crítica.
        var verdict = await ingestion.IngestCategoryAsync(IdentitySignal(
            privilegedMfa: 50, standardMfa: 95, staleAccounts: 2, conditionalAccess: true));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "privilégio sem MFA integral é falha crítica (PR.AA)");
        verdict.AwardedScore.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant);
        state.CurrentScore.Should().Be(0);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task IngestCategory_Protect_ConditionalAccessDesabilitado_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // MFA privilegiado integral, mas Conditional Access desligado — a 2ª condição do OR reprova.
        var verdict = await ingestion.IngestCategoryAsync(IdentitySignal(
            privilegedMfa: 100, standardMfa: 100, staleAccounts: 0, conditionalAccess: false));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "sem Conditional Access o acesso privilegiado fica exposto");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_Protect_IdentidadeTotalmenteConforme_ClassificaCompliant_100()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(IdentitySignal(
            privilegedMfa: 100, standardMfa: 98, staleAccounts: 0, conditionalAccess: true));

        verdict.Status.Should().Be(ControlStatus.Compliant, "MFA privilegiado integral + Conditional Access = conformidade");
        verdict.AwardedScore.Should().Be(MaxPoints);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.CurrentScore.Should().Be(MaxPoints);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    // ---- Detect (DE.CM): Tolerância Zero — ativo crítico não monitorado é ponto cego --

    [Fact]
    public async Task IngestCategory_Detect_AtivoCriticoNaoMonitorado_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // Cobertura de logs ótima (99%), mas 2 ativos críticos fora do monitoramento — ponto cego reprova.
        var verdict = await ingestion.IngestCategoryAsync(MonitoringSignal(
            criticalLogCoverage: 99, unmonitoredCriticalAssets: 2, networkVisibility: 90));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "ativo crítico não monitorado é ponto cego inaceitável (DE.CM)");
        verdict.AwardedScore.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant);
        state.CurrentScore.Should().Be(0);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task IngestCategory_Detect_MonitoramentoIntegral_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(MonitoringSignal(
            criticalLogCoverage: 98, unmonitoredCriticalAssets: 0, networkVisibility: 95));

        verdict.Status.Should().Be(ControlStatus.Compliant, "logs críticos cobertos e zero ativos críticos fora do monitoramento");
        verdict.AwardedScore.Should().Be(DetectMaxPoints);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.CurrentScore.Should().Be(DetectMaxPoints);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    // ---- Respond (RS) & Recover (RC): resiliência a incidentes -----------------------

    [Fact]
    public async Task IngestCategory_RespondAnalysisMttaAlto_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // Reconhecimento em 45 min (>30), threat hunting ótimo — o MTTA reprova (RS.MA).
        var verdict = await ingestion.IngestCategoryAsync(AnalysisSignal(mttaMins: 45, threatHunting: 95));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "MTTA acima de 30 min é resposta lenta (RS.MA)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_RespondMitigationSemIsolamentoAutomatizado_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // MTTR ótimo (30 min), mas sem isolamento automatizado — reprova (RS.MI).
        var verdict = await ingestion.IngestCategoryAsync(MitigationSignal(automatedIsolation: false, mttrMins: 30));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "sem isolamento automatizado a contenção é lenta demais (RS.MI)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_RecoverBackupCorrompidoESemImutabilidade_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // O cenário de ransomware: backup mutável E corrompido — falha crítica de recuperação (RC.RP).
        var verdict = await ingestion.IngestCategoryAsync(ExecutionSignal(immutable: false, integrity: "Corrupted", rtoMet: true));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "backup sem imutabilidade e corrompido inviabiliza a recuperação (RC.RP)");
        verdict.AwardedScore.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant);
        state.CurrentScore.Should().Be(0);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task IngestCategory_RecoverResilienciaIntegra_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(ExecutionSignal(immutable: true, integrity: "Valid", rtoMet: true));

        verdict.Status.Should().Be(ControlStatus.Compliant, "backups imutáveis, íntegros (Valid) e RTO atendido");
        verdict.AwardedScore.Should().Be(ResilienceMaxPoints);
    }

    // ---- Govern (GV): telemetria estruturada — governança não se resume a ler PDFs ---

    [Fact]
    public async Task IngestCategory_GovernSupplyChain_FornecedorComAcessoSemAuditoria_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // 3 fornecedores de TI com acesso à rede, nenhum sob auditoria de terceiros — elo não verificado (GV.SC).
        var verdict = await ingestion.IngestCategoryAsync(SupplyChainSignal(
            suppliersWithNetworkAccess: 3, criticalSuppliers: 1, thirdPartyAudited: false));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "fornecedor com acesso à rede sem auditoria é elo fraco da cadeia (GV.SC)");
        verdict.AwardedScore.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry, "telemetria de governança também é evidência autoritativa");
    }

    [Fact]
    public async Task IngestCategory_GovernSupplyChain_FornecedoresAuditados_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(SupplyChainSignal(
            suppliersWithNetworkAccess: 3, criticalSuppliers: 1, thirdPartyAudited: true));

        verdict.Status.Should().Be(ControlStatus.Compliant, "fornecedores com acesso à rede sob auditoria de terceiros ativa");
        verdict.AwardedScore.Should().Be(GovernScMaxPoints, "GV.SC é tier alto (peso 15) e a telemetria pode levá-lo a 100%");
        verdict.MaxScorePoints.Should().Be(GovernScMaxPoints);
    }

    [Fact]
    public async Task IngestCategory_GovernRoles_AdminSemRevisaoPeriodica_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // Revisão de acesso configurada, mas 2 contas de admin fora do ciclo — reprova (GV.RR).
        var verdict = await ingestion.IngestCategoryAsync(RolesSignal(
            totalAdmins: 8, adminsWithoutReview: 2, reviewConfigured: true));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "conta de administrador sem revisão periódica é autoridade sem accountability (GV.RR)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_GovernRoles_AdminsSobRevisao_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(RolesSignal(
            totalAdmins: 8, adminsWithoutReview: 0, reviewConfigured: true));

        verdict.Status.Should().Be(ControlStatus.Compliant, "todas as contas de admin sob revisão periódica configurada");
        verdict.AwardedScore.Should().Be(GovernRrMaxPoints);
    }

    // ---- Entra ID: retrato de identidade MULTI-CONTROLE (PR.AA-01 + GV.RR-01) --------

    [Fact]
    public async Task IngestCategory_EntraIdentity_PrivilegiadosSemMfa_ReprovaProtectPrAa()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // Retrato de alto risco (Purple Knight/vicunha): 15 admins, 4 sem MFA — a dimensão MFA reprova PR.AA-01.
        var verdict = await ingestion.IngestCategoryAsync(EntraIdentitySignal(
            SubCode, totalPrivileged: 15, privilegedWithoutMfa: 4));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "conta privilegiada do Entra ID sem MFA é falha crítica (PR.AA)");
        verdict.AwardedScore.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task IngestCategory_EntraIdentity_ExcessoDeAdministradores_ReprovaGovernGvRr()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // O MESMO retrato, agora avaliado como GV.RR-01: 15 contas privilegiadas (>10) quebra o menor privilégio.
        var verdict = await ingestion.IngestCategoryAsync(EntraIdentitySignal(
            GovernRrCode, totalPrivileged: 15, privilegedWithoutMfa: 4));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "mais de 10 contas privilegiadas quebra a governança de identidade (GV.RR)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_EntraIdentity_MesmoRetrato_DiscriminaPorControle()
    {
        // Prova a ÂNCORA DE CÓDIGO do StubLlmClient: um retrato com MFA íntegro (0 sem MFA) mas com excesso
        // de admins (12) deve APROVAR PR.AA-01 e REPROVAR GV.RR-01 — a regra de um controle não decide o outro.
        await using (var db = NewContext(TenantA))
        {
            var ingestion = IngestionFor(db, TenantA, new StubLlmClient());
            var prAa = await ingestion.IngestCategoryAsync(EntraIdentitySignal(
                SubCode, totalPrivileged: 12, privilegedWithoutMfa: 0));
            prAa.Status.Should().Be(ControlStatus.Compliant, "0 privilegiados sem MFA → PR.AA conforme, mesmo com muitos admins");
            prAa.AwardedScore.Should().Be(MaxPoints);
        }

        await using (var db = NewContext(TenantA))
        {
            var ingestion = IngestionFor(db, TenantA, new StubLlmClient());
            var gvRr = await ingestion.IngestCategoryAsync(EntraIdentitySignal(
                GovernRrCode, totalPrivileged: 12, privilegedWithoutMfa: 0));
            gvRr.Status.Should().Be(ControlStatus.NonCompliant, "12 contas privilegiadas (>10) → GV.RR reprova, independente do MFA");
        }
    }

    [Fact]
    public async Task IngestCategory_EntraIdentity_ContasOtSemMfaMasIsoladas_ClassificaMitigated()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // O falso positivo industrial: contas fabris (urdideira/OT) sem MFA por legado, MAS isoladas na rede.
        // A IA do Aegis PONDERA o controle compensatório → mitigado (50%), não reprova cegamente como as
        // ferramentas de mercado. Mesmo retrato de alto risco do stub, agora com hasNetworkIsolation=true.
        var verdict = await ingestion.IngestCategoryAsync(EntraIdentitySignal(
            SubCode, totalPrivileged: 15, privilegedWithoutMfa: 4, hasNetworkIsolation: true));

        verdict.Status.Should().Be(ControlStatus.MitigatedByThirdParty,
            "contas de serviço/OT sem MFA isoladas na rede são risco compensado, não falha crítica");
        verdict.AwardedScore.Should().Be(MaxPoints / 2, "MitigatedByThirdParty concede 50% do peso do controle");

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.MitigatedByThirdParty);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    // ---- Protect (PR.DS/PR.PS/PR.IR): Data, Platform e Network — regras já existentes, agora COBERTAS ----
    // Exercitam os novos records tipados (Data/Platform/NetworkTelemetrySignal.ToMetricLines()) contra as
    // heurísticas de EvaluateProtect que já viviam no StubLlmClient — fechando a lacuna de cobertura.

    [Fact]
    public async Task IngestCategory_ProtectData_CriptografiaAbaixoDoMinimo_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(DataSignal(encryptionCoverage: 90, unencryptedTraffic: false));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "criptografia de endpoint abaixo de 95% expõe dados em repouso (PR.DS)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_ProtectData_CriptografiaAmplaESemTrafegoEmClaro_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(DataSignal(encryptionCoverage: 99, unencryptedTraffic: false));

        verdict.Status.Should().Be(ControlStatus.Compliant, "criptografia ampla e tráfego cifrado fim a fim (PR.DS)");
        verdict.AwardedScore.Should().Be(DataMaxPoints);
    }

    [Fact]
    public async Task IngestCategory_ProtectPlatform_PatchCriticoPendente_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        // Hardening CIS ótimo (95%), mas 1 patch crítico pendente — a 2ª condição do OR reprova.
        var verdict = await ingestion.IngestCategoryAsync(PlatformSignal(cisRate: 95, missingPatches: 1));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "patch crítico pendente é janela de exploração (PR.PS)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_ProtectPlatform_HardeningIntegro_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(PlatformSignal(cisRate: 88, missingPatches: 0));

        verdict.Status.Should().Be(ControlStatus.Compliant, "benchmark CIS satisfatório e sem patches críticos pendentes (PR.PS)");
        verdict.AwardedScore.Should().Be(PlatformMaxPoints);
    }

    [Fact]
    public async Task IngestCategory_ProtectNetwork_SemDefaultDeny_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(NetworkSignal(defaultDenyFirewall: false));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "firewall sem política default-deny deixa o perímetro permissivo (PR.IR)");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestCategory_ProtectNetwork_DefaultDenyAplicado_ClassificaCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA, new StubLlmClient());

        var verdict = await ingestion.IngestCategoryAsync(NetworkSignal(defaultDenyFirewall: true));

        verdict.Status.Should().Be(ControlStatus.Compliant, "firewall default-deny aplicado (PR.IR)");
        verdict.AwardedScore.Should().Be(NetworkMaxPoints);
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>Monta o sinal de PR.AA no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal IdentitySignal(
        double privilegedMfa, double standardMfa, int staleAccounts, bool conditionalAccess) =>
        new(SubCode, "Protect", "Identity", new[]
        {
            $"Privileged MFA Coverage: {privilegedMfa}%",
            $"Standard MFA Coverage: {standardMfa}%",
            $"Stale Accounts Active: {staleAccounts}",
            $"Conditional Access Enforced: {(conditionalAccess ? "true" : "false")}",
        });

    /// <summary>Monta o sinal de DE.CM no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal MonitoringSignal(
        double criticalLogCoverage, int unmonitoredCriticalAssets, double networkVisibility) =>
        new(DetectSubCode, "Detect", "Monitoring", new[]
        {
            $"Critical Log Source Coverage: {criticalLogCoverage}%",
            $"Unmonitored Critical Assets: {unmonitoredCriticalAssets}",
            $"Network Visibility Coverage: {networkVisibility}%",
        });

    /// <summary>Sinal de RS.MA no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal AnalysisSignal(int mttaMins, double threatHunting) =>
        new(RespondAnalysisCode, "Respond", "Analysis", new[]
        {
            $"Mean Time To Acknowledge: {mttaMins} min",
            $"Threat Hunting Coverage Rate: {threatHunting}%",
        });

    /// <summary>Sinal de RS.MI no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal MitigationSignal(bool automatedIsolation, int mttrMins) =>
        new(RespondMitigationCode, "Respond", "Mitigation", new[]
        {
            $"Automated Isolation Enabled: {(automatedIsolation ? "true" : "false")}",
            $"Mean Time To Respond: {mttrMins} min",
        });

    /// <summary>Sinal de RC.RP no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal ExecutionSignal(bool immutable, string integrity, bool rtoMet) =>
        new(RecoverExecutionCode, "Recover", "Execution", new[]
        {
            $"Immutable Backups Enabled: {(immutable ? "true" : "false")}",
            $"Backup Integrity Status: {integrity}",
            $"Recovery Time Objective Met: {(rtoMet ? "true" : "false")}",
        });

    /// <summary>Sinal de GV.SC no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal SupplyChainSignal(
        int suppliersWithNetworkAccess, int criticalSuppliers, bool thirdPartyAudited) =>
        new(GovernScCode, "Govern", "Supply Chain", new[]
        {
            $"Suppliers With Network Access: {suppliersWithNetworkAccess}",
            $"Critical Suppliers: {criticalSuppliers}",
            $"Third Party Audited: {(thirdPartyAudited ? "true" : "false")}",
        });

    /// <summary>Sinal de GV.RR no MESMO formato de métricas que o TelemetryController produz.</summary>
    private static CategoryTelemetrySignal RolesSignal(
        int totalAdmins, int adminsWithoutReview, bool reviewConfigured) =>
        new(GovernRrCode, "Govern", "Roles", new[]
        {
            $"Admin Accounts: {totalAdmins}",
            $"Admin Accounts Without Periodic Review: {adminsWithoutReview}",
            $"Privileged Access Review Configured: {(reviewConfigured ? "true" : "false")}",
        });

    /// <summary>
    /// Monta o sinal de categoria a partir do retrato de identidade do Entra ID — via
    /// <see cref="IdentityTelemetrySignal.ToMetricLines"/>, o MESMO contrato de rótulos que a ingestão real
    /// produzirá. O código-alvo varia (PR.AA-01 = dimensão MFA; GV.RR-01 = dimensão governança): um único
    /// retrato, dois controles. O pilar do cabeçalho segue o prefixo do código (não afeta a regra, que
    /// ancora no código do controle e nos rótulos).
    /// </summary>
    private static CategoryTelemetrySignal EntraIdentitySignal(
        string subcategoryCode, int totalPrivileged, int privilegedWithoutMfa,
        bool hasNetworkIsolation = false) =>
        new(subcategoryCode,
            subcategoryCode.StartsWith("GV") ? "Govern" : "Protect",
            "Identity",
            new IdentityTelemetrySignal(
                TotalPrivilegedAccounts: totalPrivileged,
                PrivilegedAccountsWithoutMfa: privilegedWithoutMfa,
                PrivilegedAccountsWithMailbox: 9,
                InactiveGuestAccountsOver30Days: 6,
                MfaExemptServiceAccounts: new[] { "svc-urdideira-ot@vicunha.com.br" },
                HasNetworkIsolation: hasNetworkIsolation).ToMetricLines());

    /// <summary>Sinal de PR.DS a partir do <see cref="DataTelemetrySignal"/> TIPADO (via ToMetricLines) — prova que os rótulos do record casam com a regra PR.DS-01 já existente no StubLlmClient.</summary>
    private static CategoryTelemetrySignal DataSignal(double encryptionCoverage, bool unencryptedTraffic) =>
        new(DataSubCode, "Protect", "Data",
            new DataTelemetrySignal(encryptionCoverage, unencryptedTraffic).ToMetricLines());

    /// <summary>Sinal de PR.PS a partir do <see cref="PlatformTelemetrySignal"/> tipado.</summary>
    private static CategoryTelemetrySignal PlatformSignal(double cisRate, int missingPatches) =>
        new(PlatformSubCode, "Protect", "Platform",
            new PlatformTelemetrySignal(cisRate, missingPatches).ToMetricLines());

    /// <summary>Sinal de PR.IR a partir do <see cref="NetworkTelemetrySignal"/> tipado.</summary>
    private static CategoryTelemetrySignal NetworkSignal(bool defaultDenyFirewall) =>
        new(NetworkSubCode, "Protect", "Network",
            new NetworkTelemetrySignal(defaultDenyFirewall).ToMetricLines());

    /// <summary>Monta a cadeia REAL de produção (ingestão → motor → writer) sob o tenant do contexto.</summary>
    private static ITelemetryIngestionService IngestionFor(AegisScoreDbContext db, Guid? tenantId, ILLMClient llm)
    {
        var ctx = new SystemTenantContext(tenantId);
        var writer = new ControlStateWriter(db, ctx, NullLogger<ControlStateWriter>.Instance);
        var evaluator = new AegisAiEvaluatorService(db, llm, ctx, writer);
        return new TelemetryIngestionService(evaluator, ctx);
    }

    private static IControlStateWriter WriterFor(AegisScoreDbContext db) =>
        new ControlStateWriter(db, new SystemTenantContext(TenantA), NullLogger<ControlStateWriter>.Instance);

    /// <summary>Catálogo mínimo: o grafo exigido pelas FKs até uma subcategoria com peso conhecido.</summary>
    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var fn = new NistFunction { Code = "PR", Name = "PROTECT" };
        var cat = new NistCategory { Code = "PR.AA", Name = "Identity" };
        cat.Subcategories.Add(new NistSubcategory
        {
            Code = SubCode,
            Description = "Identities and credentials are managed.",
            MaxScorePoints = MaxPoints,
        });
        fn.Categories.Add(cat);

        // Demais categorias do PROTECT — PR.DS (peso 20), PR.PS/PR.IR (peso 15) — alvos dos testes de Data/Platform/Network.
        var prDs = new NistCategory { Code = "PR.DS", Name = "Data Security" };
        prDs.Subcategories.Add(new NistSubcategory { Code = DataSubCode, Description = "Data-at-rest and data-in-transit are protected.", MaxScorePoints = DataMaxPoints });
        var prPs = new NistCategory { Code = "PR.PS", Name = "Platform Security" };
        prPs.Subcategories.Add(new NistSubcategory { Code = PlatformSubCode, Description = "Hardware and software platforms are managed and hardened.", MaxScorePoints = PlatformMaxPoints });
        var prIr = new NistCategory { Code = "PR.IR", Name = "Technology Infrastructure Resilience" };
        prIr.Subcategories.Add(new NistSubcategory { Code = NetworkSubCode, Description = "Technology infrastructure resilience is protected.", MaxScorePoints = NetworkMaxPoints });
        fn.Categories.Add(prDs);
        fn.Categories.Add(prPs);
        fn.Categories.Add(prIr);
        fv.Functions.Add(fn);

        // Segunda função: DETECT / DE.CM-01 (peso 15) — alvo dos testes de telemetria de Detect.
        var deFn = new NistFunction { Code = "DE", Name = "DETECT" };
        var deCat = new NistCategory { Code = "DE.CM", Name = "Continuous Monitoring" };
        deCat.Subcategories.Add(new NistSubcategory
        {
            Code = DetectSubCode,
            Description = "Networks and network services are monitored to find potentially adverse events.",
            MaxScorePoints = DetectMaxPoints,
        });
        deFn.Categories.Add(deCat);
        fv.Functions.Add(deFn);

        // Terceira função: RESPOND / RS.MA-01 e RS.MI-01 (peso 10 cada) — alvos dos testes de Respond.
        var rsFn = new NistFunction { Code = "RS", Name = "RESPOND" };
        var rsMa = new NistCategory { Code = "RS.MA", Name = "Incident Analysis" };
        rsMa.Subcategories.Add(new NistSubcategory { Code = RespondAnalysisCode, Description = "Incidents are analyzed.", MaxScorePoints = ResilienceMaxPoints });
        var rsMi = new NistCategory { Code = "RS.MI", Name = "Incident Mitigation" };
        rsMi.Subcategories.Add(new NistSubcategory { Code = RespondMitigationCode, Description = "Incidents are contained and mitigated.", MaxScorePoints = ResilienceMaxPoints });
        rsFn.Categories.Add(rsMa);
        rsFn.Categories.Add(rsMi);
        fv.Functions.Add(rsFn);

        // Quarta função: RECOVER / RC.RP-01 (peso 10) — alvo do teste de Recover.
        var rcFn = new NistFunction { Code = "RC", Name = "RECOVER" };
        var rcRp = new NistCategory { Code = "RC.RP", Name = "Incident Recovery Plan Execution" };
        rcRp.Subcategories.Add(new NistSubcategory { Code = RecoverExecutionCode, Description = "The recovery plan is executed.", MaxScorePoints = ResilienceMaxPoints });
        rcFn.Categories.Add(rcRp);
        fv.Functions.Add(rcFn);

        // Quinta função: GOVERN / GV.SC-01 (peso 15) e GV.RR-01 (peso 5) — alvos dos testes de telemetria de Govern.
        var gvFn = new NistFunction { Code = "GV", Name = "GOVERN" };
        var gvSc = new NistCategory { Code = "GV.SC", Name = "Cybersecurity Supply Chain Risk Management" };
        gvSc.Subcategories.Add(new NistSubcategory { Code = GovernScCode, Description = "A cybersecurity supply chain risk management program is established.", MaxScorePoints = GovernScMaxPoints });
        var gvRr = new NistCategory { Code = "GV.RR", Name = "Roles, Responsibilities, and Authorities" };
        gvRr.Subcategories.Add(new NistSubcategory { Code = GovernRrCode, Description = "Roles, responsibilities, and authorities are established.", MaxScorePoints = GovernRrMaxPoints });
        gvFn.Categories.Add(gvSc);
        gvFn.Categories.Add(gvRr);
        fv.Functions.Add(gvFn);

        ctx.FrameworkVersions.Add(fv);   // catálogo é dado de referência: não é ITenantOwned, não é carimbado
        ctx.SaveChanges();
    }

    /// <summary>ILLMClient determinístico: devolve o veredito JSON do status pedido, sem heurística nem rede.</summary>
    private sealed class FakeLlmClient : ILLMClient
    {
        private readonly string _json;
        public FakeLlmClient(ControlStatus status, string evidence = "evidência de telemetria (fake)") =>
            _json = $"{{\"status\":\"{status}\",\"aiEvidence\":\"{evidence}\"}}";

        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(_json);
    }
}
