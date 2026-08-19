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
/// [AEGIS-AUD-009] Desfecho EXPLÍCITO de um refresh. Antes o serviço devolvia <c>TokenPair?</c> e o
/// <c>null</c> confundia dois casos opostos: sessão morta (breach/expirado) e corrida benigna de rotação.
/// Com só o hash no banco, o servidor não consegue mais reconstruir o sucessor bruto para reentregá-lo ao
/// request perdedor da janela de idempotência — então o perdedor precisa ser sinalizado a tentar de novo,
/// e não deslogado. Os três casos passam a ser distinguíveis pelo chamador (a controller).
/// </summary>
public enum RefreshOutcome
{
    /// <summary>Rotação concluída: <see cref="RefreshResult.Pair"/> traz o novo par (200).</summary>
    Success,

    /// <summary>Token desconhecido, expirado ou reutilização fora da janela (breach): limpa o cookie e 401.</summary>
    InvalidOrBreach,

    /// <summary>
    /// Corrida benigna de rotação DENTRO da janela de idempotência: outra requisição já venceu e recebeu
    /// o sucessor. NÃO é breach — não revoga a cadeia nem limpa o cookie. O cliente reenvia uma única vez,
    /// após um atraso curto, com o cookie sucessor já atualizado pela resposta vencedora (retryable / 409).
    /// </summary>
    RotationConflict,
}

/// <summary>Resultado tipado de <see cref="IAuthService.RefreshAsync"/> — nunca expõe o token bruto senão no sucesso.</summary>
public sealed record RefreshResult(RefreshOutcome Outcome, TokenPair? Pair)
{
    public static RefreshResult Success(TokenPair pair) => new(RefreshOutcome.Success, pair);
    public static readonly RefreshResult InvalidOrBreach = new(RefreshOutcome.InvalidOrBreach, null);
    public static readonly RefreshResult RotationConflict = new(RefreshOutcome.RotationConflict, null);
}

/// <summary>Um ambiente acessível pela pessoa autenticada, para o seletor do HUD.</summary>
public record TenantMembershipDescriptor(Guid Id, string Name, string Slug, TenantRole Role);

/// <summary>
/// [AEGIS-AUD-012] Desfecho EXPLÍCITO da autenticação (login local ou troca federada). Antes o serviço
/// devolvia <c>TokenPair?</c> e escolhia SILENCIOSAMENTE o primeiro membership do banco quando a pessoa
/// tinha vários acessos — um usuário podia entrar no cliente errado sem perceber. Agora a seleção do
/// tenant é uma decisão de primeira classe, distinguível pelo chamador.
/// </summary>
public enum LoginOutcome
{
    /// <summary>Credenciais inválidas OU nenhum acesso ativo. O controller responde 401 genérico (não revela qual).</summary>
    Denied,

    /// <summary>Sessão emitida (<see cref="LoginResult.Pair"/>): exatamente um acesso, ou o último tenant revalidado.</summary>
    Authenticated,

    /// <summary>
    /// Vários acessos e nenhum último tenant válido: exige ESCOLHA explícita. O serviço entrega os ambientes
    /// (<see cref="LoginResult.Tenants"/>) e um ticket curto e purpose-bound (<see cref="LoginResult.SelectionTicket"/>)
    /// que só serve para concluir a seleção em <see cref="IAuthService.SelectTenantAsync"/> — NÃO é uma sessão.
    /// </summary>
    SelectionRequired,
}

/// <summary>Resultado tipado do login/troca federada. Só expõe o par (ou o ticket) no desfecho correspondente.</summary>
public sealed record LoginResult(
    LoginOutcome Outcome,
    TokenPair? Pair = null,
    string? SelectionTicket = null,
    DateTimeOffset? SelectionTicketExpiresAt = null,
    IReadOnlyList<TenantMembershipDescriptor>? Tenants = null)
{
    public static readonly LoginResult Denied = new(LoginOutcome.Denied);

    public static LoginResult Authenticated(TokenPair pair) => new(LoginOutcome.Authenticated, pair);

    public static LoginResult SelectionRequired(
        string ticket, DateTimeOffset ticketExpiresAt, IReadOnlyList<TenantMembershipDescriptor> tenants) =>
        new(LoginOutcome.SelectionRequired,
            SelectionTicket: ticket, SelectionTicketExpiresAt: ticketExpiresAt, Tenants: tenants);
}

