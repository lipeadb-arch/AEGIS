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

    /// <summary>
    /// [AEGIS-AUDITOR-CONTEXT-01] O mesmo contexto mais os registros dos assessments do tenant para o FOCO da tela (KNIGHT, NIST,
    /// documentos, inventário e publicações), em fontes citáveis. Seleção de outro tenant → <see cref="AuditorFocusNotFoundException"/>.
    /// </summary>
    Task<AuditorTenantContext> BuildAsync(AuditorFocus focus, CancellationToken ct = default);
}
