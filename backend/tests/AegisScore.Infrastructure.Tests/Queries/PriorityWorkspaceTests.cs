using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Queries;

/// <summary>
/// [AEGIS-MVP-PRIORITIES-01] O read model da Central de Prioridades é PURA COMPOSIÇÃO das três autoridades já
/// existentes. Estes testes fixam o CONTRATO da composição — sem replicar os cenários já cobertos pelas queries
/// de exposições/vulnerabilidades: (1) reúne as três dimensões; (2) envia às filas exatamente estado aberto +
/// página 1 + teto 5, sem filtros extras; (3) preserva itens e resumos VERBATIM (mesma referência — nada é
/// recalculado); (4) cenário vazio/nunca-coletado; (5) propaga o CancellationToken; (6) usa o TimeProvider como
/// relógio; (7) NÃO existe score agregado no contrato; (8) o endpoint segue o padrão de autorização e não
/// aceita tenant externo. As três queries são substituídas por fakes que gravam o que receberam e devolvem DTOs
/// canônicos — isola a COMPOSIÇÃO da persistência (a composição não toca DbContext nem tenant).
/// </summary>
public sealed class PriorityWorkspaceTests
{
    // ---- Fakes que gravam o filtro/token recebido e devolvem DTOs canônicos ----------------------------

    private sealed class FakePostureQuery : IWorkspacePostureQuery
    {
        public WorkspacePostureDto Result = null!;
        public int Calls;
        public CancellationToken Token;