/// <summary>
/// [AEGIS-AUD-007] Identidade corporativa JÁ VALIDADA criptograficamente pelo esquema JWT Bearer do Entra.
/// A camada de aplicação recebe apenas as claims do principal validado — <c>tid</c>, <c>oid</c> e o e-mail
/// (para o PRIMEIRO vínculo). O serviço NUNCA confia em identidade vinda de corpo JSON do cliente.
/// </summary>
public record FederatedIdentity(string? TenantId, string? ObjectId, string? Email);

/// <summary>Desfecho da troca da PRÓPRIA senha.</summary>
public enum PasswordChangeStatus
{
    /// <summary>Senha trocada; todas as sessões da identidade revogadas.</summary>
    Changed = 0,

    /// <summary>Senha atual não confere (400).</summary>
    InvalidCurrentPassword = 1,

    /// <summary>Conta federated-only (sem senha local): a credencial é do provedor corporativo (409).</summary>
    NoLocalCredential = 2,

    /// <summary>Nova senha fora da política de comprimento (400).</summary>
    WeakPassword = 3,

    /// <summary>Conta não encontrada — não deveria ocorrer numa sessão válida (404).</summary>
    NotFound = 4,
}

/// <summary>Resultado tipado da troca de senha. NUNCA carrega senha nem hash.</summary>
public sealed record PasswordChangeResult(PasswordChangeStatus Status, string? Detail = null)
{
    public bool Succeeded => Status is PasswordChangeStatus.Changed;
    public static readonly PasswordChangeResult Changed = new(PasswordChangeStatus.Changed);
    public static PasswordChangeResult Rejected(PasswordChangeStatus status, string? detail = null) =>
        new(status, detail);
}

/// <summary>
/// Desfecho da redefinição ADMINISTRATIVA de senha — um <c>PlatformAdmin</c> redefine a credencial local de
/// OUTRA identidade que não consegue mais autenticar. Distinta da troca da PRÓPRIA senha: não há senha atual
/// (o alvo, por definição, a perdeu), então a autoridade vem exclusivamente da policy de plataforma.
/// </summary>
public enum AdminPasswordResetStatus
{
    /// <summary>Senha redefinida; todas as sessões da identidade-alvo revogadas em TODOS os tenants.</summary>
    Reset = 0,

    /// <summary>Identidade-alvo inexistente — resposta GENÉRICA (404), sem revelar se o e-mail existe.</summary>
    NotFound = 1,

    /// <summary>
    /// Conta federated-only (sem hash local): a credencial é do provedor corporativo. NÃO se cria uma
    /// credencial local por esta rota — seria fabricar um segundo fator onde só deveria existir o Entra (409).
    /// </summary>
    NoLocalCredential = 2,

    /// <summary>Nova senha fora da política de comprimento — nada é alterado (400).</summary>
    WeakPassword = 3,

    /// <summary>
    /// O ator tentou redefinir a PRÓPRIA senha por esta rota administrativa (409). A redefinição administrativa
    /// não exige a senha atual; permiti-la sobre si mesmo transformaria a rota num bypass da troca normal (que
    /// EXIGE a senha atual). O administrador deve usar a troca comum em <c>POST /api/v1/auth/password</c>.
    /// </summary>
    SelfResetForbidden = 4,
}

