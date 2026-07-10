using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Autenticação com Refresh Token Rotation (RTR), rotação ATÔMICA e detecção de reutilização (breach).
/// Opera dentro do tenant ambiente: como <see cref="User"/> e <see cref="UserRefreshToken"/> são
/// ITenantOwned, o query filter do <see cref="AegisScoreDbContext"/> já isola toda leitura por tenant
/// e o StampTenant carimba/valida o tenant em toda escrita (fail-closed).
/// </summary>
public sealed class AuthService : IAuthService
{
    /// <summary>
    /// Janela de idempotência da rotação. Uma reapresentação do MESMO refresh token dentro deste
    /// intervalo (aba concorrente, retry de rede, corrida do dedup do front) é tratada como retry
    /// benigno e recebe o sucessor já emitido ao líder — em vez de disparar breach.
    /// Trade-off consciente (padrão "refresh token reuse leeway"): dentro da janela, um co-possuidor
    /// do token também obtém o sucessor; o intervalo curto (5 s) limita a exposição. Fora da janela,
    /// qualquer reuso é comprometimento.
    /// </summary>
    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromSeconds(5);

    private readonly AegisScoreDbContext _db;
    private readonly IJwtTokenService _tokens;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<AuthService> _logger;

    // Hash válido e de mesmo custo, usado para verificar a senha mesmo quando o usuário não existe —
    // evita revelar (por timing) se um e-mail está ou não cadastrado no tenant.
    private static readonly string DummyHash = new Pbkdf2PasswordHasher().Hash("aegis-timing-guard");

    public AuthService(
        AegisScoreDbContext db,
        IJwtTokenService tokens,
        IPasswordHasher hasher,
        ILogger<AuthService> logger)
    {
        _db = db;
        _tokens = tokens;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<TokenPair?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();

        // Query isolada pelo tenant ambiente — um usuário de outro tenant é invisível aqui.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);

        // Verifica a senha SEMPRE (mesmo sem usuário) para não vazar existência do e-mail por timing.
        var ok = _hasher.Verify(password ?? "", user?.PasswordHash ?? DummyHash);
        if (user is null || !ok || !user.IsActive)
            return null;

        // [Baixo] Tenant suspenso não autentica (fail-closed). Esta consulta só ocorre após credencial
        // válida e é desprezível frente ao PBKDF2 acima, então não reabre canal de timing sobre e-mail.
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == user.TenantId, ct);
        if (tenant is null || tenant.Status == TenantStatus.Suspended)
            return null;

