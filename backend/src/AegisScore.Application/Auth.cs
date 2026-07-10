using System;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

/// <summary>Par de tokens emitido no login e a cada rotação (RTR).</summary>
public record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Serviço de autenticação: login por credenciais e rotação de refresh token (RTR) com detecção
/// de reutilização (breach). Opera sempre dentro do tenant ambiente (via query filter do
/// AegisScoreDbContext), então usuário e tokens são naturalmente isolados por tenant.
/// </summary>
public interface IAuthService
{
    /// <summary>Valida credenciais e emite um novo par de tokens. <c>null</c> = credenciais inválidas.</summary>
    Task<TokenPair?> LoginAsync(string email, string password, CancellationToken ct);

    /// <summary>
    /// Rotaciona um refresh token válido de forma ATÔMICA, emitindo um novo par e revogando o anterior.
    /// <c>null</c> = token inválido/expirado. Reapresentar o mesmo token dentro da janela de idempotência
    /// devolve o sucessor já emitido (retry benigno). Fora da janela, a reutilização de um token já
    /// revogado é breach: revoga a CADEIA (família) daquele token e retorna <c>null</c>.
    /// </summary>
    Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>Revoga um refresh token específico (logout). Idempotente.</summary>
    Task LogoutAsync(string refreshToken, CancellationToken ct);
}

/// <summary>Geração dos tokens: JWT de acesso (HS256) + refresh token opaco. Sem estado.</summary>
public interface IJwtTokenService
{
    /// <summary>JWT curto com as claims obrigatórias <c>sub</c> (UserId) e <c>tenant_id</c> (TenantId).</summary>
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user);

    /// <summary>Refresh token opaco de alta entropia (256 bits) + sua expiração.</summary>
    (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken();
}

/// <summary>Hashing de senha (algoritmo encapsulado). Verificação em tempo ~constante.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
