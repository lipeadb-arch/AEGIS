using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Scoring;

/// <summary>
/// Escritor ÚNICO do ledger de conformidade do Aegis Score. Concentra a regra de scoring
/// (status → pontos) e o upsert idempotente da célula tenant × subcategoria — antes embutidos no
/// <c>AegisAiEvaluatorService</c>. Toda fonte de evidência (telemetria, documento) grava por aqui, de
/// modo que o numerador do score jamais tenha duas implementações capazes de divergir.
///
/// Secure-by-design: opera SEMPRE dentro do tenant ambiente (query filter + stamping fail-closed do
/// AegisScoreDbContext). O tenantId explícito é uma asserção de defesa em profundidade — precisa bater
/// com o contexto (header HTTP num request, ou SystemTenantContext num worker de ingestão).
/// </summary>
public sealed class ControlStateWriter : IControlStateWriter
{
    /// <summary>Crédito parcial para risco coberto por controle compensatório (transferido, não eliminado).</summary>
    private const double MitigatedCreditFactor = 0.5;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<ControlStateWriter> _log;

    public ControlStateWriter(AegisScoreDbContext db, ITenantContext tenant, ILogger<ControlStateWriter> log)
    {
        _db = db;
        _tenant = tenant;
        _log = log;
    }

    public async Task<ComplianceVerdict> ApplyVerdictAsync(
        Guid tenantId, string subcategoryCode, ControlStatus status, string evidence,
        VerdictSource source, IReadOnlyList<ComplianceCheck>? checks = null,
        ControlIntelligence? intelligence = null,
        IReadOnlyList<MissingRequirement>? missingRequirements = null, CancellationToken ct = default)
    {
        // 1) Defesa em profundidade: o tenantId explícito precisa casar com o tenant ambiente (fail-closed).
        var ambient = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Escrita no ledger de conformidade sem tenant resolvido no contexto (fail-closed).");
        if (tenantId != ambient)
            throw new TenantSecurityException(
                $"TenantId ({tenantId}) diverge do tenant do contexto ({ambient}).");

        // 2) Catálogo global (imutável, não filtrado por tenant): a subcategoria alvo e seu peso.
        var sub = await _db.Subcategories.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == subcategoryCode, ct)
            ?? throw new InvalidOperationException(
                $"Subcategoria '{subcategoryCode}' não existe no catálogo NIST CSF 2.0.");

        var awarded = ScoreFor(status, sub.MaxScorePoints);

        // 3) Upsert idempotente. Fluxo padrão (change tracker + SaveChanges), NÃO ExecuteUpdateAsync:
        //    precisamos de insert-or-update E do stamping fail-closed do SaveChanges (ExecuteUpdate é
        //    bulk sem tracking — não insere e fura o carimbo de tenant/auditoria). O query filter garante
        //    que só enxergamos/gravamos o estado DESTE tenant; o índice único {TenantId, SubcategoryId}
        //    serializa escritas concorrentes da mesma célula.
        var state = await _db.TenantControlStates
            .FirstOrDefaultAsync(t => t.SubcategoryId == sub.Id, ct);

        // 4) Precedência de FONTE (não de pontuação). A telemetria prova implementação efetiva e é
        //    AUTORITATIVA: sobrescreve sempre, inclusive rebaixando (controle quebrou ⇒ NonCompliant
        //    prevalece). Um veredito documental só toca o estado quando ele NÃO veio de telemetria E
        //    representa um upgrade real — ver DocumentaryMayOverwrite.
        if (state is not null && source == VerdictSource.Documentary && !DocumentaryMayOverwrite(state, awarded))
        {
            _log.LogInformation(
                "Aegis Score: veredito documental de {Subcategory} recusado — estado vigente preservado " +
                "({Current} pts, fonte {Source}) no tenant {Tenant}.",
                sub.Code, state.CurrentScore, state.LastVerdictSource, tenantId);

            return new ComplianceVerdict(state.Status, state.AiEvidence ?? "", state.CurrentScore, sub.MaxScorePoints)
                {
                    Checks = DeserializeChecks(state.ChecksJson),
                    Intelligence = SafeDeserialize<ControlIntelligence>(state.IntelligenceJson),
                    // As lacunas VIGENTES, não as do veredito recusado: o retorno descreve o estado que
                    // ficou de pé. Já vêm tipadas do jsonb — sem desserialização manual aqui.
                    MissingRequirements = state.MissingRequirements,
                };
        }

