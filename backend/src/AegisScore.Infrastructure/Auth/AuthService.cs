using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Autenticação com Refresh Token Rotation (RTR) e detecção de reutilização (breach).
/// Opera dentro do tenant ambiente: como <see cref="User"/> e <see cref="UserRefreshToken"/> são
/// ITenantOwned, o query filter do <see cref="AegisScoreDbContext"/> já isola toda leitura por tenant
/// e o StampTenant carimba/valida o tenant em toda escrita (fail-closed).
/// </summary>
public sealed class AuthService : IAuthService
{
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

        // ---- Breach detection: reutilização de um token já revogado ----
        // Um token válido só é revogado quando rotacionado. Vê-lo de novo significa que a cadeia
        // RTR foi clonada — assume-se sessão comprometida e derruba-se tudo (fail-closed).
        if (stored.IsRevoked)
        {
            _logger.LogWarning(
                "SECURITY: reutilização de refresh token revogado (possível roubo de sessão). " +
                "Tenant={Tenant} User={User} TokenId={TokenId}. Revogando TODOS os tokens ativos do usuário.",
                stored.TenantId, stored.UserId, stored.Id);

            await RevokeAllActiveAsync(stored.UserId, ct);
            return null;
        }

        if (stored.IsExpired)
            return null;

        // Recarrega o dono para reemitir o access token com as claims atuais (papel, e-mail, status).
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || !user.IsActive)
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;   // órfão/desativado: encerra a cadeia
            await _db.SaveChangesAsync(ct);
            return null;
        }

        // ---- Rotação (RTR): revoga o atual, aponta o sucessor e emite um novo par ----
        var pair = IssuePair(user, replaces: stored);
        await _db.SaveChangesAsync(ct);
        return pair;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var stored = await _db.UserRefreshTokens
            .FirstOrDefaultAsync(t => t.Token == refreshToken && t.RevokedAt == null, ct);
        if (stored is null)
            return;

        stored.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Monta o par de tokens e enfileira a persistência do novo refresh token. Quando <paramref name="replaces"/>
    /// é informado, executa a rotação (revoga o antigo e encadeia o sucessor). O <c>SaveChanges</c> fica a
    /// cargo do chamador, para agrupar com outras alterações (ex.: LastLoginAt) numa única transação.
    /// </summary>
    private TokenPair IssuePair(User user, UserRefreshToken? replaces = null)
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

        if (replaces is not null)
        {
            replaces.RevokedAt = DateTimeOffset.UtcNow;
            replaces.ReplacedByToken = refresh;
        }

        return new TokenPair(access, accessExp, refresh, refreshExp);
    }

    /// <summary>Breach: revoga todos os refresh tokens ainda ativos do usuário (dentro do tenant ambiente).</summary>
    private async Task RevokeAllActiveAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await _db.UserRefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var t in active)
            t.RevokedAt = now;

        await _db.SaveChangesAsync(ct);
    }
}
