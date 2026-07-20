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
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly IJwtTokenService _tokens;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<AuthService> _logger;

    // Hash válido e de mesmo custo, usado para verificar a senha mesmo quando o usuário não existe —
    // evita revelar (por timing) se um e-mail está ou não cadastrado no tenant.
    private static readonly string DummyHash = new Pbkdf2PasswordHasher().Hash("aegis-timing-guard");

    public AuthService(
        AegisScoreDbContext db,
        DbContextOptions<AegisScoreDbContext> options,
        IJwtTokenService tokens,
        IPasswordHasher hasher,
        ILogger<AuthService> logger)
    {
        _db = db;
        _options = options;
        _tokens = tokens;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<TokenPair?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();

        // 1) A CREDENCIAL é da pessoa, não do vínculo. IdentityAccount é referência global (sem query
        //    filter), então esta leitura não precisa de exceção alguma — e o e-mail é único global.
        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Email == normalized, ct);

        // Verifica a senha SEMPRE (mesmo sem conta) para não vazar existência do e-mail por timing.
        var ok = _hasher.Verify(password ?? "", account?.PasswordHash ?? DummyHash);
        if (account is null || !ok)
            return null;

        // 2) Só DEPOIS de a credencial provar quem é a pessoa, resolvemos os ambientes dela.
        //    IgnoreQueryFilters é indispensável aqui: no login ainda não existe tenant ambiente (o
        //    analista informou apenas e-mail e senha), então o filtro devolveria zero linhas. A leitura
        //    é ancorada no IdentityAccountId JÁ AUTENTICADO — não em e-mail nem em nada vindo do cliente.
        var membership = await FirstActiveMembershipAsync(account.Id, ct);
        if (membership is null)
            return null;   // pessoa sem nenhum acesso ativo: credencial válida não basta

        return await IssuePairAsync(membership, account, ct);
    }

    public async Task<IReadOnlyList<TenantMembershipDescriptor>> GetAccessibleTenantsAsync(
        Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return Array.Empty<TenantMembershipDescriptor>();

        // Escopo da exceção: memberships DESTA conta, cruzados com o tenant para nome/slug. O join é
        // sobre Tenants (que não tem query filter — não é ITenantOwned), então só o lado User precisa
        // atravessar o filtro. Somente ambientes ATIVOS e não suspensos entram no seletor.
        // ⚠️ Projeta num tipo ANÔNIMO no SQL e só depois monta o record em memória. Projetar direto no
        // `TenantMembershipDescriptor` dentro do Join fazia o EF 8 desistir da tradução
        // ("The LINQ expression could not be translated") e cair em 500 — pego no smoke test ao vivo,
        // não pelos testes. Query syntax + tipo anônimo é a forma que o provider traduz.
        var rows = await (
            from u in _db.Users.IgnoreQueryFilters()
            join t in _db.Tenants on u.TenantId equals t.Id
            where u.IdentityAccountId == accountId && u.IsActive && t.Status != TenantStatus.Suspended
            orderby t.Name
            select new { t.Id, t.Name, t.Slug, u.Role }).ToListAsync(ct);

        return rows
            .Select(r => new TenantMembershipDescriptor(r.Id, r.Name, r.Slug, r.Role))
            .ToList();
    }

    public async Task<TokenPair?> SwitchTenantAsync(
        Guid accountId, Guid targetTenantId, string? currentRefreshToken, CancellationToken ct)
    {
        if (accountId == Guid.Empty || targetTenantId == Guid.Empty)
            return null;

        // A AUTORIZAÇÃO da troca: a pessoa do token precisa ter membership ATIVO no alvo. O predicado
        // casa por IdentityAccountId — chave estrangeira, não string —, então não há como um acesso
        // criado noutro cliente com o "mesmo e-mail" habilitar esta troca.
        var target = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                u => u.IdentityAccountId == accountId && u.TenantId == targetTenantId && u.IsActive, ct);
        if (target is null)
            return null;

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == targetTenantId, ct);
        if (tenant is null || tenant.Status == TenantStatus.Suspended)
            return null;

        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
            return null;

        // A sessão do ambiente anterior NÃO sobrevive à troca: revoga o refresh corrente antes de emitir
        // o novo. Sem isto, o cliente ficaria com dois refresh vivos de tenants distintos, e um replay do
        // antigo reabriria o ambiente que o usuário acredita ter deixado. Idempotente e atômico.
        if (!string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            // Entidade rastreada em vez de ExecuteUpdate, pelo mesmo motivo do IssuePairAsync: o update
            // em lote não traduz sob IgnoreQueryFilters. Idempotente — só revoga o que ainda está ativo.
            var atual = await _db.UserRefreshTokens.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Token == currentRefreshToken && t.RevokedAt == null, ct);
            if (atual is not null)
            {
                atual.RevokedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }

        var pair = await IssuePairAsync(target, account, ct);

        _logger.LogInformation(
            "Troca de ambiente: conta {AccountId} assumiu o tenant {TenantId} como {Role}.",
            accountId, targetTenantId, target.Role);

        return pair;
    }

    /// <summary>
    /// Primeiro membership ATIVO da pessoa, em ordem ESTÁVEL. A ordenação não é cosmética: sem ela, o
    /// "primeiro ambiente" dependeria do plano de execução do Postgres e o usuário cairia em clientes
    /// diferentes a cada login. Critério: o acesso mais antigo (o "ambiente de origem" da pessoa).
    /// </summary>
    private async Task<User?> FirstActiveMembershipAsync(Guid accountId, CancellationToken ct)
    {
        var candidatos = await (
            from u in _db.Users.IgnoreQueryFilters()
            join t in _db.Tenants on u.TenantId equals t.Id
            where u.IdentityAccountId == accountId && u.IsActive && t.Status != TenantStatus.Suspended
            select u).ToListAsync(ct);

        // ⚠️ Ordenação em MEMÓRIA de propósito: o SQLite (provider da suíte de testes) não ordena por
        // DateTimeOffset, então um ORDER BY no servidor deixaria o login sem cobertura possível. O
        // custo é nulo — uma pessoa tem um punhado de acessos, não uma tabela.
        return candidatos
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .FirstOrDefault();
    }

    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return null;

        // Sonda ancorada no SEGREDO, não no tenant ambiente. O refresh token É a credencial (256 bits)
        // e carrega o próprio tenant, então o "silent refresh" do bootstrap funciona sem o cliente saber
        // em que ambiente está — requisito direto do login sem slug. IgnoreQueryFilters aqui é a mesma
        // exceção estrita autorizada para a camada de identidade.
        var probe = await _db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Token == refreshToken, ct);
        if (probe is null)
            return null;   // token desconhecido

        // Daqui em diante operamos DENTRO do tenant que o próprio token declara: o StampTenant segue
        // rígido, apenas deixa de ser alimentado com um contexto que não é o desta escrita.
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(probe.TenantId));

        var stored = await db.UserRefreshTokens.FirstOrDefaultAsync(t => t.Token == refreshToken, ct);
        if (stored is null)
            return null;

        // [Crítico 2] Expiração ANTES de reuso: um token expirado replayado só retorna 401 e NUNCA
        // dispara a cascata de revogação — fecha o DoS por replay de token ancião.
        if (stored.IsExpired)
            return null;

        // Já revogado quando lido = rotacionado por outra request. Janela de idempotência ou breach.
        if (stored.IsRevoked)
            return await HandleAlreadyRotatedAsync(db, stored, ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        var account = user is null
            ? null
            : await db.IdentityAccounts.FirstOrDefaultAsync(a => a.Id == user.IdentityAccountId, ct);

        if (user is null || account is null || !user.IsActive)
        {
            // Órfão/desativado: encerra a cadeia de forma atômica (não dispara breach).
            await db.UserRefreshTokens
                .Where(t => t.Id == stored.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => DateTimeOffset.UtcNow), ct);
            return null;
        }

        // [Crítico 1] Rotação ATÔMICA. O UPDATE ... WHERE RevokedAt IS NULL é a seção crítica: sob
        // concorrência, apenas UMA request afeta 1 linha; as demais afetam 0 e "perdem a corrida".
        // Elimina o fork de cadeia (dois filhos ativos do mesmo pai) que cegava a detecção de breach.
        var (newRefresh, newRefreshExp) = _tokens.CreateRefreshToken();
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var claimed = await db.UserRefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, _ => now)
                .SetProperty(t => t.ReplacedByToken, _ => newRefresh), ct);

        if (claimed == 0)
        {
            // Perdi a corrida entre o SELECT e o UPDATE. Desfaz e trata como já-rotacionado, lendo o
            // estado do vencedor — cai na janela de idempotência (retry benigno) ou em breach.
            await tx.RollbackAsync(ct);
            var latest = await db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == stored.Id, ct);
            return latest is null ? null : await HandleAlreadyRotatedAsync(db, latest, ct);
        }

        // Venci a corrida: emito o novo filho. ITenantOwned → StampTenant carimba/valida no SaveChanges.
        db.UserRefreshTokens.Add(new UserRefreshToken
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Token = newRefresh,
            ExpiresAt = newRefreshExp,
        });
        var (access, accessExp) = _tokens.CreateAccessToken(user, account);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new TokenPair(access, accessExp, newRefresh, newRefreshExp);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        // Revoga de forma atômica, só se ainda ativo. Idempotente. IgnoreQueryFilters porque o logout
        // pode chegar sem tenant ambiente (token de acesso já expirado) — e o segredo apresentado é a
        // própria autorização para revogá-lo.
        await _db.UserRefreshTokens.IgnoreQueryFilters()
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
    private async Task<TokenPair?> HandleAlreadyRotatedAsync(
        AegisScoreDbContext db, UserRefreshToken parent, CancellationToken ct)
    {
        if (parent.RevokedAt is { } revokedAt
            && DateTimeOffset.UtcNow - revokedAt <= IdempotencyWindow
            && !string.IsNullOrEmpty(parent.ReplacedByToken))
        {
            var successor = await db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == parent.ReplacedByToken, ct);

            if (successor is not null && successor.IsActive)
            {
                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == successor.UserId, ct);
                var account = user is null
                    ? null
                    : await db.IdentityAccounts.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.Id == user.IdentityAccountId, ct);

                if (user is not null && account is not null && user.IsActive)
                {
                    // Idempotente: mesmo par entregue ao líder (novo access + refresh = sucessor).
                    var (access, accessExp) = _tokens.CreateAccessToken(user, account);
                    return new TokenPair(access, accessExp, successor.Token, successor.ExpiresAt);
                }
            }
        }

        _logger.LogWarning(
            "SECURITY: reutilização de refresh token revogado fora da janela de idempotência " +
            "(possível roubo de sessão). Tenant={Tenant} User={User} TokenId={TokenId}. " +
            "Revogando a CADEIA do token.",
            parent.TenantId, parent.UserId, parent.Id);

        await RevokeChainAsync(db, parent, ct);
        return null;
    }

    /// <summary>
    /// Emite o par de tokens e persiste o refresh NO TENANT DO MEMBERSHIP.
    ///
    /// ⚠️ Por que um DbContext próprio: o login acontece sem tenant ambiente (o analista informou só
    /// e-mail e senha) e a TROCA acontece sob o tenant ANTIGO. Nos dois casos o <c>StampTenant</c>
    /// fail-closed recusaria a escrita — corretamente, porque o contexto da requisição não é o do
    /// destino. A saída NÃO é afrouxar o carimbo: é abrir um contexto ligado ao tenant certo, o mesmo
    /// padrão que os workers já usam (<see cref="SystemTenantContext"/>). O carimbo continua rígido;
    /// apenas deixamos de mentir para ele sobre qual é o tenant desta escrita.
    /// </summary>
    private async Task<TokenPair> IssuePairAsync(User membership, IdentityAccount account, CancellationToken ct)
    {
        var (access, accessExp) = _tokens.CreateAccessToken(membership, account);
        var (refresh, refreshExp) = _tokens.CreateRefreshToken();

        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(membership.TenantId));

        db.UserRefreshTokens.Add(new UserRefreshToken
        {
            TenantId = membership.TenantId,   // revalidado pelo StampTenant contra o contexto acima
            UserId = membership.Id,
            Token = refresh,
            ExpiresAt = refreshExp,
        });
        // LastLoginAt por entidade RASTREADA, não por ExecuteUpdate: o update em lote não traduz junto
        // com o Global Query Filter (`(Guid?)u.TenantId == __ef_filter__…`) e quebrava no SQLite dos
        // testes. Ler e alterar no tracker funciona em qualquer provider e ainda economiza um
        // round-trip — sai no MESMO SaveChanges do refresh token acima. Este `db` é um contexto
        // PRÓPRIO (ligado ao tenant de destino), então não há risco de dois trackers donos da mesma
        // linha: a instância do `_db` da requisição não é tocada aqui.
        var tracked = await db.Users.FirstOrDefaultAsync(u => u.Id == membership.Id, ct);
        if (tracked is not null)
            tracked.LastLoginAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return new TokenPair(access, accessExp, refresh, refreshExp);
    }

    /// <summary>
    /// [Crítico 2] Breach com blast radius reduzido: revoga apenas a CADEIA (família) do token,
    /// caminhando para frente via <see cref="UserRefreshToken.ReplacedByToken"/>. Outras sessões
    /// legítimas do mesmo usuário (outros dispositivos/navegadores) permanecem ativas.
    /// </summary>
    private static async Task RevokeChainAsync(
        AegisScoreDbContext db, UserRefreshToken start, CancellationToken ct)
    {
        var chain = new List<Guid> { start.Id };
        var nextToken = start.ReplacedByToken;
        var guard = 0;

        // Caminha a linhagem para frente; o guard evita laço infinito em dados legados bifurcados.
        while (!string.IsNullOrEmpty(nextToken) && guard++ < 256)
        {
            var link = nextToken;
            var node = await db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == link, ct);
            if (node is null)
                break;

            chain.Add(node.Id);
            nextToken = node.ReplacedByToken;
        }

        await db.UserRefreshTokens
            .Where(t => chain.Contains(t.Id) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => DateTimeOffset.UtcNow), ct);
    }
}