        if (state is null)
        {
            state = new TenantControlState { SubcategoryId = sub.Id };   // TenantId carimbado no SaveChanges
            _db.TenantControlStates.Add(state);
        }

        state.Status = status;
        state.CurrentScore = awarded;
        state.AiEvidence = evidence;
        state.ChecksJson = checks is { Count: > 0 } ? JsonSerializer.Serialize(checks) : null;
        // O enriquecimento acompanha o veredito que o produziu: quem não o emite ZERA o campo em vez de
        // herdar o do veredito anterior — inteligência órfã descreveria um estado que não existe mais.
        state.IntelligenceJson = intelligence is not null ? JsonSerializer.Serialize(intelligence) : null;
        // Invariante do ledger, imposta AQUI e não confiada ao chamador: controle conforme não carrega
        // pendência. Nos demais status a lista acompanha o veredito que a produziu — quem não a emite
        // ZERA (mesma regra do enriquecimento acima), porque lacuna órfã descreveria um estado extinto.
        state.MissingRequirements = status == ControlStatus.Compliant
            ? new List<MissingRequirement>()
            : (missingRequirements ?? Array.Empty<MissingRequirement>()).ToList();
        state.LastVerdictSource = source;   // a procedência acompanha o estado, e governa a próxima escrita
        state.LastEvaluatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Aegis Score: subcategoria {Subcategory} avaliada como {Status} ({Awarded}/{Max}) para o tenant {Tenant}.",
            sub.Code, status, awarded, sub.MaxScorePoints, tenantId);

        return new ComplianceVerdict(status, evidence, awarded, sub.MaxScorePoints)
        {
            Checks = checks ?? Array.Empty<ComplianceCheck>(),
            Intelligence = intelligence,
            MissingRequirements = state.MissingRequirements,   // já normalizada pela invariante acima
        };
    }

    /// <summary>Desserializa o checklist persistido; tolera nulo/JSON inválido (devolve vazio, nunca lança).</summary>
    private static IReadOnlyList<ComplianceCheck> DeserializeChecks(string? json) =>
        SafeDeserialize<IReadOnlyList<ComplianceCheck>>(json) ?? Array.Empty<ComplianceCheck>();

    /// <summary>
    /// Desserializa um blob persistido; tolera nulo/JSON inválido (devolve null, nunca lança). Um blob
    /// explicável corrompido não pode derrubar a leitura do ledger — o score é a informação crítica.
    /// </summary>
    private static T? SafeDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Uma escrita DOCUMENTAL só pode tocar o estado vigente quando AS DUAS condições valem:
    /// <list type="number">
    /// <item>o estado NÃO foi gravado por telemetria — ela é a verdade absoluta sobre a implementação e
    /// jamais é sobrescrita por um documento, ainda que o documento "pontue mais" (senão um PDF de
    /// política maquiaria um MFA comprovadamente desligado: 0 pts → 50 pts);</item>
    /// <item>o novo veredito PONTUA MAIS que o atual — upgrade, nunca rebaixa nem faz refresh de empate.</item>
    /// </list>
    /// </summary>
    private static bool DocumentaryMayOverwrite(TenantControlState state, int awarded) =>
        state.LastVerdictSource != VerdictSource.Telemetry && awarded > state.CurrentScore;

    /// <summary>Traduz o status do controle em pontos do Aegis Score, limitado por MaxScorePoints.</summary>
    private static int ScoreFor(ControlStatus status, int maxScorePoints) => status switch
    {
        ControlStatus.Compliant             => maxScorePoints,
        ControlStatus.MitigatedByThirdParty => (int)Math.Round(maxScorePoints * MitigatedCreditFactor),
        _                                   => 0,   // NonCompliant → não pontua
    };
}
