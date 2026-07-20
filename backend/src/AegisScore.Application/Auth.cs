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

/// <summary>Um ambiente acessível pela pessoa autenticada, para o seletor do HUD.</summary>
public record TenantMembershipDescriptor(Guid Id, string Name, string Slug, UserRole Role);

/// <summary>
/// Serviço de autenticação: login por credenciais, listagem de ambientes, troca de ambiente e rotação
/// de refresh token (RTR) com detecção de reutilização (breach).
///
/// ⚠️ <b>Única camada autorizada a usar <c>IgnoreQueryFilters()</c> sobre identidade.</b> O login
/// acontece ANTES de existir tenant ambiente (o analista informa só e-mail e senha), então a busca do
/// membership precisa atravessar o filtro. O escopo dessa exceção é estrito: apenas
/// <see cref="User"/>/<see cref="IdentityAccount"/>, apenas aqui, e sempre ancorada na
/// <see cref="IdentityAccount"/> já autenticada. Nenhuma outra entidade do sistema é lida assim, e o
/// <c>TenantConsistencyMiddleware</c> segue barrando divergência token×header em todas as rotas.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Valida credenciais contra a <see cref="IdentityAccount"/> (e-mail único global) e emite o par de
    /// tokens para o PRIMEIRO membership ativo da pessoa. <c>null</c> = credenciais inválidas, conta sem
    /// nenhum acesso ativo, ou tenant suspenso.
    /// </summary>
    Task<TokenPair?> LoginAsync(string email, string password, CancellationToken ct);

    /// <summary>
    /// Ambientes acessíveis pela pessoa (memberships ATIVOS), para o seletor do HUD. Ancorado no
    /// <c>accountId</c> vindo da claim <c>account_id</c> do token — nunca num e-mail do corpo.
    /// </summary>
    Task<IReadOnlyList<TenantMembershipDescriptor>> GetAccessibleTenantsAsync(
        Guid accountId, CancellationToken ct);

    /// <summary>
    /// Troca o ambiente ativo: confirma que a pessoa (<paramref name="accountId"/>) tem membership ATIVO
    /// em <paramref name="targetTenantId"/> e emite um par NOVO, carimbado com o tenant e o papel de lá.
    /// O refresh token atual é REVOGADO na troca — a sessão anterior não sobrevive à mudança de contexto.
    /// <c>null</c> = a pessoa não tem acesso ativo ao alvo (ou o tenant está suspenso).
    /// </summary>
    Task<TokenPair?> SwitchTenantAsync(
        Guid accountId, Guid targetTenantId, string? currentRefreshToken, CancellationToken ct);

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
    /// <summary>
    /// JWT curto do MEMBERSHIP: claims obrigatórias <c>sub</c> (UserId do membership),
    /// <c>tenant_id</c> (o ambiente ativo) e <c>account_id</c> (a pessoa global).
    ///
    /// Recebe os dois lados porque a credencial e o acesso vivem em entidades distintas desde a
    /// normalização: o e-mail vem da <paramref name="account"/>, o papel e o tenant do
    /// <paramref name="membership"/>. O <c>account_id</c> é o que permite validar uma troca de ambiente
    /// sem casar por string de e-mail.
    /// </summary>
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User membership, IdentityAccount account);

    /// <summary>Refresh token opaco de alta entropia (256 bits) + sua expiração.</summary>
    (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken();
}

/// <summary>Hashing de senha (algoritmo encapsulado). Verificação em tempo ~constante.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
