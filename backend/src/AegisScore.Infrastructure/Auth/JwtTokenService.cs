using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Emite o JWT de acesso (HS256) e o refresh token opaco de alta entropia. O access token carrega
/// obrigatoriamente <c>sub</c> (UserId) e <c>tenant_id</c> (TenantId), além de e-mail, nome, papel e
/// um <c>jti</c> único por token.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    /// <summary>Claim com o tenant do usuário — base para o isolamento derivado do token.</summary>
    public const string TenantClaim = "tenant_id";

    /// <summary>
    /// Claim com a PESSOA global (<see cref="IdentityAccount"/>). É o sujeito estável através de
    /// ambientes: o <c>sub</c> muda a cada troca de tenant (é o membership), este não. É por ele que a
    /// troca de ambiente é autorizada — casar por e-mail seria casar por string.
    /// </summary>
    public const string AccountClaim = "account_id";

    private const int MinKeyBytes = 32;           // HS256 exige chave de pelo menos 256 bits
    private const int MaxAccessTokenMinutes = 10;  // [Médio 7] teto rígido de vida do access token

    private readonly JwtOptions _opt;
    private readonly SigningCredentials _creds;

    public JwtTokenService(IOptions<JwtOptions> opt)
    {
        _opt = opt.Value;

        if (Encoding.UTF8.GetByteCount(_opt.SigningKey) < MinKeyBytes)
            throw new InvalidOperationException(
                $"Jwt:SigningKey ausente ou fraca: HS256 exige ao menos {MinKeyBytes} bytes. " +
                "Configure um segredo forte (user-secrets em dev, env var/Key Vault em produção).");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey));
        _creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User membership, IdentityAccount account)
    {
        var now = DateTimeOffset.UtcNow;
        // [Médio 7] Teto rígido de 10 min mesmo que a config peça mais — invariante de segurança
        // independente de drift em appsettings/env.
        var minutes = Math.Clamp(_opt.AccessTokenMinutes, 1, MaxAccessTokenMinutes);
        var expires = now.AddMinutes(minutes);

        var claims = new List<Claim>
        {
            // sub = o MEMBERSHIP ativo (muda a cada troca de ambiente).
            new(JwtRegisteredClaimNames.Sub, membership.Id.ToString()),
            new(TenantClaim, membership.TenantId.ToString()),
            // account_id = a PESSOA (estável através de ambientes).
            new(AccountClaim, membership.IdentityAccountId.ToString()),
            new(JwtRegisteredClaimNames.Email, account.Email),
            new("name", membership.DisplayName),
            // Papel DESTE tenant: quem é TenantAdmin no cliente A pode ser Analyst no B.
            new("role", membership.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _creds);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return (jwt, expires);
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);   // 256 bits de entropia
        var token = Base64UrlEncoder.Encode(raw);
        return (token, DateTimeOffset.UtcNow.AddDays(_opt.RefreshTokenDays));
    }
}
