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
/// [AEGIS-AUD-048] Comportamentos de MAIOR RISCO dos health checks: readiness que degrada com segurança
/// (nunca propaga exceção) e a resposta HTTP que NÃO vaza detalhe interno. O caminho "Healthy" ponta a
/// ponta é coberto pelo smoke real contra PostgreSQL migrado (readiness sobre SQLite reporta sempre
/// migrations pendentes, por design do EnsureCreated).
/// </summary>
public sealed class HealthCheckTests
{
    /// <summary>IServiceProvider vazio — o readiness resolve o key ring OPCIONALMENTE (ausente em teste).</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static AegisReadinessHealthCheck NewCheck(AegisScoreDbContext db) =>
        new(db, new EmptyServiceProvider(), NullLogger<AegisReadinessHealthCheck>.Instance);

    [Fact]
    public async Task Readiness_SchemaNaoPreparado_RetornaUnhealthy_SemLancar()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new AegisScoreDbContext(
            new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(connection).Options,
            new SystemTenantContext(null));
        await db.Database.EnsureCreatedAsync();   // schema do MODELO, sem histórico de migrations → "pendente"

        var result = await NewCheck(db).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        // Rótulo genérico — nunca a lista de pendências, nomes de tabela ou connection string.
        result.Description.Should().Be("schema-not-ready");
    }

    [Fact]
    public async Task Readiness_BancoInacessivel_RetornaUnhealthy_SemLancar()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new AegisScoreDbContext(
            new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(connection).Options,
            new SystemTenantContext(null));

        // Simula indisponibilidade do banco: a conexão some por baixo do contexto.
        connection.Dispose();

        // Não pode PROPAGAR — o health check tem de degradar com segurança.
        var act = async () => await NewCheck(db).CheckHealthAsync(new HealthCheckContext());
        var result = await act.Should().NotThrowAsync();

        result.Which.Status.Should().Be(HealthStatus.Unhealthy);
    }

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
