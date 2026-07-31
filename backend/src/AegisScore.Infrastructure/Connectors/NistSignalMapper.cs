using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-AUD-043] Autoridade determinística de mapeamento (Capability, SignalKey) → subcategorias NIST, no
/// framework ATIVO. Lê APENAS reference data (<see cref="SignalMapping"/>/catálogo, sem query filter), então
/// funciona mesmo no endpoint de push (anônimo, sem tenant ambiente). O cliente/adaptador não é autoridade
/// e o LLM não participa. Só devolve códigos que EXISTEM no framework ativo — um sinal sem mapping (ou cujo
/// mapping não tem nenhum código válido) fica AUSENTE do dicionário, e o chamador o rejeita (push) ou pula (pull).
/// </summary>
public sealed class NistSignalMapper : INistSignalMapper
{
    private readonly AegisScoreDbContext _db;

    public NistSignalMapper(AegisScoreDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveAsync(
        ConnectorCapability capability, IReadOnlyCollection<string> signalKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
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
                result[m.SignalKey] = codes;
        }
        return result;
    }
}
