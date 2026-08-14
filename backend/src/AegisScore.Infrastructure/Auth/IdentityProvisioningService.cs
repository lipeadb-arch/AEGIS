using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// [AEGIS-AUD-010] Implementação do provisionamento GLOBAL de identidade (ver
/// <see cref="IIdentityProvisioningService"/> para o contrato e a política de senha por modo). Adapter da
/// Infrastructure — vive ao lado do <see cref="AuthService"/> e do <see cref="UserManagementService"/>.
///
/// Cria APENAS a <see cref="IdentityAccount"/>, que é entidade GLOBAL (não <see cref="ITenantOwned"/>):
/// a gravação não passa pelo <c>StampTenant</c> e não exige tenant ambiente. Não cria membership nem
/// concede acesso — essa é a autoridade separada do <see cref="UserManagementService"/>.
/// </summary>
public sealed class IdentityProvisioningService : IIdentityProvisioningService
{
    private readonly AegisScoreDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly FederationOptions _federation;
    private readonly ILogger<IdentityProvisioningService> _log;

    public IdentityProvisioningService(
        AegisScoreDbContext db,
        IPasswordHasher hasher,
        IOptions<FederationOptions> federation,
        ILogger<IdentityProvisioningService> log)
    {
        _db = db;
        _hasher = hasher;
        _federation = federation.Value;
        _log = log;
    }

    public async Task<IdentityProvisioningResult> ProvisionAsync(
        ProvisionIdentityCommand command, CancellationToken ct = default)
    {
        if (ValidateEmail(command.Email) is { } emailRejection)
            return emailRejection;

        // A política de senha depende do MODO. Resolve o hash (ou a ausência dele) ANTES de qualquer
        // escrita: uma recusa de política não deve deixar rastro no banco.
        if (ResolvePasswordHash(command.Password, out var passwordHash) is { } passwordRejection)
            return passwordRejection;

        var email = NormalizeEmail(command.Email);

        // A identidade é GLOBAL (sem query filter): esta leitura enxerga o sistema inteiro. Fast-path do
        // conflito; a corrida perdida no INSERT resolve no MESMO conflito (índice único de Email).
        if (await _db.IdentityAccounts.AsNoTracking().AnyAsync(a => a.Email == email, ct))
            return IdentityProvisioningResult.Rejected(IdentityProvisioningStatus.EmailAlreadyInUse);

        var account = new IdentityAccount
        {
            Email = email,
            PasswordHash = passwordHash,   // null quando federated-only — nunca "" nem hash fictício
        };
        _db.IdentityAccounts.Add(account);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Corrida perdida no índice único de Email: dois provisionamentos concorrentes do MESMO e-mail.
            // Fail-closed e traduzido como o MESMO conflito da checagem prévia. O log NÃO inclui o e-mail.
            _db.Entry(account).State = EntityState.Detached;
            _log.LogWarning(ex,
                "Provisionamento global concorrente rejeitado pelo índice único de e-mail — tratado como conflito.");
            return IdentityProvisioningResult.Rejected(IdentityProvisioningStatus.EmailAlreadyInUse);
        }

        // Log sem e-mail nem senha: o identificador auditável é o Id. Registra apenas se a conta nasceu com
        // credencial local ou federated-only — útil para operação, sem PII.
        _log.LogInformation(
            "Identidade global {AccountId} provisionada ({Credencial}).",
            account.Id, passwordHash is null ? "federated-only" : "com credencial local");

        return IdentityProvisioningResult.Ok(Project(account));
    }

    // ---- Política de senha por modo ---------------------------------------------

    /// <summary>
    /// Decide o hash a persistir a partir da senha e do modo, delegando à autoridade ÚNICA
    /// <see cref="PasswordPolicy"/>. Retorna a recusa (não-null) ou <c>null</c> com o <paramref name="hash"/>
    /// resolvido (que é <c>null</c> para federated-only). Nunca produz string vazia.
    /// </summary>
    private IdentityProvisioningResult? ResolvePasswordHash(string? password, out string? hash) =>
        PasswordPolicy.ResolveForNewIdentity(_federation, password, _hasher, out hash, out var detail) switch
        {
            PasswordPolicy.ModeOutcome.Ok => null,
            PasswordPolicy.ModeOutcome.PasswordRequired =>
                IdentityProvisioningResult.Rejected(IdentityProvisioningStatus.PasswordRequired, detail),
            PasswordPolicy.ModeOutcome.PasswordNotAllowed =>
                IdentityProvisioningResult.Rejected(IdentityProvisioningStatus.PasswordNotAllowed, detail),
            _ => IdentityProvisioningResult.Rejected(IdentityProvisioningStatus.WeakPassword, detail),
        };

    // ---- Validação --------------------------------------------------------------

    private static IdentityProvisioningResult? ValidateEmail(string? raw)
    {
        if (!EmailPolicy.IsValid(EmailPolicy.Normalize(raw)))
            return IdentityProvisioningResult.Rejected(
                IdentityProvisioningStatus.InvalidEmail,
                $"E-mail obrigatório, em formato válido e com até {EmailPolicy.MaxLength} caracteres.");
        return null;
    }

    // ---- Helpers ----------------------------------------------------------------

    /// <summary>Normalização compartilhada (<see cref="EmailPolicy"/>) — a mesma forma que o login busca.</summary>
    private static string NormalizeEmail(string? raw) => EmailPolicy.Normalize(raw);

    /// <summary>Projeção de saída SEM o hash (ver <see cref="IdentitySummary"/>). Só expõe SE há credencial local.</summary>
    private static IdentitySummary Project(IdentityAccount a) =>
        new(a.Id, a.Email, a.PasswordHash is not null, a.CreatedAt);
}
