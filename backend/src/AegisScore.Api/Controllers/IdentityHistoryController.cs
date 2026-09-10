using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-ADM-02] Superfície tenant-scoped SOMENTE LEITURA do HISTÓRICO MENSAL do ADM de identidade.
///
/// Tenant sempre IMPLÍCITO (claim <c>tenant_id</c> do JWT + Global Query Filter fail-closed) — nunca por URL ou
/// query string. Papéis: o <c>[Authorize]</c> de classe, como nas demais leituras consultivas — quem enxerga a
/// postura do ambiente enxerga a evolução dela.
///
/// A rota LÊ as consolidações já gravadas pela manutenção em fundo. Ela NÃO consolida e NÃO dispara expurgo:
/// uma consulta que apaga dados é uma armadilha, porque quem a chama não sabe que está escrevendo. Devolve
/// agregados por conjunto, a proveniência da coleta escolhida e a comparabilidade entre meses — nunca nome,
/// e-mail, identificador de objeto, credencial ou payload do fornecedor.
///
/// NÃO altera o AEGIS Score, a conformidade NIST nem qualquer estado determinístico de controle.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/identity-history")]
public class IdentityHistoryController : ControllerBase
{
    private readonly IIdentityHistoryQuery _history;

    public IdentityHistoryController(IIdentityHistoryQuery history) => _history = history;

    /// <summary>
    /// Série mensal do tenant. <paramref name="from"/> e <paramref name="to"/> são meses no formato
    /// <c>yyyy-MM</c>; omitidos, valem a janela retida inteira. Um período fora da janela é RECORTADO a ela —
    /// devolver meses vazios de antes da retenção faria "apagado por prazo" parecer "nunca coletado".
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IdentityHistoryViewDto>> Get(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        CancellationToken ct = default)
    {
        if (!TryParseMonth(from, out var de)) return BadRequest(Erro(nameof(from), from));
        if (!TryParseMonth(to, out var ate)) return BadRequest(Erro(nameof(to), to));

        return Ok(await _history.GetAsync(new IdentityHistoryRangeRequest(de, ate), ct));
    }

    /// <summary>
    /// Aceita <c>yyyy-MM</c> e ausência; recusa o resto. Um mês malformado é recusado em vez de ignorado: cair
    /// silenciosamente na janela inteira devolveria um período que o cliente não pediu, com a mesma cara de um
    /// período que ele pediu.
    /// </summary>
    private static bool TryParseMonth(string? raw, out DateOnly? month)
    {
        month = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!DateOnly.TryParseExact(
                raw.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;

        month = parsed;
        return true;
    }

    private static object Erro(string campo, string? valor) => new
    {
        error = $"O parâmetro '{campo}' precisa ser um mês no formato yyyy-MM (recebido: '{valor}').",
    };
}
