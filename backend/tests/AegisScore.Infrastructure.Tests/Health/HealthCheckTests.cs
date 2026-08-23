using System.Text;
using AegisScore.Api.Health;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Health;

/// <summary>
/// [AEGIS-AUD-048 / AEGIS-MVP-OPS-01] Comportamentos de MAIOR RISCO dos health checks: readiness que
/// degrada com segurança (nunca propaga exceção), o CURTO-CIRCUITO barato do probe recorrente (não
/// reexecuta o <see cref="SchemaReadinessGuard"/> a cada sondagem) e a resposta HTTP que NÃO vaza detalhe
/// interno. A validação estrutural COMPLETA continua provada pelos <c>SchemaReadinessGuardTests</c>, onde
/// ela roda uma vez no arranque, fail-closed.
/// </summary>
public sealed class HealthCheckTests
{
    private static AegisReadinessHealthCheck NewCheck(AegisScoreDbContext db, StartupReadinessState startup) =>
        new(db, startup, NullLogger<AegisReadinessHealthCheck>.Instance);

    /// <summary>Estado de arranque já APROVADO (o guard completo passou uma vez, no boot).</summary>
    private static StartupReadinessState ReadyState()
    {
        var state = new StartupReadinessState();
        state.MarkReady();
        return state;
    }

    /// <summary>
    /// DbContext apontando para um SQLite cujo DIRETÓRIO não existe: <c>CanConnectAsync</c> falha de forma
    /// determinística (retorna false ou lança, e o check trata) — simula o PostgreSQL inacessível sem
    /// depender de rede. Diferente de fechar uma conexão <c>:memory:</c>, que reabriria um banco novo e
    /// vazio, mascarando a indisponibilidade.
    /// </summary>
    private static AegisScoreDbContext UnreachableDb() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>()
                .UseSqlite($"DataSource=/aegis-unreachable-{Guid.NewGuid():N}/db.sqlite").Options,
            new SystemTenantContext(null));

    // ---- Readiness recorrente ----------------------------------------------------

    [Fact]
    public async Task Readiness_ArranqueNaoPronto_RetornaUnhealthy_SemTocarOBanco()
    {
        // Banco INACESSÍVEL de propósito: se o check o tocasse, o rótulo seria "database-unavailable" ou
        // "dependency-unavailable". Obter "startup-not-ready" prova o curto-circuito ANTES de qualquer
        // acesso ao banco.
        await using var db = UnreachableDb();

        var startup = new StartupReadinessState();   // NÃO pronto

        var result = await NewCheck(db, startup).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        // Rótulo genérico do arranque — nunca lista de pendências, nome de tabela ou connection string.
        result.Description.Should().Be("startup-not-ready");
    }

    [Fact]
    public async Task Readiness_ArranquePronto_BancoAcessivel_RetornaHealthy()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new AegisScoreDbContext(
            new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(connection).Options,
            new SystemTenantContext(null));
        // Nem sequer criamos o schema: CanConnectAsync verifica a CONEXÃO, não tabelas nem catálogo.

        var result = await NewCheck(db, ReadyState()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Readiness_ArranquePronto_BancoInacessivel_RetornaUnhealthy_SemLancar()
    {
        // Arranque aprovado, mas o PostgreSQL não responde: CanConnectAsync falha.
        await using var db = UnreachableDb();

        // Não pode PROPAGAR — o health check tem de degradar com segurança.
        var act = async () => await NewCheck(db, ReadyState()).CheckHealthAsync(new HealthCheckContext());
        var result = await act.Should().NotThrowAsync();

        result.Which.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Readiness_ArranquePronto_SchemaSemCatalogo_RetornaHealthy_ProvaQueNaoRodaOGuard()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new AegisScoreDbContext(
            new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(connection).Options,
            new SystemTenantContext(null));
        // Schema do MODELO, catálogo VAZIO e, para o EF, migrations "pendentes" (sem histórico). Sob o
        // comportamento ANTIGO (guard completo por probe) isto reprovaria com "schema-not-ready".
        await db.Database.EnsureCreatedAsync();

        var result = await NewCheck(db, ReadyState()).CheckHealthAsync(new HealthCheckContext());

        // Prova COMPORTAMENTAL de que o probe recorrente NÃO executa mais o SchemaReadinessGuard: catálogo
        // ausente e migrations pendentes não reprovam — só a conectividade importa aqui.
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Readiness_ChamadasRepetidas_ArranquePronto_PermanecemHealthy()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new AegisScoreDbContext(
            new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(connection).Options,
            new SystemTenantContext(null));
        await db.Database.EnsureCreatedAsync();   // catálogo vazio de propósito

        var check = NewCheck(db, ReadyState());

        // Sondagem em laço, como o Render: permanece saudável sem depender de catálogo/regras/proveniência.
        for (var i = 0; i < 5; i++)
        {
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            result.Status.Should().Be(HealthStatus.Healthy, $"probe recorrente #{i} não relê o pacote");
        }
    }

    // ---- StartupReadinessState (latch monotônico) --------------------------------

    [Fact]
    public void StartupReadinessState_ComecaNaoPronto_ETransitaUmaVezParaPronto()
    {
        var state = new StartupReadinessState();
        state.IsReady.Should().BeFalse("o estado inicial é NÃO pronto");

        state.MarkReady();
        state.IsReady.Should().BeTrue();

        // Idempotente e sem volta: marcar de novo mantém o latch em "pronto".
        state.MarkReady();
        state.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task StartupReadinessState_SeguroSobConcorrencia()
    {
        var state = new StartupReadinessState();

        // Várias tarefas marcando ao mesmo tempo: latch monotônico, termina "pronto", sem exceção.
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => state.MarkReady())));

        state.IsReady.Should().BeTrue();
    }

    // ---- Resposta HTTP sanitizada ------------------------------------------------

    [Fact]
    public async Task ResponseWriter_NaoVazaDetalheInterno()
    {
        // Uma entrada carregada de detalhe SENSÍVEL: connection string, exceção e dados internos.
        const string secret = "Host=db.internal;Username=stars;Password=hunter2";
        var entry = new HealthReportEntry(
            status: HealthStatus.Unhealthy,
            description: secret,
            duration: TimeSpan.Zero,
            exception: new InvalidOperationException("stack trace confidencial " + secret),
            data: new Dictionary<string, object> { ["connectionString"] = secret });
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry> { ["readiness"] = entry },
            HealthStatus.Unhealthy,
            TimeSpan.Zero);

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await HealthResponseWriter.WriteMinimalAsync(ctx, report);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());

        // O ÚTIL atravessa: status geral e o status por check (nome que nós controlamos).
        body.Should().Contain("Unhealthy");
        body.Should().Contain("readiness");
        // O SENSÍVEL nunca atravessa.
        body.Should().NotContain("hunter2");
        body.Should().NotContain("Password");
        body.Should().NotContain("stack trace");
        body.Should().NotContain("connectionString");
        ctx.Response.ContentType.Should().StartWith("application/json");
    }
}
