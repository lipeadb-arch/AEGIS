using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Implementação do serviço de identidades (ver <see cref="IUserManagementService"/> para o contrato,
/// o modelo de vínculo e a política de senha). Adapter da Infrastructure — vive ao lado do
/// <see cref="AuthService"/>, que é o outro dono do ciclo de vida da identidade.
///
/// Fail-closed por construção: toda leitura passa pelo Global Query Filter do tenant ambiente e toda
/// escrita pelo <c>StampTenant</c>. O <c>TenantId</c> jamais é atribuído à mão — quem carimba é o
/// <see cref="AegisScoreDbContext"/>, que revalida contra o contexto e lança se houver divergência.
/// </summary>
public sealed class UserManagementService : IUserManagementService
{
    /// <summary>
    /// Piso de comprimento da senha (NIST SP 800-63B). O 800-63B fixa 8 como mínimo absoluto; 12 é o
    /// piso adotado aqui por ser um console de administração de postura de segurança, onde a conta
    /// comprometida vale o ambiente inteiro do cliente.
    /// </summary>
    private const int MinPasswordLength = 12;

    /// <summary>
    /// Teto de comprimento. O 800-63B exige aceitar ao menos 64 caracteres (passphrases); o teto existe
    /// só para barrar entrada patológica antes de ela chegar ao PBKDF2. Nada é TRUNCADO — truncar
    /// silenciosamente enfraqueceria a senha que o usuário acredita ter escolhido.
    /// </summary>
    private const int MaxPasswordLength = 128;

    /// <summary>Espelha o <c>HasMaxLength</c> da coluna — validar aqui evita um erro de banco opaco.</summary>
    private const int MaxEmailLength = 256;
    private const int MaxDisplayNameLength = 200;

    /// <summary>
    /// Formato conservador, aplicado sobre o e-mail JÁ normalizado (minúsculas). Deliberadamente mais
    /// restrito que a RFC 5322: e-mail aqui é credencial de login, não campo de correspondência — aceitar
    /// exotismo sintático só amplia superfície sem servir a nenhum usuário real.
    /// </summary>
    private static readonly Regex EmailPattern = new(
        @"^[a-z0-9._%+-]+@[a-z0-9-]+(\.[a-z0-9-]+)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<UserManagementService> _log;

    public UserManagementService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        IPasswordHasher hasher,
        ILogger<UserManagementService> log)
    {
        _db = db;
        _tenant = tenant;
        _hasher = hasher;
        _log = log;
    }

    public async Task<UserProvisioningResult> CreateUserAsync(
        CreateUserCommand command, CancellationToken ct = default)
    {
        var tenantId = RequireAmbientTenant();

        if (Validate(command.Email, command.DisplayName, command.Password, command.Role)
            is { } rejection)
            return rejection;

        var email = NormalizeEmail(command.Email);

        // A PESSOA é global (IdentityAccount não tem query filter), então esta leitura enxerga o
        // sistema inteiro — sem exceção de filtro, porque não há filtro a excetuar.
        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);

        // ⚠️ TRAVA DE SEGURANÇA CENTRAL. Se a pessoa JÁ EXISTE, a senha informada é DESCARTADA: um
        // TenantAdmin qualquer não pode redefinir a credencial de alguém só por citar o e-mail dela.
        // Fosse permitido, bastaria criar "ceo@bancoX.com" no próprio tenant com uma senha escolhida
        // para, em seguida, entrar no ambiente do banco X pelo seletor. O que esta rota concede é
        // ACESSO AO PRÓPRIO TENANT — nunca a credencial da pessoa.
        var accountIsNew = account is null;
        if (account is null)
        {
            account = new IdentityAccount
            {
                Email = email,
                PasswordHash = _hasher.Hash(command.Password),
            };
            _db.IdentityAccounts.Add(account);
        }
        else if (await _db.Users.AsNoTracking()
                     .AnyAsync(u => u.IdentityAccountId == account.Id, ct))
        {
            // Já tem acesso NESTE tenant (o query filter restringe a consulta ao ambiente atual).
            return UserProvisioningResult.Rejected(UserProvisioningStatus.EmailAlreadyInUse);
        }

        var user = new User
        {
            Account = account,
            DisplayName = command.DisplayName.Trim(),
            Role = command.Role,
            IsActive = true,
            // TenantId é carimbado no SaveChanges (fail-closed) — nunca atribuído aqui.
        };

        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Corrida perdida em UM dos dois índices únicos: Email global (duas pessoas criadas ao mesmo
            // tempo) ou (TenantId, IdentityAccountId) (dois acessos simultâneos ao mesmo ambiente). Os
            // dois resolvem no MESMO conflito da checagem prévia (idioma da §2.6/§20).
            _db.Entry(user).State = EntityState.Detached;
            if (accountIsNew) _db.Entry(account).State = EntityState.Detached;
            _log.LogWarning(ex,
                "Provisionamento concorrente de identidade no tenant {TenantId} rejeitado por índice " +
                "único — tratado como conflito.", tenantId);
            return UserProvisioningResult.Rejected(UserProvisioningStatus.EmailAlreadyInUse);
        }

        // Log sem e-mail: o identificador auditável é o Id. O e-mail é PII e credencial de login — o
        // AuthService também registra Tenant/User/TokenId, nunca o endereço.
        _log.LogInformation(
            "Acesso {UserId} criado no tenant {TenantId} com papel {Role} (conta {AccountId}, {Origem}).",
            user.Id, tenantId, user.Role, account.Id, accountIsNew ? "NOVA" : "preexistente");

        return UserProvisioningResult.Ok(UserProvisioningStatus.Created, Project(user, account));
    }

    public async Task<UserProvisioningResult> AssignUserToTenantAsync(
        AssignUserToTenantCommand command, CancellationToken ct = default)
    {
        var tenantId = RequireAmbientTenant();

        // Defesa em profundidade: o comando NOMEIA o tenant de destino, e ele precisa ser este. Escrever
        // noutro tenant é impossível por construção (o StampTenant rejeitaria), mas falhar AQUI dá a
        // mensagem certa em vez de um erro de carimbo — e documenta a fronteira no ponto de entrada.
        if (command.TenantId != tenantId)
            throw new TenantSecurityException(
                $"TenantId do comando ({command.TenantId}) diverge do tenant do contexto ({tenantId}). " +
                "Conceder acesso a outro tenant exige operar DENTRO daquele tenant (Um-para-Muitos).");

        if (ValidateEmail(command.Email) is { } emailRejection) return emailRejection;
        if (ValidateRole(command.Role) is { } roleRejection) return roleRejection;

        var email = NormalizeEmail(command.Email);
        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);

        // O acesso DESTA pessoa a ESTE tenant (query filter restringe ao ambiente atual).
        var existing = account is null
            ? null
            : await _db.Users.FirstOrDefaultAsync(u => u.IdentityAccountId == account.Id, ct);

        // ---- Já tem acesso a este ambiente: gestão de permissão, não de credencial ----
        if (existing is not null)
        {
            var reactivated = !existing.IsActive;
            existing.Role = command.Role;
            existing.IsActive = true;
            // A credencial vive na IdentityAccount e não é tocada aqui de propósito: conceder permissão
            // NÃO é resetar senha — e permitir o reset por esta rota devolveria ao TenantAdmin o poder
            // de sequestrar a conta global de qualquer pessoa que ele consiga nomear.
            await _db.SaveChangesAsync(ct);

            _log.LogInformation(
                "Acesso {UserId} atualizado no tenant {TenantId}: papel {Role}{Reactivated}.",
                existing.Id, tenantId, existing.Role, reactivated ? ", REATIVADO" : "");

            return UserProvisioningResult.Ok(UserProvisioningStatus.AccessUpdated, Project(existing, account));
        }

        // ---- Pessoa já existe no sistema, mas sem acesso AQUI: concede sem pedir senha ----
        // A credencial dela já existe e é dela; este fluxo só cria o vínculo com este ambiente.
        if (account is not null)
        {
            var granted = new User
            {
                Account = account,
                DisplayName = DisplayNameFromEmail(email),
                Role = command.Role,
                IsActive = true,
            };
            _db.Users.Add(granted);
            await _db.SaveChangesAsync(ct);

            _log.LogInformation(
                "Acesso {UserId} concedido no tenant {TenantId} a conta preexistente {AccountId} como {Role}.",
                granted.Id, tenantId, account.Id, granted.Role);

            return UserProvisioningResult.Ok(UserProvisioningStatus.Created, Project(granted, account));
        }

        // ---- Pessoa nova no sistema inteiro: aí sim é preciso criar a credencial dela ----
        if (string.IsNullOrEmpty(command.InitialPassword))
            return UserProvisioningResult.Rejected(
                UserProvisioningStatus.PasswordRequired,
                "Pessoa ainda não cadastrada no sistema: informe uma senha inicial. " +
                "Para quem já tem conta, o acesso é concedido sem senha — a credencial é dela.");

        return await CreateUserAsync(
            new CreateUserCommand(
                command.Email,
                // Sem nome de exibição no comando de concessão: o local do e-mail é um provisório
                // honesto e editável, melhor que inventar um nome ou recusar a operação por isso.
                DisplayNameFromEmail(email),
                command.InitialPassword,
                command.Role),
            ct);
    }

    // ---- Validação --------------------------------------------------------------

    /// <summary>Valida o comando completo de criação; <c>null</c> = aprovado.</summary>
    private static UserProvisioningResult? Validate(
        string? email, string? displayName, string? password, UserRole role) =>
        ValidateEmail(email)
        ?? ValidateDisplayName(displayName)
        ?? ValidatePassword(password)
        ?? ValidateRole(role);

    private static UserProvisioningResult? ValidateEmail(string? raw)
    {
        var email = NormalizeEmail(raw);
        if (email.Length is 0 or > MaxEmailLength || !EmailPattern.IsMatch(email))
            return UserProvisioningResult.Rejected(
                UserProvisioningStatus.InvalidEmail,
                $"E-mail obrigatório, em formato válido e com até {MaxEmailLength} caracteres.");
        return null;
    }

    private static UserProvisioningResult? ValidateDisplayName(string? raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length is 0 or > MaxDisplayNameLength)
            return UserProvisioningResult.Rejected(
                UserProvisioningStatus.InvalidDisplayName,
                $"Nome de exibição obrigatório, com até {MaxDisplayNameLength} caracteres.");
        return null;
    }

    /// <summary>
    /// Política do 800-63B: comprimento, e só. Sem regra de composição — ver a nota na interface.
    /// A senha NÃO é trimada: espaço é caractere legítimo e aparado silenciosamente quebraria o login
    /// seguinte, que compara o valor cru.
    /// </summary>
    private static UserProvisioningResult? ValidatePassword(string? password)
    {
        if (password is null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return UserProvisioningResult.Rejected(
                UserProvisioningStatus.WeakPassword,
                $"Senha deve ter entre {MinPasswordLength} e {MaxPasswordLength} caracteres. " +
                "Prefira uma frase longa — não exigimos maiúsculas, dígitos ou símbolos (NIST SP 800-63B).");

        // Senha só de espaço em branco passaria no teste de comprimento e é indefensável.
        if (string.IsNullOrWhiteSpace(password))
            return UserProvisioningResult.Rejected(
                UserProvisioningStatus.WeakPassword, "Senha não pode ser composta apenas de espaços.");

        return null;
    }

    /// <summary>
    /// Barra o escalonamento de privilégio: <see cref="UserRole.PlatformAdmin"/> autoriza operações de
    /// PLATAFORMA (criar tenants), então emiti-lo por uma rota de tenant transformaria um TenantAdmin em
    /// admin da plataforma com um POST. Provisionado fora do onboarding self-service, por construção.
    /// </summary>
    private static UserProvisioningResult? ValidateRole(UserRole role) =>
        role == UserRole.PlatformAdmin
            ? UserProvisioningResult.Rejected(
                UserProvisioningStatus.RoleNotAssignable,
                "PlatformAdmin é papel de PLATAFORMA e não é atribuível por esta superfície.")
            : null;

    // ---- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Tenant ambiente, fail-closed. Idioma do <c>ControlStateWriter</c>: falhar aqui dá a mensagem
    /// certa e evita montar a entidade à toa.
    /// </summary>
    private Guid RequireAmbientTenant() => _tenant.TenantId
        ?? throw new TenantSecurityException(
            "Provisionamento de identidade sem tenant resolvido no contexto (fail-closed).");

    /// <summary>Mesma normalização do <see cref="AuthService.LoginAsync"/> — senão o login não acha o que foi gravado.</summary>
    private static string NormalizeEmail(string? raw) => (raw ?? "").Trim().ToLowerInvariant();

    /// <summary>Provisório editável a partir do local do e-mail ("ana.silva@x.com" → "ana.silva").</summary>
    private static string DisplayNameFromEmail(string email)
    {
        var local = email.Split('@')[0];
        return local.Length > MaxDisplayNameLength ? local[..MaxDisplayNameLength] : local;
    }

    /// <summary>
    /// Projeção de saída SEM o hash de senha (ver <see cref="UserSummary"/>). Recebe os dois lados
    /// porque o e-mail passou a viver na conta global e o resto no membership.
    /// </summary>
    private static UserSummary Project(User u, IdentityAccount account) => new(
        u.Id, u.TenantId, account.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt);
}
