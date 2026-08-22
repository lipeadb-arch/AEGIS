using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Reconciliação de exposições de postura (upsert + resolução + reabertura) e leitura
/// tenant-scoped. Bateria relacional (SQLite) para a lógica determinística; a bateria PostgreSQL cobre a
/// migration/unicidade/reconciliação em banco real.
/// </summary>
public sealed class PostureExposureReconcilerTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnA = Guid.Parse("aa000000-0000-0000-0000-0000000000c1");
    private static readonly Guid ConnB = Guid.Parse("bb000000-0000-0000-0000-0000000000c2");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public PostureExposureReconcilerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private static PostureFinding Finding(string id, double current, double max, int rank, string category = "Identity") =>
        new(id, $"{id} title", category, "Azure Active Directory", "Config", current, max, max - current,
            rank, "Core", "Low", "Low", "do it", "none", new[] { "Account Breach" }, null);

    private static PostureFindingCollection Collection(bool complete, params PostureFinding[] findings) =>
        new(findings, complete, "Microsoft Secure Score");

    private async Task ReconcileAsync(Guid tenant, Guid connector, PostureFindingCollection collection)
    {
        await using var db = NewContext(tenant);
        await new PostureExposureReconciler(db).ReconcileAsync(connector, collection, CancellationToken.None);
    }

    // ---- 9) Criação, atualização, resolução e reabertura idempotentes -----------------------------

    [Fact]
    public async Task Reconcile_FirstDetection_CreatesOpen_WithFirstSeen()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 5, 10, 1)));

        await using var db = NewContext(TenantA);
        var row = await db.PostureExposureFindings.SingleAsync();
        row.ExternalId.Should().Be("c1");
        row.LifecycleState.Should().Be(PostureExposureState.Open);
        row.Gap.Should().Be(5);
        row.ResolvedAt.Should().BeNull();
        row.FirstSeenAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Reconcile_SecondCollection_UpdatesButPreservesFirstSeen()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 5, 10, 1)));
        DateTimeOffset firstSeen;
        await using (var db = NewContext(TenantA))
            firstSeen = (await db.PostureExposureFindings.SingleAsync()).FirstSeenAt;

        await Task.Delay(10);
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 8, 10, 1)));   // gap agora 2

        await using var assert = NewContext(TenantA);
        var row = await assert.PostureExposureFindings.SingleAsync();
        row.Gap.Should().Be(2, "a coleta nova atualiza o gap");
        row.CurrentScore.Should().Be(8);
        row.FirstSeenAt.Should().Be(firstSeen, "FirstSeenAt é PRESERVADO entre coletas");
        row.LifecycleState.Should().Be(PostureExposureState.Open);
    }

    [Fact]
    public async Task Reconcile_GapGone_InCompleteCollection_ResolvesButKeepsRow()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 5, 10, 1), Finding("c2", 3, 10, 2)));

        // c1 deixou de ter gap (não vem na coleta); c2 permanece. Coleta COMPLETA.
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c2", 3, 10, 2)));

        await using var assert = NewContext(TenantA);
        var c1 = await assert.PostureExposureFindings.SingleAsync(f => f.ExternalId == "c1");
        c1.LifecycleState.Should().Be(PostureExposureState.Resolved, "sumiu do conjunto numa coleta completa → resolvido");
        c1.ResolvedAt.Should().NotBeNull();
        (await assert.PostureExposureFindings.CountAsync()).Should().Be(2, "nada é excluído silenciosamente");
    }

    [Fact]
    public async Task Reconcile_IncompleteCollection_DoesNotResolveByOmission()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 5, 10, 1)));
        // Coleta INCOMPLETA sem c1 → NÃO pode resolver por omissão.
        await ReconcileAsync(TenantA, ConnA, Collection(false));

        await using var assert = NewContext(TenantA);
        var c1 = await assert.PostureExposureFindings.SingleAsync();
        c1.LifecycleState.Should().Be(PostureExposureState.Open, "coleta incompleta não resolve por omissão");
    }

    [Fact]
    public async Task Reconcile_ResolvedThenSeenAgain_Reopens()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 5, 10, 1)));
        await ReconcileAsync(TenantA, ConnA, Collection(true));   // resolve c1
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("c1", 4, 10, 1)));   // volta com gap

        await using var assert = NewContext(TenantA);
        var c1 = await assert.PostureExposureFindings.SingleAsync();
        c1.LifecycleState.Should().Be(PostureExposureState.Open, "visto de novo com gap → reabre");
        c1.ResolvedAt.Should().BeNull();
        c1.Gap.Should().Be(6);
    }

    // ---- 11) Coleta repetida não duplica -----------------------------------------------------------

    [Fact]
    public async Task Reconcile_RepeatedIdenticalCollection_IsIdempotent_NoDuplicates()
    {
        var coll = Collection(true, Finding("c1", 5, 10, 1), Finding("c2", 3, 10, 2));
        await ReconcileAsync(TenantA, ConnA, coll);
        await ReconcileAsync(TenantA, ConnA, coll);
        await ReconcileAsync(TenantA, ConnA, coll);

        await using var assert = NewContext(TenantA);
        (await assert.PostureExposureFindings.CountAsync()).Should().Be(2, "sincronização repetida é idempotente");
    }

    // ---- 10) Índice único (chave natural) + isolamento entre tenants ------------------------------

    [Fact]
    public async Task NaturalKey_IsUnique_RejectsDuplicate()
    {
        await using var db = NewContext(TenantA);
        db.PostureExposureFindings.Add(new PostureExposureFinding { ConnectorConfigId = ConnA, ExternalId = "dup", Title = "a" });
        db.PostureExposureFindings.Add(new PostureExposureFinding { ConnectorConfigId = ConnA, ExternalId = "dup", Title = "b" });
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("(Tenant, Connector, ExternalId) é único no banco");
    }

    [Fact]
    public async Task Reconcile_IsolatesTenants()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true, Finding("shared", 5, 10, 1)));
        await ReconcileAsync(TenantB, ConnB, Collection(true, Finding("shared", 2, 10, 1)));

        await using var a = NewContext(TenantA);
        (await a.PostureExposureFindings.CountAsync()).Should().Be(1, "A só vê o seu");
        (await a.PostureExposureFindings.SingleAsync()).Gap.Should().Be(5);

        await using var raw = NewContext(null);
        (await raw.PostureExposureFindings.IgnoreQueryFilters().Select(f => f.TenantId).Distinct().ToListAsync())
            .Should().BeEquivalentTo(new[] { TenantA, TenantB }, "as duas linhas coexistem, isoladas por tenant");
    }

    // ---- 14) Contrato da query de lista e resumo --------------------------------------------------

    [Fact]
    public async Task Query_ListAndSummary_OrdersByRankThenGap_AndFilters()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true,
            Finding("id-rank3", 5, 10, 3, "Identity"),
            Finding("data-rank1", 4, 10, 1, "Data"),
            Finding("id-rank2big", 1, 10, 2, "Identity")));
        // Resolve uma para checar a contagem de resolvidas + o Secure Score do resumo (via sinal).
        await ReconcileAsync(TenantA, ConnA, Collection(true,
            Finding("data-rank1", 4, 10, 1, "Data"),
            Finding("id-rank2big", 1, 10, 2, "Identity")));   // id-rank3 resolvido
        await using (var seed = NewContext(TenantA))
        {
            seed.Signals.Add(new EvidenceSignal
            {
                ConnectorConfigId = ConnA, SignalKey = "secureScore.overall",
                NumericValue = 62, Unit = "percent", CollectedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var query = new PostureExposureQuery(NewContext(TenantA), new SystemTenantContext(TenantA));
        var open = await query.GetAsync(new PostureExposureFilter(PostureExposureStateFilter.Open));

        open.Summary.TotalOpen.Should().Be(2);
        open.Summary.TotalResolved.Should().Be(1);
        open.Summary.LatestSecureScorePercent.Should().Be(62, "o resumo traz o Secure Score geral mais recente");
        open.Summary.LastCollectedAt.Should().NotBeNull();
        open.Items.Should().HaveCount(2);
        open.Items.Select(i => i.ExternalId).Should().Equal(new[] { "data-rank1", "id-rank2big" },
            "ordena por rank asc (nulos por último), depois maior gap");

        // Filtro por categoria.
        var identity = await query.GetAsync(new PostureExposureFilter(PostureExposureStateFilter.Open, Category: "Identity"));
        identity.Items.Should().OnlyContain(i => i.Category == "Identity");
        identity.Items.Should().ContainSingle(i => i.ExternalId == "id-rank2big");

        // Filtro por estado resolvido.
        var resolved = await query.GetAsync(new PostureExposureFilter(PostureExposureStateFilter.Resolved));
        resolved.Items.Should().ContainSingle(i => i.ExternalId == "id-rank3");
    }

    [Fact]
    public async Task Query_NeverCollected_ReportsNullSecureScore_NotZero()
    {
        var query = new PostureExposureQuery(NewContext(TenantA), new SystemTenantContext(TenantA));
        var result = await query.GetAsync(new PostureExposureFilter());

        result.Summary.LatestSecureScorePercent.Should().BeNull("ausência de coleta é null, nunca 0%");
        result.Summary.LastCollectedAt.Should().BeNull();
        result.Summary.TotalOpen.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    // ---- 13) Contexto da IA contém somente os campos permitidos -----------------------------------

    [Fact]
    public async Task AuditorContext_IncludesTopExposures_OnlyAllowedFields()
    {
        await ReconcileAsync(TenantA, ConnA, Collection(true,
            Finding("id-1", 5, 10, 1, "Identity"),
            Finding("data-1", 4, 10, 2, "Data")));

        var builder = new AuditorContextBuilder(NewContext(TenantA), new WorkspacePostureQuery(NewContext(TenantA), new SystemTenantContext(TenantA)));
        var context = await builder.BuildAsync();

        context.TopExposures.Should().NotBeNull();
        context.TopExposures!.Should().HaveCount(2);
        context.TopExposures!.First().ExternalId.Should().Be("id-1", "ordenado por rank");

        // Serialização do contexto: SÓ os campos permitidos das exposições; nunca resposta bruta/actionUrl/PII.
        // Lowercased para ser robusto à política de nomes (a IA recebe os campos, não importa a caixa).
        var json = JsonSerializer.Serialize(context.TopExposures).ToLowerInvariant();
        json.Should().Contain("gap", "o gap é campo permitido");
        json.Should().Contain("remediation", "a remediação curta é campo permitido");
        json.Should().NotContain("currentscore", "score bruto do controle não vai à IA (só o gap)");
        json.Should().NotContain("maxscore");
        json.Should().NotContain("sourcestate", "estado da fonte é metadado — não vai ao contexto da IA");
        json.Should().NotContain("actionurl");
        json.Should().NotContain("updatedby");
    }
}
