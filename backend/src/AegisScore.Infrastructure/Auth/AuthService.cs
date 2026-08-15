using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly IRefreshTokenHasher _refreshHasher;
    private readonly FederationOptions _federation;
    private readonly ILogger<AuthService> _logger;

    // Hash válido e de mesmo custo, usado para verificar a senha mesmo quando o usuário não existe —
    // evita revelar (por timing) se um e-mail está ou não cadastrado no tenant.
    private static readonly string DummyHash = new Pbkdf2PasswordHasher().Hash("aegis-timing-guard");

    public AuthService(
        AegisScoreDbContext db,
        DbContextOptions<AegisScoreDbContext> options,
        IJwtTokenService tokens,
        IPasswordHasher hasher,
        IRefreshTokenHasher refreshHasher,
        IOptions<FederationOptions> federation,
        ILogger<AuthService> logger)
    {
        _db = db;
        _options = options;
        _tokens = tokens;
        _hasher = hasher;
        _refreshHasher = refreshHasher;
        _federation = federation.Value;
        _logger = logger;
    }

    public async Task<LoginResult> LoginAsync(string email, string password, Guid? lastTenantId, CancellationToken ct)
    {
        // [AEGIS-AUD-007] Em modo Federated o login por senha fica DESABILITADO (defesa em profundidade:
        // o front já esconde o formulário pela config pública, mas o backend é a autoridade). Local e
        // Hybrid seguem aceitando credenciais.
        if (!_federation.PasswordLoginEnabled)
            return LoginResult.Denied;

        var normalized = (email ?? "").Trim().ToLowerInvariant();

        // 1) A CREDENCIAL é da pessoa, não do vínculo. IdentityAccount é referência global (sem query
        //    filter), então esta leitura não precisa de exceção alguma — e o e-mail é único global.
        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Email == normalized, ct);

        // [AEGIS-AUD-010] PasswordHash é nullable: uma conta federated-only não tem credencial local.
        // Verifica SEMPRE contra ALGUM hash (o dummy quando não há conta OU não há hash) para não vazar
        // por timing se o e-mail existe nem se a conta tem senha local. Uma conta sem hash NUNCA autentica
        // pelo fluxo Local — o teste explícito `storedHash is null` barra mesmo que o Verify passe por acaso
        // (ex.: senha == dummy). Só uma conta COM hash e senha correta segue adiante.
        var storedHash = account?.PasswordHash;
        var ok = _hasher.Verify(password ?? "", storedHash ?? DummyHash);
        if (account is null || storedHash is null || !ok)
            return LoginResult.Denied;

        // 2) [AEGIS-AUD-012] Só DEPOIS de a credencial provar quem é a pessoa, RESOLVEMOS o ambiente — sem
        //    nunca escolher o primeiro registro em silêncio. IgnoreQueryFilters (dentro de ValidMembershipsAsync)
        //    é indispensável: no login ainda não existe tenant ambiente, então o filtro devolveria zero linhas.
        //    A leitura é ancorada no IdentityAccountId JÁ AUTENTICADO — não em e-mail nem em nada do cliente.
        var memberships = await ValidMembershipsAsync(account.Id, ct);
        return await ResolveSessionAsync(account, memberships, lastTenantId, ct);
    }

    /// <summary>
    /// [AEGIS-AUD-012] Núcleo da SELEÇÃO de ambiente, compartilhado por login local e troca federada. A
    /// credencial (ou a identidade Entra) já provou quem é a pessoa; aqui decidimos QUAL ambiente:
    ///  - zero memberships válidos → recusa (credencial válida não basta);
    ///  - exatamente um → seleção automática;
    ///  - vários → usa o ÚLTIMO tenant só se ele revalidar AGORA; senão exige escolha explícita.
    /// Nunca escolhe o primeiro registro do banco: a ordem de <paramref name="memberships"/> só ordena a
    /// apresentação. O <paramref name="lastTenantId"/> é DICA do cliente, revalidada aqui contra os acessos.
    /// </summary>
    private async Task<LoginResult> ResolveSessionAsync(
        IdentityAccount account, List<MembershipRow> memberships, Guid? lastTenantId, CancellationToken ct)
    {
        if (memberships.Count == 0)
            return LoginResult.Denied;

        if (memberships.Count == 1)
            return LoginResult.Authenticated(await IssuePairAsync(memberships[0].User, account, ct));

        // Vários acessos: o último tenant só vale se AINDA houver membership ativo nele (já garantido por
        // ValidMembershipsAsync — ativo e tenant não suspenso). Sem dica, ou dica que não casa: escolha explícita.
        if (lastTenantId is { } hint && hint != Guid.Empty)
        {
            var remembered = memberships.FirstOrDefault(m => m.User.TenantId == hint);
            if (remembered is not null)
                return LoginResult.Authenticated(await IssuePairAsync(remembered.User, account, ct));
        }

        // Ticket curto, purpose-bound, ancorado na identidade — só conclui a seleção em SelectTenantAsync.
        var (ticket, ticketExp) = _tokens.CreateTenantSelectionTicket(account.Id);
        return LoginResult.SelectionRequired(ticket, ticketExp, ToDescriptors(memberships));
    }

    public async Task<IReadOnlyList<TenantMembershipDescriptor>> GetAccessibleTenantsAsync(
        Guid accountId, CancellationToken ct)
    {
        var memberships = await ValidMembershipsAsync(accountId, ct);
        return ToDescriptors(memberships);
    }

    public async Task<TokenPair?> SwitchTenantAsync(
        Guid accountId, Guid targetTenantId, string? currentRefreshToken, CancellationToken ct)
    {
        // A AUTORIZAÇÃO da troca (membership ativo no alvo, tenant não suspenso) é validada ANTES de tocar a
        // sessão atual: um switch negado não pode revogar o refresh vigente.
        var validated = await ValidateTargetAsync(accountId, targetTenantId, ct);
        if (validated is null)
            return null;
        var (target, account) = validated.Value;

        // A sessão do ambiente anterior NÃO sobrevive à troca: revoga o refresh corrente antes de emitir
        // o novo. Sem isto, o cliente ficaria com dois refresh vivos de tenants distintos, e um replay do
        // antigo reabriria o ambiente que o usuário acredita ter deixado. Idempotente e atômico.
        if (!string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            // Localiza pelo HASH do cookie recebido — nunca comparando uma coluna persistida com o bruto.
            var currentHash = _refreshHasher.Hash(currentRefreshToken);
            // Entidade rastreada em vez de ExecuteUpdate, pelo mesmo motivo do IssuePairAsync: o update
            // em lote não traduz sob IgnoreQueryFilters. Idempotente — só revoga o que ainda está ativo.
            var atual = await _db.UserRefreshTokens.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TokenHash == currentHash && t.RevokedAt == null, ct);
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

    public async Task<TokenPair?> SelectTenantAsync(
        string selectionTicket, Guid targetTenantId, CancellationToken ct)
    {
        // [AEGIS-AUD-012] A identidade vem EXCLUSIVAMENTE do ticket assinado — nunca do corpo. Ticket
        // inválido/expirado/adulterado (ou de outra audience) falha fechado, sem tocar o banco.
        if (!_tokens.TryReadTenantSelectionTicket(selectionTicket ?? "", out var accountId))
            return null;

        // Revalida o membership ATIVO no alvo AGORA (não confia no que o login viu): o mesmo caminho da troca.
        var validated = await ValidateTargetAsync(accountId, targetTenantId, ct);
        if (validated is null)
            return null;
        var (target, account) = validated.Value;

        return await IssuePairAsync(target, account, ct);
    }

    /// <summary>
    /// Revalida que a pessoa tem membership ATIVO no alvo e o tenant não está suspenso, devolvendo o
    /// membership e a conta prontos para emissão. Base COMUM da troca (<see cref="SwitchTenantAsync"/>) e da
    /// seleção inicial (<see cref="SelectTenantAsync"/>). Casa por IdentityAccountId (FK, não string), então
    /// um acesso criado noutro cliente com o "mesmo e-mail" nunca habilita o alvo. <c>null</c> = negado.
    /// </summary>
    private async Task<(User Target, IdentityAccount Account)?> ValidateTargetAsync(
        Guid accountId, Guid targetTenantId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || targetTenantId == Guid.Empty)
            return null;

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

        return (target, account);
    }

    /// <summary>Um membership válido carregado com o nome/slug do tenant (para seletor e decisão de login).</summary>
    private sealed record MembershipRow(User User, string TenantName, string TenantSlug);

    /// <summary>
    /// [AEGIS-AUD-012] TODOS os memberships VÁLIDOS da pessoa — ATIVOS e de tenant NÃO suspenso — em ordem
    /// ESTÁVEL (acesso mais antigo primeiro). É a base tanto do seletor quanto da decisão de login. A ordem
    /// não é cosmética: sem ela a apresentação dependeria do plano de execução do Postgres; mas ela NUNCA
    /// serve para escolher um ambiente em silêncio (isso é decisão explícita em <see cref="ResolveSessionAsync"/>).
    /// </summary>
    private async Task<List<MembershipRow>> ValidMembershipsAsync(Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return new();

        // Escopo da exceção: memberships DESTA conta, cruzados com o tenant para nome/slug. O join é sobre
        // Tenants (sem query filter — não é ITenantOwned), então só o lado User atravessa o filtro. Somente
        // ambientes ATIVOS e não suspensos entram.
        // ⚠️ Projeta num tipo ANÔNIMO no SQL e só depois monta o record em memória. Projetar direto num
        // record dentro do Join fazia o EF desistir da tradução ("could not be translated") e cair em 500 —
        // pego no smoke test ao vivo. Query syntax + tipo anônimo é a forma que o provider traduz.
        var rows = await (
            from u in _db.Users.IgnoreQueryFilters()
            join t in _db.Tenants on u.TenantId equals t.Id
            where u.IdentityAccountId == accountId && u.IsActive && t.Status != TenantStatus.Suspended
            select new { User = u, t.Name, t.Slug }).ToListAsync(ct);

        // Ordenação em MEMÓRIA de propósito: o SQLite (provider dos testes) não ordena por DateTimeOffset,
        // então um ORDER BY no servidor deixaria o login sem cobertura. Custo nulo — são poucos acessos.
        return rows
            .OrderBy(r => r.User.CreatedAt).ThenBy(r => r.User.Id)
            .Select(r => new MembershipRow(r.User, r.Name, r.Slug))
            .ToList();
    }

    /// <summary>Projeta os memberships no descritor do seletor, em ordem alfabética de nome (apresentação).</summary>
    private static IReadOnlyList<TenantMembershipDescriptor> ToDescriptors(IEnumerable<MembershipRow> memberships) =>
        memberships
            .OrderBy(m => m.TenantName)
            .Select(m => new TenantMembershipDescriptor(m.User.TenantId, m.TenantName, m.TenantSlug, m.User.Role))
            .ToList();

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return RefreshResult.InvalidOrBreach;

        // [AEGIS-AUD-009] O bruto é hasheado UMA vez; daqui em diante só o hash toca o banco. Nunca se
        // procura o token bruto numa coluna — a coluna guarda apenas o hash determinístico.
        var tokenHash = _refreshHasher.Hash(refreshToken);

        // Sonda ancorada no SEGREDO (agora pelo hash dele), não no tenant ambiente. O refresh token É a
        // credencial (256 bits) e carrega o próprio tenant, então o "silent refresh" do bootstrap funciona
        // sem o cliente saber em que ambiente está — requisito direto do login sem slug. IgnoreQueryFilters
        // aqui é a mesma exceção estrita autorizada para a camada de identidade.
        var probe = await _db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (probe is null)
            return RefreshResult.InvalidOrBreach;   // token desconhecido

        // Daqui em diante operamos DENTRO do tenant que o próprio token declara: o StampTenant segue
        // rígido, apenas deixa de ser alimentado com um contexto que não é o desta escrita.
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(probe.TenantId));

        var stored = await db.UserRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (stored is null)
            return RefreshResult.InvalidOrBreach;

        // [Crítico 2] Expiração ANTES de reuso: um token expirado replayado só retorna 401 e NUNCA
        // dispara a cascata de revogação — fecha o DoS por replay de token ancião.
        if (stored.IsExpired)
            return RefreshResult.InvalidOrBreach;

        // Já revogado quando lido = rotacionado por outra request. Janela de idempotência ou breach.
        if (stored.IsRevoked)
            return await HandleAlreadyRotatedAsync(db, stored, ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        var account = user is null
            ? null
            : await db.IdentityAccounts.FirstOrDefaultAsync(a => a.Id == user.IdentityAccountId, ct);

        if (user is null || account is null || !user.IsActive)
        {
            // Órfão/desativado: encerra a cadeia de forma atômica (não dispara breach). `now` capturado
            // (não `DateTimeOffset.UtcNow` inline) para o SetProperty traduzir em todo provider.
            var revokedNow = DateTimeOffset.UtcNow;
            await db.UserRefreshTokens
                .Where(t => t.Id == stored.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => revokedNow), ct);
            return RefreshResult.InvalidOrBreach;
        }

        // [Crítico 1] Rotação ATÔMICA. O sucessor bruto é gerado aqui e só o HASH dele é persistido — no
        // pai (ReplacedByTokenHash) e no filho (TokenHash). O bruto vai apenas ao vencedor, no TokenPair.
        // O UPDATE ... WHERE RevokedAt IS NULL é a seção crítica: sob concorrência, apenas UMA request
        // afeta 1 linha; as demais afetam 0 e "perdem a corrida". Elimina o fork de cadeia (dois filhos
        // ativos do mesmo pai) que cegava a detecção de breach.
        var (newRefresh, newRefreshExp) = _tokens.CreateRefreshToken();
        var newRefreshHash = _refreshHasher.Hash(newRefresh);
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var claimed = await db.UserRefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, _ => now)
                .SetProperty(t => t.ReplacedByTokenHash, _ => newRefreshHash), ct);

        if (claimed == 0)
        {
            // Perdi a corrida entre o SELECT e o UPDATE. Desfaz e trata como já-rotacionado, lendo o
            // estado do vencedor — cai na janela de idempotência (conflito benigno) ou em breach.
            await tx.RollbackAsync(ct);
            var latest = await db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == stored.Id, ct);
            return latest is null ? RefreshResult.InvalidOrBreach : await HandleAlreadyRotatedAsync(db, latest, ct);
        }

        // Venci a corrida: emito o novo filho, persistindo só o HASH do sucessor. ITenantOwned →
        // StampTenant carimba/valida no SaveChanges. O bruto (newRefresh) volta apenas no TokenPair.
        db.UserRefreshTokens.Add(new UserRefreshToken
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            TokenHash = newRefreshHash,
            ExpiresAt = newRefreshExp,
        });
        var (access, accessExp) = _tokens.CreateAccessToken(user, account);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RefreshResult.Success(new TokenPair(access, accessExp, newRefresh, newRefreshExp));
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        // Localiza/revoga pelo HASH do cookie — nunca comparando a coluna persistida com o token bruto.
        var tokenHash = _refreshHasher.Hash(refreshToken);
        var now = DateTimeOffset.UtcNow;

        // Revoga de forma atômica, só se ainda ativo. Idempotente. IgnoreQueryFilters porque o logout
        // pode chegar sem tenant ambiente (token de acesso já expirado) — e o segredo apresentado é a
        // própria autorização para revogá-lo. `now` capturado para o SetProperty traduzir em todo provider.
        await _db.UserRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => now), ct);
    }

    /// <summary>
    /// [AEGIS-AUD-007] Troca a identidade corporativa JÁ VALIDADA (Entra) por uma sessão local. Toda a
    /// verificação criptográfica (assinatura/issuer/audience/lifetime) já foi feita pelo esquema JWT Bearer
    /// do Entra; aqui só decidimos o VÍNCULO e emitimos o par local. Nunca cria conta/membership.
    /// </summary>
    public async Task<LoginResult> ExchangeFederatedAsync(
        FederatedIdentity identity, Guid? lastTenantId, CancellationToken ct)
    {
        // Federação precisa estar ligada (defesa em profundidade — a policy já exige, mas o serviço é a
        // autoridade final de persistência).
        if (!_federation.FederationEnabled)
            return LoginResult.Denied;

        // Canonicaliza tid/oid: um identificador malformado retorna falha GENÉRICA aqui mesmo, ANTES de
        // qualquer consulta por e-mail ou escrita no banco. tid precisa ser o tenant configurado. Só o
        // formato canônico "D" é comparado e persistido.
        if (!Guid.TryParse(identity.TenantId, out var tidGuid) || !Guid.TryParse(identity.ObjectId, out var oidGuid))
            return LoginResult.Denied;
        if (!Guid.TryParse(_federation.TenantId, out var allowedTid) || tidGuid != allowedTid)
            return LoginResult.Denied;

        var tid = tidGuid.ToString("D");
        var oid = oidGuid.ToString("D");

        // 1) Identidade JÁ vinculada → localizar por tid+oid (imutáveis), NUNCA por e-mail. Assim, trocar
        //    o e-mail no Entra não quebra o login, e o e-mail deixa de ser superfície de captura.
        var account = await _db.IdentityAccounts
            .FirstOrDefaultAsync(a => a.ExternalTenantId == tid && a.ExternalObjectId == oid, ct);

        var precisaVincular = false;
        if (account is null)
        {
            // 2) PRIMEIRO login: só é permitido VINCULAR a uma conta preexistente cujo e-mail normalizado
            //    corresponda ao do token E que ainda NÃO esteja vinculada (ExternalObjectId NULL). Uma
            //    conta já ligada a OUTRO oid não é retornada aqui (filtro oid IS NULL) — logo não pode ser
            //    capturada por alguém com o mesmo e-mail. Sem conta correspondente → nega, sem provisionar.
            var email = (identity.Email ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(email))
                return LoginResult.Denied;

            account = await _db.IdentityAccounts
                .FirstOrDefaultAsync(a => a.Email == email && a.ExternalObjectId == null, ct);
            if (account is null)
                return LoginResult.Denied;
            precisaVincular = true;
        }

        // 3) [AEGIS-AUD-012] Ao menos um acesso ATIVO é obrigatório e NUNCA é criado (provisionamento é o
        //    AUD-010). Conferido ANTES de gravar o vínculo: uma federação negada (sem acesso ativo) não deixa
        //    efeito colateral no banco.
        var memberships = await ValidMembershipsAsync(account.Id, ct);
        if (memberships.Count == 0)
            return LoginResult.Denied;

        // 4) Fecha o vínculo no primeiro login. IdentityAccount é referência GLOBAL (não ITenantOwned):
        //    grava sem tenant ambiente. O índice único parcial (tid,oid) torna a corrida uma invariante
        //    de banco — dois primeiros logins concorrentes da MESMA identidade não geram duas linhas.
        if (precisaVincular)
        {
            account.ExternalTenantId = tid;
            account.ExternalObjectId = oid;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Corrida perdida: outra requisição venceu o vínculo desta MESMA identidade. Falha fechada
                // e re-resolve pelo vencedor (tid+oid) — mesma linha (e-mail é único), membership segue válido.
                _db.Entry(account).State = EntityState.Detached;
                var winner = await _db.IdentityAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.ExternalTenantId == tid && a.ExternalObjectId == oid, ct);
                if (winner is null)
                    return LoginResult.Denied;
                account = winner;   // mesma linha (e-mail é único); os memberships já carregados seguem válidos
            }
        }

        // [AEGIS-AUD-012] Resolve o ambiente sem escolher em silêncio — idêntico ao login local (um acesso
        // seleciona automático; vários exigem o último tenant revalidado ou escolha explícita).
        return await ResolveSessionAsync(account, memberships, lastTenantId, ct);
    }

    public async Task<PasswordChangeResult> ChangeOwnPasswordAsync(
        Guid accountId, string currentPassword, string newPassword, CancellationToken ct)
    {
        if (accountId == Guid.Empty)
            return PasswordChangeResult.Rejected(PasswordChangeStatus.NotFound);

        // A identidade é GLOBAL (sem query filter): leitura por Id, ancorada na conta AUTENTICADA.
        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
            return PasswordChangeResult.Rejected(PasswordChangeStatus.NotFound);

        // Conta federated-only (sem hash): não há senha local a trocar — a credencial é do provedor corporativo.
        if (account.PasswordHash is null)
            return PasswordChangeResult.Rejected(
                PasswordChangeStatus.NoLocalCredential,
                "Esta conta autentica pelo provedor corporativo — não há senha local para trocar.");

        // A senha atual precisa conferir. NÃO é aparada (espaço é caractere legítimo). Verificação ~constante.
        if (!_hasher.Verify(currentPassword ?? "", account.PasswordHash))
            return PasswordChangeResult.Rejected(
                PasswordChangeStatus.InvalidCurrentPassword, "Senha atual incorreta.");

        // A NOVA senha segue a MESMA política NIST (comprimento) — autoridade única PasswordPolicy.
        if (PasswordPolicy.ValidateStrength(newPassword ?? "") is { } weak)
            return PasswordChangeResult.Rejected(PasswordChangeStatus.WeakPassword, weak);

        // ATÔMICO: a nova senha (IdentityAccount) e a revogação de TODAS as sessões da identidade — em TODOS
        // os tenants — num ÚNICO commit. Se a revogação falhar, a transação é revertida e a senha ANTIGA
        // permanece válida (nenhum estado parcial: senha nova sem sessões revogadas seria uma janela de risco).
        // A revogação cross-tenant é ESTRITAMENTE ancorada no account_id — a exceção autorizada de
        // IgnoreQueryFilters sobre identidade (ver a doc de IAuthService). O access token já emitido conserva
        // seu teto de 10 min (não o encurtamos).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var membershipIds = await _db.Users.IgnoreQueryFilters()
            .Where(u => u.IdentityAccountId == accountId)
            .Select(u => u.Id)
            .ToListAsync(ct);

        account.PasswordHash = _hasher.Hash(newPassword!);   // IdentityAccount é global: SaveChanges não exige tenant
        await _db.SaveChangesAsync(ct);

        if (membershipIds.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;   // capturado p/ o SetProperty traduzir em todo provider
            await _db.UserRefreshTokens.IgnoreQueryFilters()
                .Where(t => t.RevokedAt == null && membershipIds.Contains(t.UserId))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => now), ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Troca de senha concluída para a conta {AccountId}: sessões revogadas em {Count} ambiente(s).",
            accountId, membershipIds.Count);

        return PasswordChangeResult.Changed;
    }

    /// <summary>
    /// Trata a reapresentação de um token JÁ revogado (rotacionado):
    ///  (a) Dentro da janela de idempotência e com sucessor ativo → conflito benigno de rotação: outra
    ///      requisição já venceu e recebeu o sucessor. [AEGIS-AUD-009] Como só o HASH do sucessor está
    ///      persistido, o bruto não pode ser reentregue aqui — devolve <see cref="RefreshOutcome.RotationConflict"/>
    ///      para o chamador pedir um retry curto. NÃO revoga a cadeia, NÃO limpa o cookie.
    ///  (b) Fora da janela (ou sucessor ausente/inativo) → reuso genuíno = breach: revoga a CADEIA do token
    ///      (blast radius reduzido, não todas as sessões do usuário) e retorna InvalidOrBreach.
    /// </summary>
    private async Task<RefreshResult> HandleAlreadyRotatedAsync(
        AegisScoreDbContext db, UserRefreshToken parent, CancellationToken ct)
    {
        if (parent.RevokedAt is { } revokedAt
            && DateTimeOffset.UtcNow - revokedAt <= IdempotencyWindow
            && !string.IsNullOrEmpty(parent.ReplacedByTokenHash))
        {
            // Localiza o sucessor pelo HASH gravado no pai — casa exatamente com o TokenHash do filho.
            var successor = await db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == parent.ReplacedByTokenHash, ct);

            if (successor is not null && successor.IsActive)
            {
                // Conflito benigno: a rotação legítima acabou de acontecer e o sucessor está vivo. Não há
                // como devolver o sucessor bruto (não é reconstruível do hash), então sinalizamos retry.
                // O vencedor já entregou o bruto ao SEU cliente; o retry deste reenviará o cookie sucessor.
                return RefreshResult.RotationConflict;
            }
        }

        _logger.LogWarning(
            "SECURITY: reutilização de refresh token revogado fora da janela de idempotência " +
            "(possível roubo de sessão). Tenant={Tenant} User={User} TokenId={TokenId}. " +
            "Revogando a CADEIA do token.",
            parent.TenantId, parent.UserId, parent.Id);

        await RevokeChainAsync(db, parent, ct);
        return RefreshResult.InvalidOrBreach;
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
            TokenHash = _refreshHasher.Hash(refresh),   // [AEGIS-AUD-009] só o hash é persistido; o bruto vai ao cliente
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
    /// caminhando para frente via <see cref="UserRefreshToken.ReplacedByTokenHash"/>. [AEGIS-AUD-009] A
    /// linhagem é percorrida EXCLUSIVAMENTE por hashes — nunca se reconstrói nem se armazena token bruto.
    /// Outras sessões legítimas do mesmo usuário (outros dispositivos/navegadores) permanecem ativas.
    /// </summary>
    private static async Task RevokeChainAsync(
        AegisScoreDbContext db, UserRefreshToken start, CancellationToken ct)
    {
        var chain = new List<Guid> { start.Id };
        var nextHash = start.ReplacedByTokenHash;
        var guard = 0;

        // Caminha a linhagem para frente pelo hash do sucessor; o guard evita laço infinito em dados
        // legados bifurcados.
        while (!string.IsNullOrEmpty(nextHash) && guard++ < 256)
        {
            var link = nextHash;
            var node = await db.UserRefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == link, ct);
            if (node is null)
                break;

            chain.Add(node.Id);
            nextHash = node.ReplacedByTokenHash;
        }

        var now = DateTimeOffset.UtcNow;   // capturado para o SetProperty traduzir em todo provider
        await db.UserRefreshTokens
            .Where(t => chain.Contains(t.Id) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => now), ct);
    }
}