        var pair = IssuePair(user);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return pair;
    }

    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return null;

        var stored = await _db.UserRefreshTokens.FirstOrDefaultAsync(t => t.Token == refreshToken, ct);
        if (stored is null)
            return null;   // token desconhecido dentro deste tenant

        // [Crítico 2] Expiração ANTES de reuso: um token expirado replayado só retorna 401 e NUNCA
        // dispara a cascata de revogação — fecha o DoS por replay de token ancião.
        if (stored.IsExpired)
            return null;

        // Já revogado quando lido = rotacionado por outra request. Janela de idempotência ou breach.
        if (stored.IsRevoked)
            return await HandleAlreadyRotatedAsync(stored, ct);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || !user.IsActive)
        {
            // Órfão/desativado: encerra a cadeia de forma atômica (não dispara breach).
            await _db.UserRefreshTokens
                .Where(t => t.Id == stored.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => DateTimeOffset.UtcNow), ct);
            return null;
        }

        // [Crítico 1] Rotação ATÔMICA. O UPDATE ... WHERE RevokedAt IS NULL é a seção crítica: sob
        // concorrência, apenas UMA request afeta 1 linha; as demais afetam 0 e "perdem a corrida".
        // Elimina o fork de cadeia (dois filhos ativos do mesmo pai) que cegava a detecção de breach.
        var (newRefresh, newRefreshExp) = _tokens.CreateRefreshToken();
        var now = DateTimeOffset.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var claimed = await _db.UserRefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, _ => now)
                .SetProperty(t => t.ReplacedByToken, _ => newRefresh), ct);

        if (claimed == 0)
        {
            // Perdi a corrida entre o SELECT e o UPDATE. Desfaz e trata como já-rotacionado, lendo o
            // estado do vencedor — cai na janela de idempotência (retry benigno) ou em breach.
            await tx.RollbackAsync(ct);
            var latest = await _db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == stored.Id, ct);
            return latest is null ? null : await HandleAlreadyRotatedAsync(latest, ct);
        }

        // Venci a corrida: emito o novo filho. ITenantOwned → StampTenant carimba/valida no SaveChanges.
        _db.UserRefreshTokens.Add(new UserRefreshToken
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Token = newRefresh,
            ExpiresAt = newRefreshExp,
        });
        var (access, accessExp) = _tokens.CreateAccessToken(user);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new TokenPair(access, accessExp, newRefresh, newRefreshExp);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        // Revoga de forma atômica, só se ainda ativo. Idempotente.
        await _db.UserRefreshTokens
            .Where(t => t.Token == refreshToken && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// Trata a reapresentação de um token JÁ revogado (rotacionado):
    ///  (a) Dentro da janela de idempotência e com sucessor ativo → retry benigno: reemite o access
    ///      token e devolve o MESMO refresh (o sucessor) já entregue ao líder.
    ///  (b) Fora da janela → reuso genuíno = breach: revoga a CADEIA do token (blast radius reduzido,
    ///      não todas as sessões do usuário) e retorna null.
    /// </summary>
    private async Task<TokenPair?> HandleAlreadyRotatedAsync(UserRefreshToken parent, CancellationToken ct)
    {
        if (parent.RevokedAt is { } revokedAt
            && DateTimeOffset.UtcNow - revokedAt <= IdempotencyWindow
            && !string.IsNullOrEmpty(parent.ReplacedByToken))
        {
            var successor = await _db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == parent.ReplacedByToken, ct);

            if (successor is not null && successor.IsActive)
            {
                var user = await _db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == successor.UserId, ct);
                if (user is not null && user.IsActive)
                {
                    // Idempotente: mesmo par entregue ao líder (novo access + refresh = sucessor).
                    var (access, accessExp) = _tokens.CreateAccessToken(user);
                    return new TokenPair(access, accessExp, successor.Token, successor.ExpiresAt);
                }
            }
        }

        _logger.LogWarning(
            "SECURITY: reutilização de refresh token revogado fora da janela de idempotência " +
            "(possível roubo de sessão). Tenant={Tenant} User={User} TokenId={TokenId}. " +
            "Revogando a CADEIA do token.",
            parent.TenantId, parent.UserId, parent.Id);

        await RevokeChainAsync(parent, ct);
        return null;
    }

    /// <summary>Monta o par de tokens do LOGIN (sem rotação) e enfileira a persistência do refresh.</summary>
    private TokenPair IssuePair(User user)
    {
        var (access, accessExp) = _tokens.CreateAccessToken(user);
        var (refresh, refreshExp) = _tokens.CreateRefreshToken();

        _db.UserRefreshTokens.Add(new UserRefreshToken
        {
            TenantId = user.TenantId,   // revalidado/carimbado pelo StampTenant no SaveChanges
            UserId = user.Id,
            Token = refresh,
            ExpiresAt = refreshExp,
        });

        return new TokenPair(access, accessExp, refresh, refreshExp);
    }

    /// <summary>
    /// [Crítico 2] Breach com blast radius reduzido: revoga apenas a CADEIA (família) do token,
    /// caminhando para frente via <see cref="UserRefreshToken.ReplacedByToken"/>. Outras sessões
    /// legítimas do mesmo usuário (outros dispositivos/navegadores) permanecem ativas.
    /// </summary>
    private async Task RevokeChainAsync(UserRefreshToken start, CancellationToken ct)
    {
        var chain = new List<Guid> { start.Id };
        var nextToken = start.ReplacedByToken;
        var guard = 0;

        // Caminha a linhagem para frente; o guard evita laço infinito em dados legados bifurcados.
        while (!string.IsNullOrEmpty(nextToken) && guard++ < 256)
        {
            var link = nextToken;
            var node = await _db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == link, ct);
            if (node is null)
                break;

            chain.Add(node.Id);
            nextToken = node.ReplacedByToken;
        }

        await _db.UserRefreshTokens
            .Where(t => chain.Contains(t.Id) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => DateTimeOffset.UtcNow), ct);
    }
}