        public Task<WorkspacePostureDto> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            Token = ct;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeExposureQuery : IPostureExposureQuery
    {
        public PostureExposureListDto Result = null!;
        public PostureExposureFilter? Filter;
        public CancellationToken Token;

        public Task<PostureExposureListDto> GetAsync(PostureExposureFilter filter, CancellationToken ct = default)
        {
            Filter = filter;
            Token = ct;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeVulnerabilityQuery : IVulnerabilityQuery
    {
        public VulnerabilityListDto Result = null!;
        public VulnerabilityFilter? Filter;
        public CancellationToken Token;

        public Task<VulnerabilityListDto> GetAsync(VulnerabilityFilter filter, CancellationToken ct = default)
        {
            Filter = filter;
            Token = ct;
            return Task.FromResult(Result);
        }
    }

    // ---- Builders de DTOs canônicos ---------------------------------------------------------------------

    private static WorkspacePostureDto Posture(string state = "Evaluated", double? pct = 62.5, DateTimeOffset? latest = null)
    {
        var overall = new WorkspaceOverallDto(
            FormulaVersion: "aegis-score-v1", EvaluationState: state, Percentage: pct, CoveragePercentage: 40,
            AchievedScore: 10, EvaluatedMaxScore: 16, EligibleMaxScore: 40, EligibleControls: 20,
            EvaluatedControls: 8, CompliantControls: 5, NonCompliantControls: 2, MitigatedControls: 1,
            NotEvaluatedControls: 12, Severities: Array.Empty<SeverityCountDto>(), LatestEvidenceAt: latest);
        var slice = new EvidenceCoverageSliceDto(0, 0, 0, 0, 0);
        return new WorkspacePostureDto(
            overall, Array.Empty<FunctionPostureDto>(),
            new ConnectorHealthSummaryDto(0, 0, 0, 0, 0, 0, 0, null, Array.Empty<ConnectorHealthItemDto>()),
            new EvidenceCoverageSummaryDto(slice, slice, slice, slice));
    }

    private static PostureExposureItemDto ExposureItem(string title) => new(
        Guid.NewGuid(), $"ext-{title}", title, "Identity", "AAD", "Config",
        CurrentScore: 3, MaxScore: 10, Gap: 7, SourceRank: 1, Tier: "A",
        ImplementationCost: "Low", UserImpact: "Low", Remediation: "Do X", RemediationImpact: "None",
        Threats: new[] { "AccountBreach" }, SourceState: "Default", LifecycleState: "Open",
        FirstSeenAt: DateTimeOffset.UnixEpoch, LastSeenAt: DateTimeOffset.UnixEpoch, ResolvedAt: null);

    private static PostureExposureListDto ExposureList(
        IReadOnlyList<PostureExposureItemDto> items, DateTimeOffset? collectedAt, int page = 1, int pageSize = 5)
    {
        var summary = new PostureExposureSummaryDto(
            SourceLabel: "Microsoft Secure Score", TotalOpen: items.Count, TotalResolved: 0,
            OpenByCategory: Array.Empty<PostureExposureCategoryCountDto>(),
            LastCollectedAt: collectedAt, LatestSecureScorePercent: collectedAt is null ? null : 55, LatestSecureScoreAt: collectedAt);
        return new PostureExposureListDto(summary, items, items.Count, page, pageSize);
    }

    private static VulnerabilityItemDto VulnItem(string cve) => new(
        Guid.NewGuid(), cve, $"{cve} title", "High", CvssScore: 8.1, CvssVector: "v", PublicExploit: true,
        ExploitVerified: false, Epss: 0.3, PublishedOn: DateTimeOffset.UnixEpoch, AssetId: Guid.NewGuid(),
        AssetName: "web-01", AssetCriticality: 4, AssetSubType: "Server", EffectiveLifecycle: "Open",
        Status: "Open", DetectedAt: DateTimeOffset.UnixEpoch, Sources: Array.Empty<VulnerabilityObservationDto>());

    private static VulnerabilityListDto VulnList(
        IReadOnlyList<VulnerabilityItemDto> items, DateTimeOffset? collectedAt, bool neverCollected,
        int page = 1, int pageSize = 5)
    {
        var summary = new VulnerabilitySummaryDto(
            TotalOpen: items.Count, TotalResolved: 0, DistinctCvesOpen: items.Count, AffectedAssetsOpen: items.Count,
            OpenBySeverity: Array.Empty<VulnerabilitySeverityCountDto>(), Sources: Array.Empty<VulnerabilitySourceDto>(),
            LastCollectedAt: collectedAt, NeverCollected: neverCollected);
        return new VulnerabilityListDto(summary, items, items.Count, page, pageSize);
    }

    private static (PriorityWorkspaceQuery query, FakePostureQuery p, FakeExposureQuery e, FakeVulnerabilityQuery v, FakeTimeProvider clock)
        Build(WorkspacePostureDto posture, PostureExposureListDto exposures, VulnerabilityListDto vulns, DateTimeOffset? now = null)
    {
        var p = new FakePostureQuery { Result = posture };
        var e = new FakeExposureQuery { Result = exposures };
        var v = new FakeVulnerabilityQuery { Result = vulns };
        var clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        return (new PriorityWorkspaceQuery(p, e, v, clock), p, e, v, clock);
    }

    // ---- (1) Composição: reúne as três dimensões numa única leitura ------------------------------------

    [Fact]
    public async Task Compoe_AsTresDimensoes()
    {
        var (query, p, e, v, _) = Build(
            Posture(),
            ExposureList(new[] { ExposureItem("MFA") }, DateTimeOffset.UnixEpoch),
            VulnList(new[] { VulnItem("CVE-1") }, DateTimeOffset.UnixEpoch, neverCollected: false));

        var result = await query.GetAsync();

        p.Calls.Should().Be(1, "a postura é lida uma única vez");
        result.Posture.Should().NotBeNull();
        result.ConfigurationExposures.Top.Should().ContainSingle().Which.Title.Should().Be("MFA");
        result.Vulnerabilities.Top.Should().ContainSingle().Which.CveId.Should().Be("CVE-1");
        result.ReadModelVersion.Should().Be(PriorityWorkspaceDto.Version);
    }

    // ---- (2) Filtros enviados às filas: aberto + página 1 + teto 5, sem filtros extras ------------------

    [Fact]
    public async Task EnviaFiltros_AbertoPagina1Limite5_SemFiltrosExtras()
    {
        var (query, _, e, v, _) = Build(
            Posture(),
            ExposureList(Array.Empty<PostureExposureItemDto>(), null),
            VulnList(Array.Empty<VulnerabilityItemDto>(), null, neverCollected: true));

        await query.GetAsync();

        PriorityWorkspaceDto.MaxQueueItems.Should().Be(5);

        e.Filter!.State.Should().Be(PostureExposureStateFilter.Open);
        e.Filter.Page.Should().Be(1);
        e.Filter.PageSize.Should().Be(5);
        e.Filter.Category.Should().BeNull();
        e.Filter.Service.Should().BeNull();
        e.Filter.Search.Should().BeNull();

        v.Filter!.State.Should().Be(VulnerabilityLifecycleFilter.Open);
        v.Filter.Page.Should().Be(1);
        v.Filter.PageSize.Should().Be(5);
        v.Filter.Severity.Should().BeNull();
        v.Filter.Exploit.Should().Be(VulnerabilityExploitFilter.All);
        v.Filter.AssetId.Should().BeNull();
        v.Filter.Provider.Should().BeNull();
        v.Filter.ConnectorId.Should().BeNull();
        v.Filter.Search.Should().BeNull();
    }

    // ---- (3) Preserva itens e resumos VERBATIM (mesma referência — nada é recalculado) ------------------

    [Fact]
    public async Task Preserva_ItensEResumos_SemRecalcular()
    {
        var posture = Posture();
        var exposures = ExposureList(new[] { ExposureItem("MFA") }, DateTimeOffset.UnixEpoch);
        var vulns = VulnList(new[] { VulnItem("CVE-1") }, DateTimeOffset.UnixEpoch, neverCollected: false);
        var (query, _, _, _, _) = Build(posture, exposures, vulns);

        var result = await query.GetAsync();

        // Mesma referência prova que a composição NÃO reconstrói nem recalcula os valores autoritativos.
        result.Posture.Should().BeSameAs(posture.Overall);
        result.ConfigurationExposures.Summary.Should().BeSameAs(exposures.Summary);
        result.ConfigurationExposures.Top.Should().BeSameAs(exposures.Items);
        result.Vulnerabilities.Summary.Should().BeSameAs(vulns.Summary);
        result.Vulnerabilities.Top.Should().BeSameAs(vulns.Items);

        // Ativos afetados e frescor derivam dos resumos existentes — não há campo recalculado.
        result.Vulnerabilities.Summary.AffectedAssetsOpen.Should().Be(1);
        result.ConfigurationExposures.Summary.LastCollectedAt.Should().Be(DateTimeOffset.UnixEpoch);
    }

    // ---- (4) Vazio / nunca-coletado: reflete os vazios das filas, distinguindo "nunca coletado" ---------

    [Fact]
    public async Task Vazio_NuncaColetado_ERefletido()
    {
        var (query, _, _, _, _) = Build(
            Posture(state: "NotEvaluated", pct: null),
            ExposureList(Array.Empty<PostureExposureItemDto>(), collectedAt: null),
            VulnList(Array.Empty<VulnerabilityItemDto>(), collectedAt: null, neverCollected: true));

        var result = await query.GetAsync();

        result.Posture.EvaluationState.Should().Be("NotEvaluated");
        result.Posture.Percentage.Should().BeNull();
        result.ConfigurationExposures.Top.Should().BeEmpty();
        result.ConfigurationExposures.Summary.LastCollectedAt.Should().BeNull("nunca coletado ≠ coletado sem achados");
        result.Vulnerabilities.Top.Should().BeEmpty();
        result.Vulnerabilities.Summary.NeverCollected.Should().BeTrue();
    }

    // ---- (5) Propaga o CancellationToken às três filas -------------------------------------------------

    [Fact]
    public async Task Propaga_CancellationToken()
    {
        var (query, p, e, v, _) = Build(
            Posture(),
            ExposureList(Array.Empty<PostureExposureItemDto>(), null),
            VulnList(Array.Empty<VulnerabilityItemDto>(), null, neverCollected: true));
        using var cts = new CancellationTokenSource();

        await query.GetAsync(cts.Token);

        p.Token.Should().Be(cts.Token);
        e.Token.Should().Be(cts.Token);
        v.Token.Should().Be(cts.Token);
    }

    // ---- (6) GeneratedAt vem do TimeProvider injetável -------------------------------------------------

    [Fact]
    public async Task GeneratedAt_VemDoTimeProvider()
    {
        var now = new DateTimeOffset(2026, 8, 23, 9, 30, 0, TimeSpan.Zero);
        var (query, _, _, _, clock) = Build(
            Posture(),
            ExposureList(Array.Empty<PostureExposureItemDto>(), null),
            VulnList(Array.Empty<VulnerabilityItemDto>(), null, neverCollected: true), now);

        var result = await query.GetAsync();

        result.GeneratedAt.Should().Be(clock.GetUtcNow()).And.Be(now);
    }

    // ---- (7) NÃO há score agregado no contrato composto -------------------------------------------------

    [Fact]
    public void Contrato_NaoIntroduzScoreAgregado()
    {
        // As dimensões são heterogêneas; o contrato composto NÃO pode criar um "score de risco" combinado.
        // Nenhum dos tipos NOVOS de composição declara propriedade cujo nome contenha "score" (os scores
        // legítimos — CurrentScore/MaxScore da exposição, CvssScore da vuln — vivem nos DTOs de item, que são
        // fatos por-fonte pré-existentes, nunca um agregado desta camada).
        var newTypes = new[]
        {
            typeof(PriorityWorkspaceDto),
            typeof(PriorityExposureQueueDto),
            typeof(PriorityVulnerabilityQueueDto),
        };

        foreach (var t in newTypes)
        {
            t.GetProperties().Select(pr => pr.Name)
                .Should().NotContain(n => n.Contains("score", StringComparison.OrdinalIgnoreCase),
                    $"{t.Name} não pode introduzir um score agregado entre dimensões heterogêneas");
        }
    }

    // ---- (8) Endpoint: padrão de autorização e SEM tenant externo --------------------------------------

    [Fact]
    public void Endpoint_ExigeAutenticacao_RotaEsperada_ESemTenantExterno()
    {
        var controller = typeof(PrioritiesController);

        controller.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull();

        var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull("a superfície é protegida a nível de classe, como as demais leituras");
        authorize!.Roles.Should().BeNull("é leitura autenticada padrão — sem papel específico");
        authorize.Policy.Should().BeNull("não é uma superfície de plataforma");

        controller.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/v1/priorities");

        var get = controller.GetMethod(nameof(PrioritiesController.Get))!;
        get.GetCustomAttribute<HttpGetAttribute>().Should().NotBeNull("é somente leitura");

        // Tenant IMPLÍCITO: o endpoint jamais aceita tenant por parâmetro (URL/QueryString/body). Só o token.
        get.GetParameters().Should().OnlyContain(pr => pr.ParameterType == typeof(CancellationToken),
            "o tenant é herdado do contexto autenticado, nunca recebido como parâmetro");
    }

    // ---- (9) DI: a árvore completa resolve em runtime a partir do composition root real ----------------

    [Fact]
    public void Di_ResolveArvoreCompleta_EmRuntime()
    {
        // Composition root REAL (mesmo padrão de AegisAiDependencyInjectionTests): AddAegisScoreInfrastructure
        // registra o DbContext (só REGISTRA — connection string dummy, nenhuma conexão é aberta) e as três
        // queries; o host provê o ITenantContext. Resolver IPriorityWorkspaceQuery força a CONSTRUÇÃO de toda a
        // árvore (as três queries + DbContext + TimeProvider) — prova o wiring de runtime que a reflexão não cobre.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=aegis_test;Username=test;Password=test",
            ["Ai:Mode"] = "Simulated",
        }).Build();

        var services = new ServiceCollection();
        services.AddAegisScoreInfrastructure(config);
        services.AddScoped<ITenantContext>(_ => new SystemTenantContext(null));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPriorityWorkspaceQuery>()
            .Should().BeOfType<PriorityWorkspaceQuery>("o endpoint compõe as três queries + TimeProvider");
    }
}
