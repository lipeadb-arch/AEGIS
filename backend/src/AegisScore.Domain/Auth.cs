using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>
/// A PESSOA — identidade global do MSSP, dona da credencial. É referência GLOBAL: NÃO é
/// <see cref="ITenantOwned"/>, não tem query filter e não é carimbada. O e-mail é único no sistema
/// inteiro (não por tenant), porque num MSSP o e-mail corporativo representa a mesma pessoa física
/// através de todos os clientes.
///
/// ⚠️ Existe para que o vínculo pessoa↔tenant seja AUTENTICADO por chave estrangeira, e não por
/// coincidência de string. Antes, <see cref="User"/> guardava e-mail + hash próprios por tenant: um
/// admin de qualquer cliente podia criar a linha <c>ceo@bancoX.com</c> no PRÓPRIO tenant com uma senha
/// que ele mesmo escolhia, e qualquer fluxo que casasse "tenants deste e-mail" entregaria a ele um
/// token do banco X. Com a credencial ÚNICA e global, quem não sabe a senha da pessoa não alcança
/// nenhum ambiente dela.
/// </summary>
public class IdentityAccount : Entity
{
    /// <summary>Login. Único GLOBAL. Persistido normalizado (minúsculas).</summary>
    public string Email { get; set; } = "";

    /// <summary>Hash PBKDF2 no formato <c>iterações.salt.hash</c>. Nunca a senha em claro.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Os ambientes a que esta pessoa tem acesso (um <see cref="User"/> por tenant).</summary>
    public ICollection<User> Memberships { get; set; } = new List<User>();
}

/// <summary>
/// O MEMBERSHIP: o acesso de uma <see cref="IdentityAccount"/> a UM tenant, com o papel que ela exerce
/// ALI. Continua <see cref="ITenantOwned"/> com um único <c>TenantId</c> — o query filter e o stamping
/// fail-closed do DbContext seguem intactos, e é isso que preserva o isolamento das demais rotas.
///
/// Não carrega mais e-mail nem senha: a credencial é da pessoa, não do vínculo. Duplicá-la por tenant
/// convidava à dessincronização (mesma pessoa com senhas divergentes por cliente) e era a raiz do vetor
/// descrito em <see cref="IdentityAccount"/>. O que É por tenant permanece aqui: papel, ativação,
/// nome de exibição e último login.
/// </summary>
public class User : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>A pessoa dona deste acesso. É o vínculo autenticado — nunca casar por e-mail.</summary>
    public Guid IdentityAccountId { get; set; }
    public IdentityAccount? Account { get; set; }

    /// <summary>Nome exibido NESTE cliente (a mesma pessoa pode se apresentar diferente em cada um).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Papel exercido NESTE tenant. A troca de ambiente reemite o token com o papel de lá.</summary>
    public UserRole Role { get; set; } = UserRole.Analyst;

    /// <summary>Desativado ≠ deletado: membership inativo não autentica nem aparece no seletor (fail-closed).</summary>
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
