using System.Text.RegularExpressions;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Autoridade ÚNICA da normalização e validação de e-mail global. Reutilizada pelo provisionamento
/// (<see cref="IdentityProvisioningService"/>), pelo onboarding de plataforma
/// (<see cref="PlatformTenantUserService"/>) e por qualquer descoberta por e-mail — a MESMA forma canônica
/// que o índice único de <c>IdentityAccount.Email</c> compara. Normalizar de formas divergentes deixaria
/// "Ana@x.com" e "ana@x.com" conviverem como duas contas, ou o login não achar o que foi gravado.
/// </summary>
public static class EmailPolicy
{
    /// <summary>Espelha o <c>HasMaxLength</c> da coluna — validar aqui evita um erro de banco opaco.</summary>
    public const int MaxLength = 256;

    /// <summary>Formato conservador, avaliado sobre o e-mail JÁ normalizado (minúsculas).</summary>
    private static readonly Regex Pattern = new(
        @"^[a-z0-9._%+-]+@[a-z0-9-]+(\.[a-z0-9-]+)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Forma canônica: aparada e em minúsculas. É o valor persistido e comparado.</summary>
    public static string Normalize(string? raw) => (raw ?? "").Trim().ToLowerInvariant();

    /// <summary>Valida o e-mail JÁ normalizado: não-vazio, dentro do teto e no formato aceito.</summary>
    public static bool IsValid(string normalizedEmail) =>
        normalizedEmail.Length is > 0 and <= MaxLength && Pattern.IsMatch(normalizedEmail);
}