/// <summary>
/// Resultado tipado da redefinição administrativa. NUNCA carrega senha nem hash. Em sucesso, expõe SOMENTE a
/// quantidade de ambientes cujas sessões foram revogadas — o dado auditável, sem nada sensível.
/// </summary>
public sealed record AdminPasswordResetResult(
    AdminPasswordResetStatus Status, int AffectedEnvironments = 0, string? Detail = null)
{
    public bool Succeeded => Status is AdminPasswordResetStatus.Reset;
    public static AdminPasswordResetResult Reset(int affectedEnvironments) =>
        new(AdminPasswordResetStatus.Reset, affectedEnvironments);
    public static AdminPasswordResetResult Rejected(AdminPasswordResetStatus status, string? detail = null) =>
        new(status, 0, detail);
}

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
    /// [AEGIS-AUD-012] Valida credenciais contra a <see cref="IdentityAccount"/> (e-mail único global) e
    /// RESOLVE o ambiente da pessoa sem jamais escolher em silêncio:
    ///  - <see cref="LoginOutcome.Denied"/>: credenciais inválidas ou nenhum acesso ativo;
    ///  - <see cref="LoginOutcome.Authenticated"/>: exatamente um acesso, OU vários com um
    ///    <paramref name="lastTenantId"/> que REVALIDA agora (membership ativo, tenant não suspenso);
    ///  - <see cref="LoginOutcome.SelectionRequired"/>: vários acessos e nenhum último tenant válido —
    ///    exige escolha explícita via <see cref="SelectTenantAsync"/>.
    /// O <paramref name="lastTenantId"/> é uma DICA do cliente; nunca é confiada sem revalidação no backend.
    /// </summary>
    Task<LoginResult> LoginAsync(string email, string password, Guid? lastTenantId, CancellationToken ct);

    /// <summary>
    /// [AEGIS-AUD-012] Conclui a seleção INICIAL de ambiente após um <see cref="LoginOutcome.SelectionRequired"/>.
    /// Autoriza-se pelo <paramref name="selectionTicket"/> curto e purpose-bound emitido no login (que carrega
    /// só a identidade, sem tenant nem papel), revalida o membership ATIVO no alvo e emite o par carimbado.
    /// <c>null</c> = ticket inválido/expirado, sem acesso ativo ao alvo, ou tenant suspenso.
    /// </summary>
    Task<TokenPair?> SelectTenantAsync(string selectionTicket, Guid targetTenantId, CancellationToken ct);

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
    /// O desfecho é explícito (<see cref="RefreshResult"/>):
    ///  - <see cref="RefreshOutcome.Success"/>: novo par emitido;
    ///  - <see cref="RefreshOutcome.InvalidOrBreach"/>: token desconhecido/expirado, ou reutilização fora da
    ///    janela de idempotência (breach) — que revoga a CADEIA (família) daquele token;
    ///  - <see cref="RefreshOutcome.RotationConflict"/>: reapresentação dentro da janela, quando outra
    ///    requisição já venceu a rotação. Como só o hash do sucessor é persistido, o bruto não pode ser
    ///    reentregue aqui — o chamador deve sinalizar retry (não é breach, não revoga, não limpa cookie).
    /// </summary>
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>Revoga um refresh token específico (logout). Idempotente.</summary>
    Task LogoutAsync(string refreshToken, CancellationToken ct);

    /// <summary>
    /// [AEGIS-AUD-007] Troca uma identidade corporativa JÁ VALIDADA (Entra) por uma sessão local: resolve a
    /// <see cref="IdentityAccount"/> vinculada por <c>tid+oid</c> (ou, no PRIMEIRO login, vincula uma conta
    /// preexistente com e-mail correspondente e ainda não vinculada) e emite o par local — access token +
    /// refresh HttpOnly — pelo mesmo caminho do login. <c>null</c> = negado (tid/oid inválidos, tid fora do
    /// permitido, sem conta correspondente, conta já vinculada a outro oid, ou sem membership ativo).
    /// NUNCA cria conta, membership, tenant ou papel (provisionamento é o AUD-010).
    ///
    /// [AEGIS-AUD-012] Assim como o login local, resolve o ambiente sem escolher em silêncio: devolve um
    /// <see cref="LoginResult"/> (autenticado quando há um único acesso ou o <paramref name="lastTenantId"/>
    /// revalida; caso contrário, seleção explícita). O vínculo Entra do primeiro login só é gravado quando há
    /// ao menos um acesso ativo — uma federação negada não deixa efeito colateral no banco.
    /// </summary>
    Task<LoginResult> ExchangeFederatedAsync(FederatedIdentity identity, Guid? lastTenantId, CancellationToken ct);

    /// <summary>
    /// Troca a PRÓPRIA senha da identidade autenticada. Estritamente ancorada no <paramref name="accountId"/>
    /// (claim <c>account_id</c>), NUNCA num e-mail ou id do corpo. Exige a senha atual; a nova segue a MESMA
    /// política NIST. Após a troca, REVOGA todas as sessões (refresh tokens) da identidade em TODOS os
    /// tenants — é a exceção autorizada de <c>IgnoreQueryFilters</c> sobre identidade, aqui, na camada de
    /// autenticação. O access token JÁ emitido conserva seu teto de 10 min (não o encurtamos). Uma conta
    /// federated-only (sem hash) devolve <see cref="PasswordChangeStatus.NoLocalCredential"/>. Nunca registra
    /// nem devolve senha/hash.
    /// </summary>
    Task<PasswordChangeResult> ChangeOwnPasswordAsync(
        Guid accountId, string currentPassword, string newPassword, CancellationToken ct);

    /// <summary>
    /// Redefinição ADMINISTRATIVA de senha local: um <c>PlatformAdmin</c> restabelece a credencial de OUTRA
    /// identidade que não consegue mais autenticar (recuperação legítima, sem e-mail/SMTP). O alvo é a
    /// <see cref="IdentityAccount"/> (<paramref name="targetAccountId"/>), NUNCA um membership — o modelo de
    /// identidade global é preservado. Diferente da troca da própria senha, NÃO exige a senha atual; por isso
    /// a autoridade vem só da policy de plataforma na borda, e esta operação recusa a AUTO-redefinição
    /// (<paramref name="actorAccountId"/> == alvo → <see cref="AdminPasswordResetStatus.SelfResetForbidden"/>),
    /// para não virar um bypass da troca normal.
    ///
    /// A nova senha segue a MESMA <c>PasswordPolicy</c> (NIST). Em sucesso, substitui o hash e revoga
    /// ATOMICAMENTE todos os refresh tokens da identidade em TODOS os tenants — reutilizando o MESMO núcleo
    /// sensível da troca da própria senha (sem duplicar a régua de atomicidade/revogação). Uma conta
    /// federated-only (sem hash) devolve <see cref="AdminPasswordResetStatus.NoLocalCredential"/> e NÃO ganha
    /// credencial local. Se a revogação falhar, a transação reverte e o hash NOVO não é persistido (nunca um
    /// estado parcial). Nunca registra, devolve nem inclui em exceção a senha ou o hash — a auditoria carrega
    /// apenas <paramref name="actorAccountId"/>, <paramref name="targetAccountId"/> e a contagem de ambientes.
    /// </summary>
    Task<AdminPasswordResetResult> AdminResetPasswordAsync(
        Guid actorAccountId, Guid targetAccountId, string newPassword, CancellationToken ct);
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

    /// <summary>
    /// [AEGIS-AUD-012] Ticket CURTO e purpose-bound para concluir a seleção inicial de tenant. Carrega só a
    /// identidade (<c>account_id</c>) e um propósito próprio, NUNCA tenant/papel, e usa uma audience distinta —
    /// então o esquema Bearer padrão o rejeita: não é uma sessão, só autoriza <see cref="IAuthService.SelectTenantAsync"/>.
    /// </summary>
    (string Token, DateTimeOffset ExpiresAt) CreateTenantSelectionTicket(Guid accountId);

    /// <summary>
    /// [AEGIS-AUD-012] Valida um ticket de seleção (assinatura/issuer/audience própria/lifetime + propósito) e
    /// extrai o <paramref name="accountId"/>. Fail-closed: qualquer inconsistência devolve <c>false</c>.
    /// </summary>
    bool TryReadTenantSelectionTicket(string ticket, out Guid accountId);
}

/// <summary>Hashing de senha (algoritmo encapsulado). Verificação em tempo ~constante.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>
/// [AEGIS-AUD-009] Hash determinístico do refresh token, centralizado num único componente (não espalhar
/// SHA-256 pelo serviço). SHA-256 — e NÃO PBKDF2/bcrypt: o refresh token é um segredo aleatório de 256
/// bits, não uma senha de baixa entropia; ele precisa de lookup indexado determinístico e não tem margem
/// para brute force que justifique um KDF caro. Sem pepper/HMAC de propósito: um segredo adicional exigiria
/// gestão e rotação de chave (escopo do AEGIS-AUD-060), e ampliaria este pacote.
/// </summary>
public interface IRefreshTokenHasher
{
    /// <summary>SHA-256 do token bruto em hexadecimal minúsculo de 64 caracteres. Determinístico.</summary>
    string Hash(string rawToken);
}
