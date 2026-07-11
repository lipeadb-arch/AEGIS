using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
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

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

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
        fv.Functions.Add(fn);

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
