using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] O percurso INTEIRO no nível de serviço, com o coletor Entra REAL sobre HTTP
/// simulado (sem rede): coleta → aquisição do ADM (com os objetos de configuração) → avaliação lida da evidência
/// PERSISTIDA → objetos afetados e evidências de configuração → resumo → fotografia v2 → HTML e CSV.
///
/// O cenário é o que a leitura antiga errava: a MFA administrativa é exigida por política DIRIGIDA AO PAPEL de
/// Administrador Global (com a conta de emergência excluída), o Administrador de Segurança não é coberto, e o
/// bloqueio de autenticação legada existe só em SOMENTE RELATÓRIO. As permissões de risco de identidade faltam
/// (coleta parcial). Nomes de objetos trazem conteúdo hostil — marcação e fórmula — para provar que as
/// exportações tratam evidência como dado.
/// </summary>
public sealed class KnightMulticloudReportTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-7777-7777-7777-777777777777");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-8888-8888-8888-888888888888");
    private const string GaTemplate = KnightGraphScenario.GaTemplate;
    private const string SaTemplate = KnightGraphScenario.SaTemplate;
    private const string Hostil = KnightGraphScenario.Hostil;
    private const string Formula = KnightGraphScenario.Formula;

    private readonly SqliteConnection _connection;

    public KnightMulticloudReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Coleta_ADM_Avaliacao_Fotografia_E_Exportacoes_ContamAMesmaCoisa()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var assessment = await ServiceFor(db, TenantA).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);

        // ---- Coleta parcial declarada, avaliação concluída a partir da aquisição persistida ----
        assessment.Status.Should().Be(KnightRunStatus.Completed);
        assessment.SourceState.Should().Be(KnightSourceState.PartialCollection, "as duas capacidades de risco devolveram 403");
        assessment.CatalogVersion.Should().Be("ak-knight-v3");
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == assessment.Id);
        run.IdentityAcquisitionId.Should().NotBeNull();
        (await db.IdentityConfigurationObservations.CountAsync(c => c.AcquisitionId == run.IdentityAcquisitionId))
            .Should().Be(4, "duas políticas e dois papéis ativos preservados como evidência da aquisição");

        // ---- MFA administrativa lida papel a papel ----
        var adminMfa = assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-008");
        adminMfa.Status.Should().Be(KnightIndicatorStatus.Exposed);
        adminMfa.AffectedObjectCount.Should().Be(1, "só o Administrador de Segurança não é coberto");
        adminMfa.HasAffectedDetail.Should().BeTrue();
        adminMfa.AffectedDetailComplete.Should().BeTrue();
        adminMfa.EvidenceObjectCount.Should().BeGreaterThan(0);
        adminMfa.Presentation!.DomainLabel.Should().Be("Identidade");
        adminMfa.Presentation.Service.Should().Be("Microsoft Entra ID");
        adminMfa.Presentation.Criterion.Should().NotBeNull("a avaliação usou o catálogo corrente");

        var svc = ServiceFor(db, TenantA);
        var afetados = await svc.GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-008", 1, 50, null);
        afetados!.Items.Should().ContainSingle().Which.Should().Match<KnightAffectedObjectView>(
            o => o.Kind == KnightAffectedObjectKind.DirectoryRole && o.DisplayName == "Security Administrator");

        var evidencias = await svc.GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-008", 1, 50, null, default, KnightObjectRelation.Evidence);
        evidencias!.Items.Should().Contain(o => o.Kind == KnightAffectedObjectKind.Policy && o.ExternalId == "p-admins"
            && o.ObservedConfiguration!.Contains("habilitada"));
        evidencias.Items.Should().Contain(o => o.Kind == KnightAffectedObjectKind.DirectoryRole && o.ExternalId == GaTemplate
            && o.Detail!.Contains("Conta de emergência"), "a exceção declarada aparece pelo NOME do membro privilegiado");
        evidencias.Items.Should().Contain(o => o.Kind == KnightAffectedObjectKind.TenantSetting);

        // ---- Autenticação legada: a política existe, mas só relata ----
        var legado = assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-007");
        legado.Status.Should().Be(KnightIndicatorStatus.Exposed);
        var legadoEv = await svc.GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-007", 1, 50, null, default, KnightObjectRelation.Evidence);
        legadoEv!.Items.Should().Contain(o => o.ExternalId == "p-legacy" && o.Detail!.Contains("somente relatório"));

        assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-014").Status.Should().Be(KnightIndicatorStatus.Passed);
        assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-001").Title.Should().Contain("registrado");

        // ---- Unidades distintas: controles × ocorrências × objetos únicos ----
        var resumo = await svc.GetAffectedSummaryAsync(assessment.Id);
        // 001 → Bruno (sem método registrado); 004 → o convidado; 008 → o papel de Administrador de Segurança.
        resumo!.Occurrences.Should().Be(3);
        resumo.UniqueObjects.Should().Be(3);
        resumo.Complete.Should().BeTrue();
        resumo.Top.Select(t => t.ExternalId).Should().Contain(new[] { "u2", "g-hostil", SaTemplate });
        await using (var dbB = NewContext(TenantB))
        {
            (await ServiceFor(dbB, TenantB).GetAffectedSummaryAsync(assessment.Id)).Should().BeNull("outro tenant não enxerga");
            (await ServiceFor(dbB, TenantB).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-008", 1, 50, null, default, KnightObjectRelation.Evidence))
                .Should().BeNull("nem as evidências de configuração");
        }

        // ---- Fotografia v2: tudo congelado, hash re-derivável ----
        var published = await PostureFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null, assessment.Id);
        published.Summary.SchemaVersion.Should().Be(PostureSnapshotSchema.KnightReportVersion);
        var snapshot = await LoadAsync(db, published.Summary.Id);
        PostureSnapshotHasher.Verify(snapshot).Should().BeTrue();
        snapshot.Objects.Count.Should().Be(await db.KnightAffectedObjects.CountAsync(o => o.RunId == assessment.Id));
        snapshot.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-008").Recommendation.Should().NotBeNullOrWhiteSpace();
        snapshot.AdvisoryFromAi.Should().BeFalse("a IA está indisponível neste teste — o relatório usa o texto determinístico");

        // ---- HTML: autocontido, CSP restritiva, evidência hostil tratada como dado ----
        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("default-src 'none'").And.Contain("script-src 'sha256-");
        html.Should().NotContain("unsafe-inline").And.NotContain("<link").And.NotContain("<script src");
        html.Should().NotContain("<script>alert").And.NotContain("<img src=x");
        html.Should().Contain("\\u003Cscript\\u003Ealert", "o conteúdo hostil vai escapado na ilha de dados");
        html.Should().Contain("Análise da postura de segurança multicloud");

        // ---- CSV: mesma fotografia, reconcilia com o modelo do HTML, fórmula neutralizada ----
        var csvBytes = (await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Csv))!.Content;
        var csv = ParseCsv(Encoding.UTF8.GetString(csvBytes).TrimStart('﻿'));
        var header = csv[0];
        int Col(string n) => Array.IndexOf(header, n);
        var rows = csv.Skip(1).ToList();
        rows.Select(r => r[Col("IndicatorId")]).Distinct().Count().Should().Be(snapshot.Indicators.Count);

        var model = KnightReportModelBuilder.Build(snapshot, true);
        rows.Count(r => r[Col("ObjectRelation")] == "Afetado" && (r[Col("Status")] == "Exposed" || r[Col("Status")] == "Mitigated"))
            .Should().Be(model.Kpis.Occurrences, "HTML e CSV contam as mesmas ocorrências");
        rows.Where(r => r[Col("ObjectRelation")] == "Afetado" && (r[Col("Status")] == "Exposed" || r[Col("Status")] == "Mitigated"))
            .Select(r => (r[Col("ObjectType")], r[Col("ObjectExternalId")].ToLowerInvariant())).Distinct().Count()
            .Should().Be(model.Kpis.UniqueAffected);
        var upnCol = Col("ObjectPrincipalName");
        rows.Select(r => r[upnCol]).Should().Contain("'" + Formula, "fórmula de planilha é neutralizada");

        // ---- Histórico imutável: mudar a origem depois não muda o relatório publicado ----
        await db.KnightAffectedObjects.Where(o => o.RunId == assessment.Id).ExecuteDeleteAsync();
        await ServiceFor(db, TenantA).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        var htmlDepois = Encoding.UTF8.GetString((await new PostureSnapshotExporter(NewContext(TenantA)).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        htmlDepois.Should().Be(html, "o relatório deriva só da fotografia congelada");
    }

    [Fact]
    public void FotografiaV1_ContinuaLegivel_ComALimitacaoDeclarada_EHtmlSoParaKnight()
    {
        var v1 = new PostureSnapshot
        {
            TenantId = TenantA, Type = PostureSnapshotType.Knight, SchemaVersion = PostureSnapshotSchema.Version,
            FormulaVersion = "knight-score-v1", CatalogVersion = "ak-knight-v2", SemanticFamily = "knight:MicrosoftEntraId",
            SourceType = KnightSourceType.MicrosoftEntraId, SourceLabel = "Microsoft Entra ID",
            CapturedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z", CultureInfo.InvariantCulture),
            Score = 50, Coverage = 80, CompliantCount = 1, NonCompliantCount = 1, NotEvaluatedCount = 1,
            CollectionLimitations = new List<string> { "IdentityRiskyUsers: InsufficientPermission" },
        };
        v1.Indicators.Add(new PostureSnapshotIndicator { IndicatorId = "AK-ENTRA-001", Title = "Contas privilegiadas sem autenticação multifator efetiva", Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Critical, Status = KnightIndicatorStatus.Exposed, Evidence = "2 de 5", AffectedObjectCount = 2, SourceType = KnightSourceType.MicrosoftEntraId, NistCodes = new() { "PR.AA-01" } });
        v1.Indicators.Add(new PostureSnapshotIndicator { IndicatorId = "AK-ENTRA-002", Title = "Volume", Category = KnightIndicatorCategory.IdentityGovernance, Severity = SeverityLevel.High, Status = KnightIndicatorStatus.Passed, Evidence = "3", SourceType = KnightSourceType.MicrosoftEntraId });
        v1.ContentHash = PostureSnapshotHasher.Compute(v1);

        PostureSnapshotHasher.Verify(v1).Should().BeTrue();
        var model = KnightReportModelBuilder.Build(v1, true);
        model.Notes.Should().Contain(n => n.Contains("formato anterior (v1)"));
        model.Kpis.UniqueAffected.Should().BeNull("a v1 não congelou objetos — não se inventa o número");
        model.Controls.Single(c => c.Id == "AK-ENTRA-001").Title.Should().Contain("efetiva", "o texto histórico é literal");
        model.LegacyLimitations.Should().ContainSingle();
        Encoding.UTF8.GetString(PostureSnapshotHtmlWriter.Write(v1, true)).Should().Contain("aegis-data");
        Encoding.UTF8.GetString(PostureSnapshotCsvWriter.Write(v1)).Should().NotContain("RowKind", "o CSV v1 mantém o formato anterior");

        var nist = new PostureSnapshot { Type = PostureSnapshotType.AegisScoreNist };
        FluentActions.Invoking(() => PostureSnapshotHtmlWriter.Write(nist, true)).Should().Throw<PostureExportNotSupportedException>();
    }

    // ---- Infraestrutura -----------------------------------------------------------------------------

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
            DisplayName = "Microsoft Entra ID", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }

    internal static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId)
    {
        var tenant = new SystemTenantContext(tenantId);
        var registry = new KnightCollectorRegistry(new IKnightCollector[]
        {
            new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(new KnightGraphScenario.StubHandler()))),
        });
        var config = new EntraConfig();
        var store = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, store, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new SemIa(), evidence, store, tenant);
    }

    private static IPostureSnapshotService PostureFor(AegisScoreDbContext db, Guid tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db));

    private static Task<PostureSnapshot> LoadAsync(AegisScoreDbContext db, Guid id) =>
        db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == id);

    internal sealed class EntraConfig : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(source == KnightSourceType.MicrosoftEntraId
                ? new KnightEntraIdConfiguration("dir-demo-0001", "client", "secret")
                : new KnightSourceNotConfigured(source));
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    /// <summary>IA indisponível: o gerador cai no texto determinístico — a avaliação não depende dela.</summary>
    internal sealed class SemIa : IKnightAdvisoryGenerator
    {
        public Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default) =>
            Task.FromResult(new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false));
    }

    /// <summary>Parser CSV mínimo (';', aspas duplas escapadas) — só para ler o que o escritor gerou.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == ';') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}

/// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Diretório Entra SINTÉTICO (sem rede) compartilhado pelas baterias SQLite e PostgreSQL.</summary>
internal static class KnightGraphScenario
{
    internal const string GaTemplate = "62e90394-69f5-4237-9190-012177145e10";
    internal const string SaTemplate = "194ae4cb-b126-40b2-bd5b-6091b380977d";
    internal const string Hostil = "<script>alert('x')</script><img src=x onerror=alert(1)>";
    internal const string Formula = "=HYPERLINK(\"http://evil.example.com\",\"clique\")";

    // ---- Cenário Graph sintético --------------------------------------------------------------------

    internal static readonly string Recent = DateTimeOffset.UtcNow.AddDays(-2).ToString("o");
    internal static readonly string Old = DateTimeOffset.UtcNow.AddDays(-200).ToString("o");

    internal static (HttpStatusCode, string) Graph(HttpRequestMessage req)
    {
        var url = Uri.UnescapeDataString(req.RequestUri!.AbsoluteUri);
        if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token"))
            return (HttpStatusCode.OK, """{"access_token":"t","expires_in":3600,"token_type":"Bearer"}""");
        if (url.Contains("identityProtection"))
            return (HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"x"}}""");
        if (url.Contains("/directoryRoles/r-ga/members"))
            return (HttpStatusCode.OK, ("""{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1","userType":"Member","displayName":"Ana Prado","userPrincipalName":"ana@demo.example.com","signInActivity":{"lastSignInDateTime":"R"}},""" +
                """{"@odata.type":"#microsoft.graph.user","id":"u-bg","userType":"Member","displayName":"Conta de emergência 1","userPrincipalName":"bg1@demo.example.com","signInActivity":{"lastSignInDateTime":"R"}},""" +
                """{"@odata.type":"#microsoft.graph.user","id":"u2","userType":"Member","displayName":"Bruno Costa","userPrincipalName":"bruno@demo.example.com","signInActivity":{"lastSignInDateTime":"R"}}]}""").Replace("R", Recent));
        if (url.Contains("/directoryRoles/r-sa/members"))
            return (HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u2","userType":"Member","displayName":"Bruno Costa","userPrincipalName":"bruno@demo.example.com","signInActivity":{"lastSignInDateTime":"R"}}]}""".Replace("R", Recent));
        if (url.Contains("/directoryRoles?"))
            return (HttpStatusCode.OK, $$"""{"value":[{"id":"r-ga","displayName":"Global Administrator","roleTemplateId":"{{GaTemplate}}"},{"id":"r-sa","displayName":"Security Administrator","roleTemplateId":"{{SaTemplate}}"}]}""");
        if (url.Contains("userRegistrationDetails"))
            return (HttpStatusCode.OK, """{"value":[{"id":"u1","isMfaCapable":true},{"id":"u-bg","isMfaCapable":true},{"id":"u2","isMfaCapable":false},{"id":"u9","isMfaCapable":true}]}""");
        if (url.Contains("/users") && url.Contains("Guest"))
            return (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
            {
                value = new[] { new { id = "g-hostil", displayName = Hostil, userPrincipalName = Formula, signInActivity = new { lastSignInDateTime = Old } } },
            }));
        if (url.Contains("conditionalAccess/policies"))
            return (HttpStatusCode.OK, $$$"""
                {"value":[
                  {"id":"p-admins","displayName":"MFA para administradores","state":"enabled",
                   "conditions":{"users":{"includeRoles":["{{{GaTemplate}}}"],"excludeUsers":["u-bg"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
                   "grantControls":{"operator":"OR","builtInControls":["mfa"]}},
                  {"id":"p-legacy","displayName":"Bloquear autenticação legada","state":"enabledForReportingButNotEnforced",
                   "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["exchangeActiveSync","other"]},
                   "grantControls":{"builtInControls":["block"]}}
                ]}
                """);
        if (url.Contains("identitySecurityDefaultsEnforcementPolicy")) return (HttpStatusCode.OK, """{"isEnabled":false}""");
        if (url.Contains("appRoleAssignedTo")) return (HttpStatusCode.OK, """{"value":[]}""");
        if (url.Contains("servicePrincipals")) return (HttpStatusCode.OK, """{"id":"graph-sp"}""");
        if (url.Contains("oauth2PermissionGrants")) return (HttpStatusCode.OK, """{"value":[]}""");
        if (url.Contains("/applications")) return (HttpStatusCode.OK, """{"value":[]}""");
        return (HttpStatusCode.NotFound, "{}");
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = Graph(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
