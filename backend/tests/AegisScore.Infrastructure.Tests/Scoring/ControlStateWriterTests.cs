using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// Testes do <see cref="ControlStateWriter"/> — o escritor ÚNICO do ledger de conformidade, por onde
/// passa cada ponto do Aegis Score. Rodam sobre SQLite in-memory (banco relacional real: exercita o
/// índice único, o Global Query Filter e o stamping fail-closed de verdade, sem PostgreSQL).
///
/// Cobre os quatro contratos que sustentam a integridade do score: upsert idempotente, isolamento
/// multitenant fail-closed, tradução status → pontos, e a PRECEDÊNCIA de fonte (telemetria autoritativa,
/// documento apenas em upgrade).
/// </summary>
public sealed class ControlStateWriterTests : IDisposable
{
    private const int MaxPoints = 20;              // par de propósito: 50% = 10 exato, sem arredondamento
    private const string SubCode = "PR.AA-01";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public ControlStateWriterTests()
    {
        // Banco in-memory vive enquanto a conexão estiver aberta; xUnit instancia a classe por caso de
        // teste, então cada teste recebe um banco limpo e isolado.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    // ---- Precedência de fonte: o coração da integridade -----------------------------

    [Fact]
    public async Task ApplyVerdictAsync_DocumentoNaoDeveRebaixarTelemetria_MantemEstadoCompliant()
    {
        const string evidenciaAutoritativa = "telemetria: MFA aplicado no host WKS-042";

        // 1) A telemetria valida a implementação efetiva → 100% dos pontos.
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.Compliant, evidenciaAutoritativa, VerdictSource.Telemetry);

        // 2) Um PDF reprocessado tenta gravar crédito parcial (50%).
        await using (var db = NewContext(TenantA))
        {
            var verdict = await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "documental: política vigente",
                VerdictSource.Documentary);

            // O writer devolve o estado PRESERVADO, não o proposto.
            verdict.Status.Should().Be(ControlStatus.Compliant);
            verdict.AwardedScore.Should().Be(MaxPoints);
        }

