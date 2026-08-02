using System.Net.Mime;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AegisScore.Api.Health;

/// <summary>
/// [AEGIS-AUD-048] Escritor MÍNIMO da resposta de health check. Emite apenas o status geral e o status
/// de cada check pelo NOME (que nós controlamos: <c>self</c>, <c>readiness</c>). NUNCA serializa
/// <c>description</c>, <c>data</c> ou <c>exception</c> — de modo que connection strings, stack traces,
/// nomes sensíveis ou a lista de pendências do schema jamais atravessem a fronteira HTTP. O detalhe
/// operacional vive no log do servidor.
/// </summary>
public static class HealthResponseWriter
{
    public static Task WriteMinimalAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
            }),
        });
    }
}
