using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Tests.Legacy;

/// <summary>
/// [AEGIS-AUD-043/019] Mapeamento determinístico (Capability, SignalKey) → subcategorias NIST (+
/// <see cref="SignalMapping.ScoringHint"/>) no framework ATIVO, transferido da produção para os testes sem mudança de
/// semântica: reconstrói a proveniência de telemetria das fotografias históricas <c>aegis-score-v1</c> do fixture.
/// Só devolve códigos que EXISTEM no framework ativo; um sinal sem mapping fica AUSENTE do dicionário.
/// </summary>
internal sealed class NistSignalMapper
{
    private readonly AegisScoreDbContext _db;

    public NistSignalMapper(AegisScoreDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, SignalMappingResolution>> ResolveAsync(
        ConnectorCapability capability, IReadOnlyCollection<string> signalKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, SignalMappingResolution>(StringComparer.Ordinal);
        var keys = signalKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct().ToList();
        if (keys.Count == 0) return result;

        var fvId = await _db.FrameworkVersions.Where(f => f.IsActive)
            .Select(f => f.Id).FirstOrDefaultAsync(ct);
        if (fvId == Guid.Empty) return result;

        var mappings = await _db.SignalMappings
            .Where(m => m.FrameworkVersionId == fvId && m.Capability == capability && keys.Contains(m.SignalKey))
            .ToListAsync(ct);
        if (mappings.Count == 0) return result;

        // Integridade referencial: só códigos que existem no framework ativo entram no resultado.
        var validCodes = (await (
            from s in _db.Subcategories
            join c in _db.Categories on s.CategoryId equals c.Id
            join f in _db.Functions on c.FunctionId equals f.Id
            where f.FrameworkVersionId == fvId
            select s.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        foreach (var m in mappings)
        {
            var codes = m.SubcategoryCodes.Where(validCodes.Contains).Distinct().ToList();
            if (codes.Count > 0)
                result[m.SignalKey] = new SignalMappingResolution(codes, m.ScoringHint);
        }
        return result;
    }
}

/// <summary>Resolução de UM sinal: as subcategorias mapeadas e o <see cref="SignalMapping.ScoringHint"/> (pode ser nulo).</summary>
internal sealed record SignalMappingResolution(IReadOnlyList<string> SubcategoryCodes, string? ScoringHint);