        // 3) O banco permanece Compliant, 100%, com a evidência autoritativa intacta.
        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.Compliant, "um documento jamais rebaixa evidência de telemetria");
        state.CurrentScore.Should().Be(MaxPoints);
        state.AiEvidence.Should().Be(evidenciaAutoritativa, "a evidência autoritativa não pode ser sobrescrita");
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task ApplyVerdictAsync_DocumentoNaoDeveSobrescreverTelemetriaFalha_MantemZeroPontos()
    {
        const string falhaTecnica = "telemetria: MFA desativado no tenant";

        // 1) A telemetria constata a FALHA do controle → 0 pontos.
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, falhaTecnica, VerdictSource.Telemetry);

        // 2) Um PDF de política tenta creditar 50%. PONTUA MAIS que o estado vigente — e mesmo assim é
        //    recusado: a precedência é de FONTE, não de pontuação.
        await using (var db = NewContext(TenantA))
        {
            var verdict = await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "documental: política de MFA vigente",
                VerdictSource.Documentary);

            verdict.Status.Should().Be(ControlStatus.NonCompliant);
            verdict.AwardedScore.Should().Be(0);
        }

        // 3) A falha técnica permanece exposta: nenhum documento a maquia.
        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant, "documento jamais sobrescreve telemetria, nem para cima");
        state.CurrentScore.Should().Be(0, "um PDF não pode maquiar um controle comprovadamente falho");
        state.AiEvidence.Should().Be(falhaTecnica);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task ApplyVerdictAsync_TelemetriaSobrescreveEstadoDocumental_EAssumeAPrecedencia()
    {
        // Documento credita 50% num controle ainda não medido...
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "política vigente", VerdictSource.Documentary);

        // ...e a telemetria depois constata a falha: autoritativa, rebaixa a 0 e ASSUME a procedência.
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "MFA desligado", VerdictSource.Telemetry);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.CurrentScore.Should().Be(0);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry,
            "a partir daqui nenhum documento consegue sobrescrever este estado");
    }

    [Fact]
    public async Task ApplyVerdictAsync_TelemetriaEhAutoritativa_PodeRebaixarUmControleValidado()
    {
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.Compliant, "MFA ativo", VerdictSource.Telemetry);

        // O controle quebrou: a telemetria DEVE poder rebaixá-lo — é o oposto exato do documento.
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "MFA desativado no tenant", VerdictSource.Telemetry);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant, "telemetria rebaixa: o controle deixou de funcionar");
        state.CurrentScore.Should().Be(0);
    }

    [Fact]
    public async Task ApplyVerdictAsync_DocumentoEmControleAindaNaoAvaliado_InsereComCreditoParcial()
    {
        await using var db = NewContext(TenantA);

        var verdict = await WriterFor(db, TenantA).ApplyVerdictAsync(
            TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "política vigente", VerdictSource.Documentary);

        verdict.AwardedScore.Should().Be(MaxPoints / 2);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.MitigatedByThirdParty);
        state.CurrentScore.Should().Be(MaxPoints / 2, "upgrade a partir de 'ainda não avaliado' é legítimo");
        state.LastVerdictSource.Should().Be(VerdictSource.Documentary, "a procedência acompanha o estado");
    }

    [Fact]
    public async Task ApplyVerdictAsync_DocumentoComPontuacaoIgual_NaoSobrescreveEvidenciaVigente()
    {
        const string primeira = "primeiro documento";

        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, primeira, VerdictSource.Documentary);

        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "segundo documento", VerdictSource.Documentary);

        await using var assert = NewContext(TenantA);
        var states = await assert.TenantControlStates.ToListAsync();
        states.Should().ContainSingle();
        states[0].AiEvidence.Should().Be(primeira,
            "empate não sobrescreve: a escrita documental é um upgrade, não um refresh");
    }

    // ---- Idempotência ---------------------------------------------------------------

    [Fact]
    public async Task ApplyVerdictAsync_ChamadoDuasVezes_FazUpsertSemDuplicarRegistro()
    {
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "1a analise", VerdictSource.Telemetry);

        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.Compliant, "2a analise", VerdictSource.Telemetry);

        await using var assert = NewContext(TenantA);
        var states = await assert.TenantControlStates.ToListAsync();

        states.Should().ContainSingle("o upsert atualiza a célula tenant × subcategoria, nunca insere outra");
        states[0].Status.Should().Be(ControlStatus.Compliant);
        states[0].CurrentScore.Should().Be(MaxPoints);
        states[0].AiEvidence.Should().Be("2a analise", "a evidência autoritativa mais recente prevalece");
    }

    [Fact]
    public async Task ApplyVerdictAsync_NoPrimeiroVeredito_CarimbaOTenantDoContexto()
    {
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.Compliant, "ok", VerdictSource.Telemetry);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();

        // TenantId NUNCA vem do chamador: é carimbado pelo StampTenant no SaveChanges.
        state.TenantId.Should().Be(TenantA);
        state.LastEvaluatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ---- Segurança: fail-closed -----------------------------------------------------

    [Fact]
    public async Task ApplyVerdictAsync_ComTenantIdDivergenteDoContexto_LancaTenantSecurityException()
    {
        await using var db = NewContext(TenantA);
        var writer = WriterFor(db, TenantA);

        // Tentativa de gravar no tenant B a partir de um contexto resolvido para o tenant A.
        var acao = () => writer.ApplyVerdictAsync(
            TenantB, SubCode, ControlStatus.Compliant, "spoof", VerdictSource.Telemetry);

        await acao.Should().ThrowAsync<TenantSecurityException>();

        // Defesa em profundidade: nada foi persistido, sob nenhum tenant.
        (await db.TenantControlStates.IgnoreQueryFilters().CountAsync())
            .Should().Be(0, "um veredito rejeitado jamais pode deixar rastro no ledger");
    }

    [Fact]
    public async Task ApplyVerdictAsync_SemTenantResolvidoNoContexto_LancaTenantSecurityException()
    {
        // Fail-CLOSED: contexto sem tenant (ex.: worker sem SystemTenantContext) nunca escreve.
        await using var db = NewContext(null);
        var writer = WriterFor(db, null);

        var acao = () => writer.ApplyVerdictAsync(
            TenantA, SubCode, ControlStatus.Compliant, "sem tenant", VerdictSource.Telemetry);

        await acao.Should().ThrowAsync<TenantSecurityException>();
    }

    [Fact]
    public async Task ApplyVerdictAsync_EstadoDeUmTenant_NaoVazaParaOutro()
    {
        await using (var db = NewContext(TenantA))
            await WriterFor(db, TenantA).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.Compliant, "de A", VerdictSource.Telemetry);

        // O Global Query Filter (fail-closed) isola a leitura do tenant B.
        await using var dbB = NewContext(TenantB);
        (await dbB.TenantControlStates.CountAsync()).Should().Be(0);

        // Mas a linha existe fisicamente, carimbada para o tenant A.
        await using var raw = NewContext(TenantA);
        var all = await raw.TenantControlStates.IgnoreQueryFilters().ToListAsync();
        all.Should().ContainSingle().Which.TenantId.Should().Be(TenantA);
    }

    // ---- Cálculo de score: status → pontos ------------------------------------------

    [Theory]
    [InlineData(ControlStatus.Compliant, MaxPoints)]                    // 100% — só a telemetria concede
    [InlineData(ControlStatus.MitigatedByThirdParty, MaxPoints / 2)]    // 50%  — teto da evidência documental
    [InlineData(ControlStatus.NonCompliant, 0)]                         // 0%
    public async Task ApplyVerdictAsync_TraduzStatusEmPontosDoAegisScore(ControlStatus status, int pontosEsperados)
    {
        await using var db = NewContext(TenantA);
        var writer = WriterFor(db, TenantA);

        var verdict = await writer.ApplyVerdictAsync(
            TenantA, SubCode, status, "evidencia", VerdictSource.Telemetry);

        verdict.Status.Should().Be(status);
        verdict.AwardedScore.Should().Be(pontosEsperados);
        verdict.MaxScorePoints.Should().Be(MaxPoints, "o denominador vem do catálogo, nunca do estado do tenant");

        await using var assert = NewContext(TenantA);
        (await assert.TenantControlStates.SingleAsync()).CurrentScore.Should().Be(pontosEsperados);
    }

    // ---- Robustez: código fora do catálogo ------------------------------------------

    [Fact]
    public async Task ApplyVerdictAsync_ComSubcategoriaForaDoCatalogo_LancaInvalidOperationException()
    {
        // É este contrato que o DocumentAnalysisWorker captura para ignorar um código alucinado pelo LLM
        // sem abortar o documento inteiro.
        await using var db = NewContext(TenantA);
        var writer = WriterFor(db, TenantA);

        var acao = () => writer.ApplyVerdictAsync(
            TenantA, "XX.YY-99", ControlStatus.Compliant, "inexistente", VerdictSource.Telemetry);

        await acao.Should().ThrowAsync<InvalidOperationException>().WithMessage("*XX.YY-99*");
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>Writer sob o MESMO tenant ambiente do contexto — como no worker e no request HTTP.</summary>
    private static IControlStateWriter WriterFor(AegisScoreDbContext db, Guid? tenantId) =>
        new ControlStateWriter(db, new SystemTenantContext(tenantId), NullLogger<ControlStateWriter>.Instance);

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
}
