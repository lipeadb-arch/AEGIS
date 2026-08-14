using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Autoridade ÚNICA da política de senha (NIST SP 800-63B: comprimento, sem regra de composição) e da sua
/// resolução POR MODO de federação. Extraída para que o provisionamento global
/// (<see cref="IdentityProvisioningService"/>), o onboarding de plataforma
/// (<see cref="PlatformTenantUserService"/>) e a troca da própria senha (<see cref="AuthService"/>)
/// apliquem EXATAMENTE a mesma régua — sem duplicar o limite de comprimento nem a semântica Local/Federated/Hybrid.
/// </summary>
public static class PasswordPolicy
{
    /// <summary>Piso de comprimento (NIST SP 800-63B) — prefira uma frase longa.</summary>
    public const int MinLength = 12;

    /// <summary>Teto — barra entrada patológica antes do PBKDF2. Nada é truncado.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Valida SÓ a força (comprimento + não-só-espaços). A senha NÃO é aparada — espaço é caractere legítimo
    /// e aparar quebraria o login seguinte, que compara o valor cru. Retorna <c>null</c> quando válida, ou a
    /// mensagem de recusa.
    /// </summary>
    public static string? ValidateStrength(string password)
    {
        if (password.Length < MinLength || password.Length > MaxLength)
            return $"Senha deve ter entre {MinLength} e {MaxLength} caracteres. " +
                   "Prefira uma frase longa — não exigimos maiúsculas, dígitos ou símbolos (NIST SP 800-63B).";

        if (string.IsNullOrWhiteSpace(password))
            return "Senha não pode ser composta apenas de espaços.";

        return null;
    }

    /// <summary>Desfecho da resolução de senha por modo para uma identidade NOVA.</summary>
    public enum ModeOutcome
    {
        /// <summary>Senha resolvida (o <c>hash</c> de saída pode ser <c>null</c> para federated-only).</summary>
        Ok,

        /// <summary>Modo <c>Local</c> sem senha: a conta precisa de credencial local para autenticar.</summary>
        PasswordRequired,

        /// <summary>Modo <c>Federated</c> com senha: credencial inutilizada — recusada em vez de guardada.</summary>
        PasswordNotAllowed,

        /// <summary>Senha presente porém fora da política de comprimento.</summary>
        WeakPassword,
    }

    /// <summary>
    /// Resolve o hash a persistir para uma identidade NOVA, conforme o modo de federação:
    /// <list type="bullet">
    /// <item><c>Local</c>: senha OBRIGATÓRIA (único fator).</item>
    /// <item><c>Federated</c>: conta SEM senha; senha enviada é RECUSADA.</item>
    /// <item><c>Hybrid</c>: senha OPCIONAL.</item>
    /// </list>
    /// Em <see cref="ModeOutcome.Ok"/>, <paramref name="hash"/> é o PBKDF2 da senha ou <c>null</c>
    /// (federated-only) — jamais string vazia nem hash fictício. <paramref name="detail"/> explica a recusa.
    /// </summary>
    public static ModeOutcome ResolveForNewIdentity(
        FederationOptions federation, string? password, IPasswordHasher hasher,
        out string? hash, out string? detail)
    {
        hash = null;
        detail = null;
        var hasPassword = !string.IsNullOrEmpty(password);

        // Federated: autentica só pelo Entra. Uma senha aqui seria credencial inutilizada — recusa.
        if (!federation.PasswordLoginEnabled)
        {
            if (hasPassword)
            {
                detail = "Em modo Federated a conta autentica apenas pelo provedor corporativo: não envie senha local.";
                return ModeOutcome.PasswordNotAllowed;
            }
            return ModeOutcome.Ok;   // federated-only: sem hash, e está correto
        }

        // Local exige senha; Hybrid a aceita opcional.
        if (!hasPassword)
        {
            if (federation.Mode == FederationMode.Local)
            {
                detail = "Em modo Local a identidade precisa de uma senha local.";
                return ModeOutcome.PasswordRequired;
            }
            return ModeOutcome.Ok;   // Hybrid sem senha: federated-only permitido
        }

        // Senha presente (Local ou Hybrid): valida a política e deriva o hash.
        if (ValidateStrength(password!) is { } strengthError)
        {
            detail = strengthError;
            return ModeOutcome.WeakPassword;
        }

        hash = hasher.Hash(password!);
        return ModeOutcome.Ok;
    }
}
