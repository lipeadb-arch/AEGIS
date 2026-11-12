using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// [AEGIS-KNIGHT-SCOPE-01] Escopo salvo da avaliação KNIGHT do tenant do contexto. Scoped (DbContext com filtro de tenant e
/// carimbo fail-closed): a leitura e a gravação de um tenant nunca alcançam o registro de outro. Não coleta nada, não altera
/// avaliação nem fotografia publicada — só decide quais fontes compõem os totais do consolidado daqui em diante.
/// </summary>
public sealed class KnightAssessmentScopeService : IKnightAssessmentScopeService
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public KnightAssessmentScopeService(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<KnightAssessmentScopeView> GetAsync(CancellationToken ct = default)
    {
        RequireTenant();
        return await ReadAsync(_db, ct);
    }

    /// <summary>Leitura compartilhada com quem já tem o DbContext do tenant (publicação e prévia do consolidado).</summary>
    internal static async Task<KnightAssessmentScopeView> ReadAsync(AegisScoreDbContext db, CancellationToken ct)
    {
        var row = await db.KnightAssessmentScopes.AsNoTracking().FirstOrDefaultAsync(ct);
        return row is null
            ? KnightAssessmentScopeView.Default
            : new KnightAssessmentScopeView(KnightAssessmentScopeFormat.Parse(row.ExcludedSources), row.UpdatedAt ?? row.CreatedAt, row.UpdatedByName);
    }

    public async Task<KnightAssessmentScopeView> SaveAsync(
        IReadOnlyCollection<KnightSourceType> inScope, string? updatedByName, CancellationToken ct = default)
    {
        RequireTenant();
        var excluded = KnightAssessmentScopeFormat.Serialize(KnightAssessmentScopeFormat.OutOfScopeFrom(inScope));
        var by = string.IsNullOrWhiteSpace(updatedByName) ? null : updatedByName.Trim()[..Math.Min(200, updatedByName.Trim().Length)];
        var now = _clock.GetUtcNow();

        for (var attempt = 0; ; attempt++)
        {
            var row = await _db.KnightAssessmentScopes.FirstOrDefaultAsync(ct);
            if (row is null)
            {
                row = new KnightAssessmentScope { ExcludedSources = excluded, UpdatedByName = by, CreatedAt = now, UpdatedAt = now };
                _db.KnightAssessmentScopes.Add(row);
            }
            else
            {
                row.ExcludedSources = excluded;
                row.UpdatedByName = by;
                row.UpdatedAt = now;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return new KnightAssessmentScopeView(KnightAssessmentScopeFormat.Parse(row.ExcludedSources), row.UpdatedAt, row.UpdatedByName);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Outra gravação criou o registro do tenant entre a leitura e esta inserção (índice único): descarta a tentativa
                // e regrava sobre o registro que venceu — o último a salvar define o escopo, sem um segundo registro.
                _db.ChangeTracker.Clear();
            }
        }
    }

    private void RequireTenant()
    {
        if (_tenant.TenantId is not Guid)
            throw new TenantSecurityException("Escopo da avaliação KNIGHT sem tenant resolvido no contexto (fail-closed).");
    }
}
