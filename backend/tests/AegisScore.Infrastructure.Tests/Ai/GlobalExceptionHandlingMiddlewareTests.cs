using System.IO;
using System.Threading.Tasks;
using AegisScore.Api;
using AegisScore.Application.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Middleware de erro: prova que cota esgotada e indisponibilidade genérica da IA viram 503 DISTINTOS
/// (códigos próprios) e que o corpo é SANITIZADO — nunca vaza a mensagem interna crua do provedor.
/// </summary>
public sealed class GlobalExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task Quota429_Vira503_ComCodigoProprio_ESemVazarMensagemInterna()
    {
        var body = await InvokeAndReadAsync(
            new AiQuotaExhaustedException("detalhe interno cru do provedor que NAO pode vazar"));

        body.status.Should().Be(503);
        body.json.Should().Contain("\"code\":\"ai_quota_exhausted\"");
        body.json.Should().Contain("Cota gratuita da IA temporariamente esgotada.");
        body.json.Should().Contain("traceId");
        body.json.Should().NotContain("detalhe interno cru", "o corpo é sanitizado — nada da exceção crua");
    }

    [Fact]
    public async Task Indisponibilidade_Vira503_ComCodigoGenerico()
    {
        var body = await InvokeAndReadAsync(
            new AiUnavailableException("timeout cru ao contatar o provedor"));

        body.status.Should().Be(503);
        body.json.Should().Contain("\"code\":\"ai_unavailable\"");
        body.json.Should().Contain("Serviço de IA temporariamente indisponível.");
        body.json.Should().NotContain("timeout cru");
    }

    private static async Task<(int status, string json)> InvokeAndReadAsync(System.Exception toThrow)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        var mw = new GlobalExceptionHandlingMiddleware(
            _ => throw toThrow, NullLogger<GlobalExceptionHandlingMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, json);
    }
}
