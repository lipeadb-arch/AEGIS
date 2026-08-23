using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// [AEGIS-AUD-021/027/032] Testes da projeção ÚNICA do workspace (<see cref="WorkspacePostureQuery"/>) sobre
/// SQLite in-memory. Provam: fail-closed sem tenant, NotEvaluated ≠ 0%, consistência score/cobertura/contagens,
/// paridade Função↔total, exclusão de framework inativo, isolamento por tenant e saúde de conectores (nunca
/// sincronizado ≠ saudável).
/// </summary>
public sealed class WorkspacePostureQueryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string PrAa = "PR.AA-01";   // peso 20 (Função PR)
    private const string PrDs = "PR.DS-01";   // peso 20 (Função PR)
    private const string DeCm = "DE.CM-01";   // peso 15 (Função DE)
    private const int EligibleMax = 55;
    private const int EligibleCount = 3;

    private readonly SqliteConnection _connection;

    public WorkspacePostureQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
        // ConnectorConfig tem FK para Tenant — os estados não, mas os conectores exigem a linha do tenant.
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SemTenantAmbiente_ProjecaoVazia_FailClosed()
    {
        await using var db = NewContext(null);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(null)).GetAsync();

        w.Overall.EvaluationState.Should().Be(nameof(ScoreEvaluationState.NotEvaluated));
        w.Overall.Percentage.Should().BeNull();
        w.Overall.EligibleControls.Should().Be(0, "sem tenant, nada é projetado — nem o catálogo");
        w.Functions.Should().BeEmpty();
        w.Connectors.Configured.Should().Be(0);
    }

    [Fact]
    public async Task SemAvaliacoes_TudoNotEvaluated_NaoZero_ComElegivelDoCatalogo()
    {
        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Overall.Percentage.Should().BeNull("nada avaliado NÃO é 0% — é ausência de score");
        w.Overall.EvaluationState.Should().Be(nameof(ScoreEvaluationState.NotEvaluated));
        w.Overall.EligibleControls.Should().Be(EligibleCount, "o universo elegível vem do catálogo ativo");
        w.Overall.EligibleMaxScore.Should().Be(EligibleMax);
        w.Overall.EvaluatedControls.Should().Be(0);
        w.Overall.CoveragePercentage.Should().Be(0);
        w.Overall.NotEvaluatedControls.Should().Be(EligibleCount);
        w.Functions.Should().HaveCount(2, "PR e DE");
        w.Functions.Should().OnlyContain(f => f.Percentage == null && f.EvaluatedControls == 0);
    }

    [Fact]
    public async Task ScoreCoberturaContagens_Consistentes_EAvaliadoNaoExcedeElegivel()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);            // 20/20
        await SeedStateAsync(DeCm, ControlStatus.NonCompliant, 0);          // 0/15 (avaliado, 0% real)

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        var o = w.Overall;
        o.EvaluatedControls.Should().Be(2);
        o.CompliantControls.Should().Be(1);
        o.NonCompliantControls.Should().Be(1);
        o.MitigatedControls.Should().Be(0);
        o.NotEvaluatedControls.Should().Be(1, "PR.DS-01 ficou sem avaliação");
        o.AchievedScore.Should().Be(20);
        o.EvaluatedMaxScore.Should().Be(35);
        o.Percentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(20.0 / 35 * 100));
        o.CoveragePercentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(35.0 / EligibleMax * 100));
        o.EvaluatedControls.Should().BeLessThanOrEqualTo(o.EligibleControls);
        o.EvaluatedMaxScore.Should().BeLessThanOrEqualTo(o.EligibleMaxScore);
        o.CoveragePercentage.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task SomaDasFuncoes_BateComOTotal()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedStateAsync(PrDs, ControlStatus.MitigatedByThirdParty, 10);
        await SeedStateAsync(DeCm, ControlStatus.NonCompliant, 0);

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Functions.Sum(f => f.EligibleControls).Should().Be(w.Overall.EligibleControls);
        w.Functions.Sum(f => f.EvaluatedControls).Should().Be(w.Overall.EvaluatedControls);
        w.Functions.Sum(f => f.CompliantControls).Should().Be(w.Overall.CompliantControls);
        w.Functions.Sum(f => f.NonCompliantControls).Should().Be(w.Overall.NonCompliantControls);
        w.Functions.Sum(f => f.MitigatedControls).Should().Be(w.Overall.MitigatedControls);
        w.Functions.Sum(f => f.NotEvaluatedControls).Should().Be(w.Overall.NotEvaluatedControls);

        var pr = w.Functions.Single(f => f.Code == "PR");
        pr.EvaluatedControls.Should().Be(2);
        pr.CompliantControls.Should().Be(1);
        pr.MitigatedControls.Should().Be(1);
    }

    [Fact]
    public async Task EstadoDeFrameworkInativo_NaoEntraNoScoreNemInflaCobertura()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedInactiveFrameworkStateAsync("OLD.XX-01", weight: 40, ControlStatus.Compliant, 40);

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Overall.EvaluatedControls.Should().Be(1, "só o estado do framework ATIVO conta");
        w.Overall.AchievedScore.Should().Be(20);
        w.Overall.EligibleControls.Should().Be(EligibleCount);
        w.Overall.CoveragePercentage.Should().BeLessThanOrEqualTo(100);
        w.Functions.Should().NotContain(f => f.Code == "OLD");
    }

    [Fact]
    public async Task IsolamentoPorTenant_BNaoVeAPosturaNemConectoresDeA()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedConnectorAsync(TenantA, ConnectorCapability.Siem, ConnectorStatus.Healthy, DateTimeOffset.UtcNow);

        await using var db = NewContext(TenantB);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantB)).GetAsync();

        w.Overall.EvaluatedControls.Should().Be(0, "o tenant B não vê os estados de A");
        w.Overall.Percentage.Should().BeNull();
        w.Connectors.Configured.Should().Be(0, "o tenant B não vê os conectores de A");
    }

    [Fact]
    public async Task Conectores_SaudeOperacional_Honesta_EntreHabilitados()
    {
        // (1) HABILITADO com LastStatus=Healthy MAS nunca sincronizou → NeverSynced, jamais saudável.
        await SeedConnectorAsync(TenantA, ConnectorCapability.Siem, ConnectorStatus.Healthy, lastSyncAt: null, enabled: true);
        // Habilitado e sincronizado com Healthy → saudável.
        await SeedConnectorAsync(TenantA, ConnectorCapability.Edr, ConnectorStatus.Healthy, DateTimeOffset.UtcNow, enabled: true);
        // (2) DESABILITADO com status Failed → NÃO degrada a saúde operacional (fora do denominador).
        await SeedConnectorAsync(TenantA, ConnectorCapability.Cmdb, ConnectorStatus.Failed, DateTimeOffset.UtcNow, enabled: false);

        await using var db = NewContext(TenantA);
        var c = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).Connectors;

        // (3) contagens configurado/habilitado/desabilitado.
        c.Configured.Should().Be(3);
        c.Enabled.Should().Be(2);
        c.Disabled.Should().Be(1);
        c.Healthy.Should().Be(1, "só o habilitado que sincronizou com Healthy");
        c.NeverSynced.Should().Be(1, "o Healthy-sem-sync é NeverSynced, nunca saudável");
        c.Failed.Should().Be(0, "o conector com falha está DESABILITADO — fora do denominador operacional");
        (c.Healthy + c.Degraded + c.Failed + c.NeverSynced).Should().Be(c.Enabled, "as 4 categorias particionam os habilitados");
        c.LastSyncAt.Should().NotBeNull("ao menos um habilitado sincronizou");
        c.Items.Should().Contain(i => !i.Enabled && i.Status == nameof(ConnectorStatus.Failed));
    }

    [Fact]
    public async Task Conectores_ListaVazia_ResumoVazio_SemExcecao()
    {
        await using var db = NewContext(TenantA);
        var c = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).Connectors;

        c.Configured.Should().Be(0);
        c.Enabled.Should().Be(0);
        c.Disabled.Should().Be(0);
        c.Healthy.Should().Be(0);
        c.LastSyncAt.Should().BeNull("(4) resumo vazio sem exceção");
        c.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Severidades_DerivadasDoStatus_ContamOsAvaliados()
    {
        await SeedStateAsync(PrAa, ControlStatus.NonCompliant, 0);          // Critical
        await SeedStateAsync(PrDs, ControlStatus.MitigatedByThirdParty, 10); // Medium
        await SeedStateAsync(DeCm, ControlStatus.Compliant, 15);           // Low

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        int SevOf(string s) => w.Overall.Severities.Single(x => x.Severity == s).Count;
        SevOf(nameof(SeverityLevel.Critical)).Should().Be(1);
        SevOf(nameof(SeverityLevel.Medium)).Should().Be(1);
        SevOf(nameof(SeverityLevel.Low)).Should().Be(1);
    }

    // ---- [AEGIS-MVP-ENV-01] cobertura por NATUREZA da evidência ---------------------
    // A autoridade de classificação é EXCLUSIVAMENTE AegisAssessmentRule.EvidenceType; a AUSÊNCIA de regra é
    // um estado próprio (NotAutomated). Os testes usam regras TIPADAS mínimas no fixture — NUNCA congelam a
    // distribuição real do catálogo (58/41/0) como constante de domínio.

    [Fact]
    public async Task Classificacao_PorEvidenceType_Telemetry_Documentation_Both_EAvaliadoNoBucketCerto()
    {
        // Uma regra tipada por natureza. Pesos: Telemetry 20 · Documentation 20 · Both 15.
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);
        await SeedRuleAsync(PrDs, RuleEvidenceType.Documentation);
        await SeedRuleAsync(DeCm, RuleEvidenceType.Both);
        // Avalia SÓ o de telemetria: se Telemetry/Documentation estivessem trocados, o avaliado cairia no bucket errado.
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;

        ec.Telemetry.EligibleControls.Should().Be(1);
        ec.Telemetry.EligibleMaxScore.Should().Be(20);
        ec.Telemetry.EvaluatedControls.Should().Be(1, "PR.AA-01 (telemetria) foi avaliado");
        ec.Telemetry.EvaluatedMaxScore.Should().Be(20);
        ec.Telemetry.CoveragePercentage.Should().Be(100);

        ec.Documentation.EligibleControls.Should().Be(1);
        ec.Documentation.EligibleMaxScore.Should().Be(20);
        ec.Documentation.EvaluatedControls.Should().Be(0, "PR.DS-01 (documental) não foi avaliado");
        ec.Documentation.CoveragePercentage.Should().Be(0);

        ec.Both.EligibleControls.Should().Be(1);
        ec.Both.EligibleMaxScore.Should().Be(15);
        ec.Both.EvaluatedControls.Should().Be(0);

        ec.NotAutomated.EligibleControls.Should().Be(0, "todas as três subcategorias têm regra");
    }

    [Fact]
    public async Task SubcategoriaSemRegra_ClassificadaComoNotAutomated_NaoComoDocumentacao()
    {
        // Só PR.AA-01 tem regra; PR.DS-01 e DE.CM-01 ficam SEM regra → NotAutomated (nunca documentação por default).
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;

        ec.NotAutomated.EligibleControls.Should().Be(2, "PR.DS-01 e DE.CM-01 não têm regra");
        ec.NotAutomated.EligibleMaxScore.Should().Be(35, "20 (PR.DS-01) + 15 (DE.CM-01)");
        ec.NotAutomated.EvaluatedControls.Should().Be(0);
        ec.Documentation.EligibleControls.Should().Be(0, "ausência de regra NÃO é documentação");
        ec.Telemetry.EligibleControls.Should().Be(1);
    }

    [Fact]
    public async Task QuatroBuckets_Exclusivos_ReconciliamExatamenteComOverall()
    {
        // Uma 4ª subcategoria SEM regra dá conteúdo ao bucket NotAutomated (peso 10, sob DE.CM).
        await SeedActiveSubcategoryAsync("DE.CM", "DE.CM-02", 10);
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);      // 20
        await SeedRuleAsync(PrDs, RuleEvidenceType.Documentation);  // 20
        await SeedRuleAsync(DeCm, RuleEvidenceType.Both);           // 15
        // DE.CM-02 (10) fica sem regra → NotAutomated.
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);    // avalia 1 de telemetria
        await SeedStateAsync(DeCm, ControlStatus.NonCompliant, 0);  // avalia 1 híbrido (avaliado ≠ conforme)

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();
        var ec = w.EvidenceCoverage;
        var buckets = new[] { ec.Telemetry, ec.Documentation, ec.Both, ec.NotAutomated };

        // Exclusividade/partição: as somas dos quatro buckets batem EXATAMENTE com o total — se qualquer
        // subcategoria fosse contada em dois buckets, a soma excederia o Overall.
        buckets.Sum(b => b.EligibleControls).Should().Be(w.Overall.EligibleControls).And.Be(4);
        buckets.Sum(b => b.EligibleMaxScore).Should().Be(w.Overall.EligibleMaxScore).And.Be(65);
        buckets.Sum(b => b.EvaluatedControls).Should().Be(w.Overall.EvaluatedControls).And.Be(2);
        buckets.Sum(b => b.EvaluatedMaxScore).Should().Be(w.Overall.EvaluatedMaxScore).And.Be(35);

        ec.NotAutomated.EligibleControls.Should().Be(1);
        ec.NotAutomated.EligibleMaxScore.Should().Be(10);
    }

    [Fact]
    public async Task CoberturaPonderada_UsaMaxScorePoints_NaoMediaSimplesDeQuantidade()
    {
        // Dois controles de PESOS diferentes no MESMO bucket; avalia só o mais pesado.
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);  // 20
        await SeedRuleAsync(DeCm, RuleEvidenceType.Telemetry);  // 15
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;

        ec.Telemetry.EligibleControls.Should().Be(2);
        ec.Telemetry.EligibleMaxScore.Should().Be(35);
        ec.Telemetry.EvaluatedControls.Should().Be(1);
        ec.Telemetry.EvaluatedMaxScore.Should().Be(20);
        // Ponderada por peso: 20/35 = 57,1% — NÃO a média simples de quantidade (1/2 = 50%).
        ec.Telemetry.CoveragePercentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(20.0 / 35 * 100));
        ec.Telemetry.CoveragePercentage.Should().NotBe(50, "cobertura é ponderada por MaxScorePoints, não por contagem");
    }

    [Fact]
    public async Task SemTenant_BucketsDeEvidencia_VaziosZerados()
    {
        await using var db = NewContext(null);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(null)).GetAsync()).EvidenceCoverage;

        foreach (var b in new[] { ec.Telemetry, ec.Documentation, ec.Both, ec.NotAutomated })
        {
            b.EligibleControls.Should().Be(0);
            b.EvaluatedControls.Should().Be(0);
            b.EligibleMaxScore.Should().Be(0);
            b.EvaluatedMaxScore.Should().Be(0);
            b.CoveragePercentage.Should().Be(0);
        }
    }

    [Fact]
    public async Task FrameworkInativo_E_SuasRegras_NaoEntramEmNenhumBucket()
    {
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);
        // Subcategoria + estado + regra de um framework INATIVO: nada disso pode inflar cobertura por evidência.
        await SeedInactiveFrameworkStateAsync("OLD.XX-01", weight: 40, ControlStatus.Compliant, 40);
        await SeedRuleAsync("OLD.XX-01", RuleEvidenceType.Documentation);

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;
        var buckets = new[] { ec.Telemetry, ec.Documentation, ec.Both, ec.NotAutomated };

        buckets.Sum(b => b.EligibleControls).Should().Be(EligibleCount, "só o catálogo ATIVO entra");
        buckets.Sum(b => b.EligibleMaxScore).Should().Be(EligibleMax);
        ec.Documentation.EligibleControls.Should().Be(0, "a regra do framework inativo é ignorada");
    }

    [Fact]
    public async Task IsolamentoPorTenant_CoberturaAvaliada_NaoVazaDeAparaB()
    {
        // Catálogo e regras são GLOBAIS; o AVALIADO é tenant-scoped. B vê o elegível, nunca a avaliação de A.
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);

        await using var db = NewContext(TenantB);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantB)).GetAsync()).EvidenceCoverage;

        ec.Telemetry.EligibleControls.Should().Be(1, "o elegível vem do catálogo global");
        ec.Telemetry.EvaluatedControls.Should().Be(0, "o tenant B não vê a avaliação de A");
        ec.Telemetry.EvaluatedMaxScore.Should().Be(0);
        ec.Telemetry.CoveragePercentage.Should().Be(0);
    }

    [Fact]
    public async Task BucketComPesoElegivelZero_NaoDividePorZero_CoberturaZero()
    {
        // Subcategoria de peso 0 com regra documental, e avaliada: peso elegível do bucket = 0 → cobertura 0 (guard).
        await SeedActiveSubcategoryAsync("DE.CM", "DE.CM-00", 0);
        await SeedRuleAsync("DE.CM-00", RuleEvidenceType.Documentation);
        await SeedStateAsync("DE.CM-00", ControlStatus.Compliant, 0);

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;

        ec.Documentation.EligibleControls.Should().Be(1);
        ec.Documentation.EligibleMaxScore.Should().Be(0);
        ec.Documentation.EvaluatedControls.Should().Be(1, "avaliado, ainda que sem peso");
        ec.Documentation.CoveragePercentage.Should().Be(0, "peso elegível 0 → cobertura 0, nunca divisão por zero");
        ec.Both.CoveragePercentage.Should().Be(0, "bucket vazio também é 0 sem exceção");
    }

    [Fact]
    public async Task Classificacao_NaoInferePorTextoDeEvidenceRequirements_SoPorEvidenceType()
    {
        // Texto INVERTIDO em relação ao tipo: se houvesse inferência textual, cairiam nos buckets trocados.
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry, new List<string> { "MANUAL_AUDIT_REQUIRED" });
        await SeedRuleAsync(PrDs, RuleEvidenceType.Documentation, new List<string> { "Ferramenta: Microsoft Defender" });

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;

        ec.Telemetry.EligibleControls.Should().Be(1, "EvidenceType=Telemetry manda, apesar do texto MANUAL_AUDIT_REQUIRED");
        ec.Telemetry.EligibleMaxScore.Should().Be(20);
        ec.Documentation.EligibleControls.Should().Be(1, "EvidenceType=Documentation manda, apesar do texto de ferramenta");
        ec.Documentation.EligibleMaxScore.Should().Be(20);
    }

    [Fact]
    public async Task EvidenceTypeInvalido_FalhaAcionavel_SemFallbackSilencioso()
    {
        await SeedRuleAsync(PrAa, (RuleEvidenceType)99);

        await using var db = NewContext(TenantA);
        var act = async () => await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*EvidenceType inválido*");
    }

    [Fact]
    public async Task DuplicidadeDeRegra_MesmaSubcategoria_TiposDivergentes_Falha()
    {
        // Duas regras para o MESMO SubcategoryId com tipos DIVERGENTES → falha (fail-closed).
        await SeedDuplicateRulesAsync(PrAa, RuleEvidenceType.Telemetry, RuleEvidenceType.Documentation);

        await using var db = NewContext(TenantA);
        var act = async () => await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Duplicidade de regra*");
    }

    [Fact]
    public async Task DuplicidadeDeRegra_MesmaSubcategoria_MesmoTipo_TambemFalha()
    {
        // Segunda regra para o MESMO SubcategoryId com tipo IGUAL também falha — nada de sobrescrever em silêncio.
        await SeedDuplicateRulesAsync(PrAa, RuleEvidenceType.Telemetry, RuleEvidenceType.Telemetry);

        await using var db = NewContext(TenantA);
        var act = async () => await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Duplicidade de regra*");
    }

    [Fact]
    public async Task RegrasDistintas_EmSubcategoriasDiferentes_PermanecemValidas()
    {
        // Regras em subcategorias DIFERENTES não são duplicidade — a classificação segue normalmente.
        await SeedRuleAsync(PrAa, RuleEvidenceType.Telemetry);
        await SeedRuleAsync(PrDs, RuleEvidenceType.Documentation);

        await using var db = NewContext(TenantA);
        var ec = (await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync()).EvidenceCoverage;

        ec.Telemetry.EligibleControls.Should().Be(1);
        ec.Documentation.EligibleControls.Should().Be(1);
        ec.Both.EligibleControls.Should().Be(0);
        ec.NotAutomated.EligibleControls.Should().Be(1, "DE.CM-01 ficou sem regra");
    }

    // ---- fixture --------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private async Task SeedStateAsync(string subCode, ControlStatus status, int score)
    {
        await using var db = NewContext(TenantA);
        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = status, CurrentScore = score, LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Regra tipada GLOBAL (reference data) para uma subcategoria — a AUTORIDADE de classificação de cobertura.</summary>
    private async Task SeedRuleAsync(string subCode, RuleEvidenceType evidenceType, List<string>? evidenceRequirements = null)
    {
        await using var db = NewContext(TenantA);
        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.AssessmentRules.Add(new AegisAssessmentRule
        {
            SubcategoryId = subId,
            SubcategoryCode = subCode,
            EvidenceType = evidenceType,
            EvidenceRequirements = evidenceRequirements ?? new List<string>(),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Duas regras para o MESMO SubcategoryId (códigos distintos driblam o índice único em SubcategoryCode).</summary>
    private async Task SeedDuplicateRulesAsync(string subCode, RuleEvidenceType first, RuleEvidenceType second)
    {
        await using var db = NewContext(TenantA);
        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.AssessmentRules.Add(new AegisAssessmentRule { SubcategoryId = subId, SubcategoryCode = subCode, EvidenceType = first });
        db.AssessmentRules.Add(new AegisAssessmentRule { SubcategoryId = subId, SubcategoryCode = subCode + "-DUP", EvidenceType = second });
        await db.SaveChangesAsync();
    }

    /// <summary>Adiciona uma subcategoria ao catálogo ATIVO (sob uma categoria existente), para compor os buckets.</summary>
    private async Task SeedActiveSubcategoryAsync(string categoryCode, string subCode, int weight)
    {
        await using var db = NewContext(TenantA);
        var catId = await db.Categories.Where(c => c.Code == categoryCode).Select(c => c.Id).SingleAsync();
        db.Subcategories.Add(new NistSubcategory { CategoryId = catId, Code = subCode, Description = "x", MaxScorePoints = weight });
        await db.SaveChangesAsync();
    }

    private async Task SeedConnectorAsync(
        Guid tenantId, ConnectorCapability capability, ConnectorStatus status, DateTimeOffset? lastSyncAt, bool enabled = true)
    {
        await using var db = NewContext(tenantId);
        db.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Generic, Capability = capability,
            DisplayName = $"Generic {capability} {Guid.NewGuid():N}", AuthType = ConnectorAuthType.ApiKey, Enabled = enabled,
            LastStatus = status, LastSyncAt = lastSyncAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedInactiveFrameworkStateAsync(string subCode, int weight, ControlStatus status, int score)
    {
        await using var db = NewContext(TenantA);
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0 (legado)", IsActive = false };
        var fn = new NistFunction { Code = "OLD", Name = "Legacy", Order = 99 };
        var cat = new NistCategory { Code = "OLD.XX", Name = "Legacy" };
        cat.Subcategories.Add(new NistSubcategory { Code = subCode, Description = "x", MaxScorePoints = weight });
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);
        db.FrameworkVersions.Add(fv);
        await db.SaveChangesAsync();

        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = status, CurrentScore = score, LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { Code = "PR", Name = "PROTECT", Order = 2 };
        var prAa = new NistCategory { Code = "PR.AA", Name = "Identity" };
        prAa.Subcategories.Add(new NistSubcategory { Code = PrAa, Description = "x", MaxScorePoints = 20 });
        var prDs = new NistCategory { Code = "PR.DS", Name = "Data" };
        prDs.Subcategories.Add(new NistSubcategory { Code = PrDs, Description = "x", MaxScorePoints = 20 });
        pr.Categories.Add(prAa);
        pr.Categories.Add(prDs);

        var de = new NistFunction { Code = "DE", Name = "DETECT", Order = 3 };
        var deCm = new NistCategory { Code = "DE.CM", Name = "Monitoring" };
        deCm.Subcategories.Add(new NistSubcategory { Code = DeCm, Description = "x", MaxScorePoints = 15 });
        de.Categories.Add(deCm);

        fv.Functions.Add(pr);
        fv.Functions.Add(de);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();
    }
}
