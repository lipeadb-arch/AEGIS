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

    /// <summary>
    /// Hash PBKDF2 no formato <c>iterações.salt.hash</c>. Nunca a senha em claro.
    ///
    /// [AEGIS-AUD-010] NULLABLE: uma conta <b>federated-only</b> (provisionada por PlatformAdmin sem senha
    /// local, para autenticar exclusivamente pelo Entra ID) existe SEM credencial local. Ausência de senha
    /// é <c>null</c> — nunca string vazia nem hash fictício. O fluxo de login Local
    /// (<c>AuthService.LoginAsync</c>) jamais autentica uma conta sem hash, e mantém o dummy hash para não
    /// vazar a existência/estado da conta por timing. Contas locais/híbridas seguem com hash preenchido.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// [AEGIS-AUD-007] Vínculo com a identidade corporativa (Microsoft Entra ID), por identificadores
    /// IMUTÁVEIS: <c>tid</c> (tenant/diretório) e <c>oid</c> (objeto do usuário). Nullable — contas locais
    /// (dev/demonstração) não têm vínculo. Uma vez ligada, a pessoa é localizada por <c>tid+oid</c>, nunca
    /// por e-mail: trocar o e-mail no Entra não quebra o login. O índice único composto (parcial, só linhas
    /// vinculadas) impede que a MESMA identidade externa seja ligada a duas contas.
    /// </summary>
    public string? ExternalTenantId { get; set; }

    /// <summary>Object ID (<c>oid</c>) estável da pessoa no Entra. Ver <see cref="ExternalTenantId"/>.</summary>
    public string? ExternalObjectId { get; set; }

    /// <summary>
    /// [AEGIS-AUD-011] Autoridade GLOBAL de plataforma da PESSOA — deliberadamente separada do papel
    /// tenant-scoped (<see cref="User.Role"/>). Antes o privilégio de plataforma era um valor do enum
    /// de membership (<c>PlatformAdmin</c>), então autoridade global dependia do tenant ativo e trafegava
    /// na mesma claim <c>role</c>. Aqui a autoridade global mora na identidade (não no membership), é
    /// invariável à troca de tenant e viaja numa claim própria (<c>platform_role</c>). Padrão
    /// <see cref="PlatformRole.None"/>: quase toda identidade é apenas um usuário de tenant; o
    /// <c>PlatformAdmin</c> é provisionado fora do self-service (nunca por concessão de membership).
    /// </summary>
    public PlatformRole PlatformRole { get; set; } = PlatformRole.None;

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

    /// <summary>
    /// [AEGIS-AUD-011] Papel TENANT-SCOPED exercido NESTE tenant. A troca de ambiente reemite o token com o
    /// papel de lá. É estritamente <see cref="TenantRole"/> — o antigo <c>PlatformAdmin</c> (autoridade
    /// global) NÃO pode ser gravado aqui; ele vive em <see cref="IdentityAccount.PlatformRole"/>.
    /// </summary>
    public TenantRole Role { get; set; } = TenantRole.Analyst;

    /// <summary>Desativado ≠ deletado: membership inativo não autentica nem aparece no seletor (fail-closed).</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<UserRefreshToken> RefreshTokens { get; set; } = new List<UserRefreshToken>();
}

/// <summary>
/// [AEGIS-AUD-011] Papel TENANT-SCOPED — o que a pessoa exerce DENTRO de um tenant. É o tipo de
/// <see cref="User.Role"/> e o valor da claim <c>role</c> do tenant ativo. Os valores numéricos 0/1/2 são
/// preservados do antigo <c>UserRole</c> para compatibilidade dos dados existentes. <c>PlatformAdmin</c>
/// deliberadamente NÃO existe aqui: autoridade global não é atributo de membership (ver <see cref="PlatformRole"/>).
/// </summary>
public enum TenantRole { Analyst = 0, Manager = 1, TenantAdmin = 2 }

/// <summary>
/// [AEGIS-AUD-011] Autoridade GLOBAL de plataforma — atributo da <see cref="IdentityAccount"/> (a pessoa),
/// não de um membership. Opera cross-tenant (ex.: criar tenants, provisionar identidades) e viaja na claim
/// própria <c>platform_role</c>, invariável à troca de tenant. Provisionado fora do self-service; nenhuma
/// concessão de acesso a tenant a atribui.
/// </summary>
public enum PlatformRole { None = 0, PlatformAdmin = 1 }

/// <summary>
/// Refresh token persistido para Refresh Token Rotation (RTR). Cada token é de uso único:
/// ao ser trocado, é revogado e aponta para o sucessor (<see cref="ReplacedByTokenHash"/>), formando
/// uma cadeia auditável. A reutilização de um token já revogado é indício de comprometimento
/// (breach) e derruba toda a sessão do usuário.
/// ITenantOwned: herda o query filter e o stamping fail-closed do AegisScoreDbContext.
///
/// [AEGIS-AUD-009] O token bruto NUNCA é persistido: só o seu hash SHA-256 (determinístico) mora no
/// banco, indexado para lookup. O segredo de alta entropia existe apenas em trânsito — na geração, no
/// <c>TokenPair</c> interno e no cookie HttpOnly do cliente. Um banco comprometido entrega apenas
/// hashes, inúteis como credencial de sessão.
/// </summary>
public class UserRefreshToken : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// Hash SHA-256 (hex minúsculo, 64 chars) do refresh token bruto de 256 bits. É a chave de busca
    /// determinística — nunca o segredo em si. O bruto vai ao cliente só no cookie HttpOnly.
    /// </summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Preenchido quando o token é rotacionado ou revogado (logout / breach).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Hash SHA-256 do sucessor que substituiu este na rotação — trilha de auditoria da cadeia RTR.
    /// Guarda o hash do sucessor, jamais o sucessor bruto (que iria só ao vencedor da rotação).
    /// </summary>
    public string? ReplacedByTokenHash { get; set; }

    public User? User { get; set; }

    // ---- Estado derivado (nunca persistido; ignorado no DbContext) ----
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt is not null;
    public bool IsActive => !IsRevoked && !IsExpired;
}
