using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>
/// Usuário operacional de um tenant (analista, gestor, admin do cliente). Isolado por tenant:
/// o mesmo e-mail pode coexistir em tenants distintos sem colidir. Nunca guarda a senha em claro —
/// apenas o hash derivado (PBKDF2). Herda Id/CreatedAt/UpdatedAt de <see cref="Entity"/>.
/// </summary>
public class User : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Login do usuário, único por tenant. Persistido normalizado (minúsculas).</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Hash PBKDF2 no formato <c>iterações.salt.hash</c>. Nunca a senha em claro.</summary>
    public string PasswordHash { get; set; } = "";

    public UserRole Role { get; set; } = UserRole.Analyst;

    /// <summary>Desativado ≠ deletado: usuário inativo não autentica (fail-closed).</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<UserRefreshToken> RefreshTokens { get; set; } = new List<UserRefreshToken>();
}

/// <summary>
/// Papel do usuário. Analyst/Manager/TenantAdmin são papéis DENTRO de um tenant. PlatformAdmin é um
/// papel de PLATAFORMA (operações cross-tenant, ex.: criar tenants) — provisionado fora do onboarding
/// self-service e que nenhum usuário de tenant comum deve receber.
/// </summary>
public enum UserRole { Analyst = 0, Manager = 1, TenantAdmin = 2, PlatformAdmin = 3 }

/// <summary>
/// Refresh token persistido para Refresh Token Rotation (RTR). Cada token é de uso único:
/// ao ser trocado, é revogado e aponta para o sucessor (<see cref="ReplacedByToken"/>), formando
/// uma cadeia auditável. A reutilização de um token já revogado é indício de comprometimento
/// (breach) e derruba toda a sessão do usuário.
/// ITenantOwned: herda o query filter e o stamping fail-closed do AegisScoreDbContext.
/// </summary>
public class UserRefreshToken : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Segredo de alta entropia (256 bits, base64url). Vai ao cliente via cookie HttpOnly.</summary>
    public string Token { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Preenchido quando o token é rotacionado ou revogado (logout / breach).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Token que substituiu este na rotação — trilha de auditoria da cadeia RTR.</summary>
    public string? ReplacedByToken { get; set; }

    public User? User { get; set; }

    // ---- Estado derivado (nunca persistido; ignorado no DbContext) ----
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt is not null;
    public bool IsActive => !IsRevoked && !IsExpired;
}
