namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Configuração do JWT e do refresh token, bindada da seção <c>Jwt</c> (appsettings / user-secrets).
/// Em produção, <see cref="SigningKey"/> DEVE vir de um secret (env var, Key Vault…), nunca do
/// appsettings versionado.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Chave simétrica HS256. Mínimo de 32 bytes (256 bits) — validado no startup.</summary>
    public string SigningKey { get; set; } = "";

    public string Issuer { get; set; } = "aegis-score";
    public string Audience { get; set; } = "aegis-score";

    /// <summary>
    /// Vida curta do access token (JWT). Padrão: 10 minutos. O <see cref="JwtTokenService"/> aplica
    /// um teto RÍGIDO de 10 min: como o access token não é revogável até expirar, sua vida curta limita
    /// o impacto de um token vazado e da cascata de breach (que só derruba refresh tokens).
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>Vida do refresh token (janela da sessão com rotação). Padrão: 7 dias.</summary>
    public int RefreshTokenDays { get; set; } = 7;
}
