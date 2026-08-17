using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;

namespace AegisScore.Application.Services;

/// <summary>
/// Monta o contexto tenant-scoped, SOMENTE LEITURA e LIMITADO com que o Auditor Virtual fundamenta as
/// respostas (<see cref="AuditorTenantContext"/>). O tenant é IMPLÍCITO (fail-closed via ITenantContext +
/// Global Query Filter) — nunca parâmetro. Só agregados e trechos curtos já validados entram no contexto;
/// jamais documento completo, log bruto, credencial ou identificador pessoal.
/// </summary>
public interface IAuditorContextBuilder
{
    Task<AuditorTenantContext> BuildAsync(CancellationToken ct = default);
}
