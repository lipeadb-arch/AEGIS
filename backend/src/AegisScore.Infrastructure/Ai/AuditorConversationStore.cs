using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Memória das conversas do Auditor, isolada por tenant (filtro global fail-closed) E por conta. Lê só os turnos
/// mais recentes (o histórico enviado ao motor é limitado) e grava um turno por vez: a sequência é única por conversa, então duas
/// respostas concorrentes na mesma conversa não se misturam — a segunda recebe conflito.
/// </summary>
public sealed class AuditorConversationStore : IAuditorConversationStore
{
    /// <summary>Turnos lidos para o histórico enviado ao motor.</summary>
    internal const int RecentTurns = 8;

    /// <summary>Limite de turnos por conversa: depois disso, a pessoa inicia uma nova (o contexto do tenant é sempre relido).</summary>
    internal const int MaxTurns = 60;

    private const int MaxHistoryText = 2000;
    private const int MaxQuestion = 4000;
    private const int MaxReply = 12000;
    private const int MaxFocus = 500;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public AuditorConversationStore(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    private Guid RequireTenant() =>
        _tenant.TenantId is { } t && t != Guid.Empty ? t : throw new TenantSecurityException("Conversa do Auditor sem tenant autenticado.");

    public async Task<AuditorConversation> ReadAsync(Guid accountId, Guid? conversationId, CancellationToken ct = default)
    {
        RequireTenant();
        if (accountId == Guid.Empty) throw new AuditorConversationNotFoundException();
        if (conversationId is null) return new AuditorConversation(Guid.NewGuid(), 0, Array.Empty<AuditorMessage>());

        // Filtro global = tenant; o predicado = conta. Conversa de outro tenant ou de outra pessoa é indistinguível de inexistente.
        var turns = await _db.AuditorConversationTurns.AsNoTracking()
            .Where(t => t.ConversationId == conversationId && t.AccountId == accountId)
            .OrderByDescending(t => t.Sequence)
            .Take(RecentTurns)
            .ToListAsync(ct);
        if (turns.Count == 0) throw new AuditorConversationNotFoundException();
        var messages = turns.OrderBy(t => t.Sequence)
            .SelectMany(t => new[] { new AuditorMessage("user", Limit(t.Question)), new AuditorMessage("assistant", Limit(t.Reply)) })
            .ToList();
        return new AuditorConversation(conversationId.Value, turns.Max(t => t.Sequence), messages);
    }

    public async Task AppendAsync(Guid accountId, AuditorConversation conversation, string question, string reply, string focusLabel, bool simulated,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        if (accountId == Guid.Empty) throw new AuditorConversationNotFoundException();
        if (conversation.Sequence >= MaxTurns)
            throw new AuditorConversationConflictException("Esta conversa atingiu o limite de turnos. Inicie uma nova conversa.");

        var turn = new AuditorConversationTurn
        {
            TenantId = tenantId,
            ConversationId = conversation.Id,
            AccountId = accountId,
            Sequence = conversation.Sequence + 1,
            Question = Cut(question, MaxQuestion),
            Reply = Cut(reply, MaxReply),
            FocusLabel = string.IsNullOrWhiteSpace(focusLabel) ? null : Cut(focusLabel, MaxFocus),
            Simulated = simulated,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.AuditorConversationTurns.Add(turn);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(turn).State = EntityState.Detached;
            // A sequência já existe nesta conversa (outro turno concluído no meio) — de qualquer conta: a conversa não é reaproveitável.
            if (await _db.AuditorConversationTurns.AsNoTracking()
                    .AnyAsync(t => t.ConversationId == conversation.Id && t.Sequence == turn.Sequence, ct))
                throw new AuditorConversationConflictException("Outra resposta foi concluída nesta conversa enquanto esta era preparada. Tente novamente.");
            throw;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    private static string Limit(string text) => text.Length <= MaxHistoryText ? text : text[..MaxHistoryText] + "…";

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
