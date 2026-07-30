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

    /// <summary>
    /// [AEGIS-AUD-011] Claim da AUTORIDADE GLOBAL de plataforma (<see cref="PlatformRole"/> da identidade).
    /// Eixo SEPARADO da claim <c>role</c> (tenant-scoped): nunca é derivada de <see cref="User.Role"/> e não
    /// muda na troca de tenant. Emitida apenas quando há autoridade (<see cref="PlatformRole.PlatformAdmin"/>);
    /// uma identidade sem papel global não recebe a claim (ausência ≡ <see cref="PlatformRole.None"/>).
    /// </summary>
    public const string PlatformRoleClaim = "platform_role";

    /// <summary>
    /// [AEGIS-AUD-012] Claim de PROPÓSITO. Um ticket de seleção de tenant o carrega com
    /// <see cref="TenantSelectionPurpose"/> e NÃO carrega tenant/papel — não é uma sessão.
    /// </summary>
    public const string PurposeClaim = "purpose";
    public const string TenantSelectionPurpose = "tenant-selection";

    private const int MinKeyBytes = 32;           // HS256 exige chave de pelo menos 256 bits
    private const int MaxAccessTokenMinutes = 10;  // [Médio 7] teto rígido de vida do access token
    private const int SelectionTicketMinutes = 5;  // [AEGIS-AUD-012] janela curta para concluir a escolha

    private readonly JwtOptions _opt;
    private readonly SigningCredentials _creds;

    /// <summary>
    /// Audience PRÓPRIA do ticket de seleção — distinta da do access token. Como o esquema Bearer padrão
    /// valida <c>ValidAudience == Audience</c>, um ticket JAMAIS autentica uma rota tenant-scoped: ele só
    /// é aceito por <see cref="TryReadTenantSelectionTicket"/>, que valida contra esta audience.
    /// </summary>
    private string SelectionAudience => $"{_opt.Audience}:tenant-selection";

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
            // Papel DESTE tenant (eixo tenant-scoped): quem é TenantAdmin no cliente A pode ser Analyst no B.
            // NUNCA carrega PlatformAdmin — o tipo TenantRole não o possui.
            new("role", membership.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        // [AEGIS-AUD-011] Eixo GLOBAL, separado e inequívoco: só é emitido quando a IDENTIDADE tem
        // autoridade de plataforma. Vem do estado global da conta (nunca de membership.Role) e é idêntico
        // em todos os tenants — a troca de ambiente reemite `role`, mas não toca este. Ausência da claim ≡
        // sem autoridade global (None), então não poluímos o token com `platform_role=None`.
        if (account.PlatformRole == PlatformRole.PlatformAdmin)
            claims.Add(new Claim(PlatformRoleClaim, account.PlatformRole.ToString()));

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

    public (string Token, DateTimeOffset ExpiresAt) CreateTenantSelectionTicket(Guid accountId)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(SelectionTicketMinutes);

        // Só a identidade e o propósito. Sem tenant_id, sem role, sem account é inválido: o ticket prova
        // "esta pessoa autenticou", nada mais — a autorização do alvo é revalidada no SelectTenantAsync.
        var claims = new List<Claim>
        {
            new(AccountClaim, accountId.ToString()),
            new(PurposeClaim, TenantSelectionPurpose),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: SelectionAudience,   // audience própria: o Bearer padrão NÃO o aceita como sessão
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public bool TryReadTenantSelectionTicket(string ticket, out Guid accountId)
    {
        accountId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(ticket))
            return false;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey));
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _opt.Issuer,
            ValidateAudience = true,
            ValidAudience = SelectionAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },   // barra confusão de algoritmo
        };

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(ticket, parameters, out _);

            // O propósito é obrigatório: um access token normal (audience diferente) já teria falhado acima,
            // mas a checagem explícita fecha qualquer token de audience coincidente sem o propósito.
            if (principal.FindFirst(PurposeClaim)?.Value != TenantSelectionPurpose)
                return false;

            return Guid.TryParse(principal.FindFirst(AccountClaim)?.Value, out accountId)
                && accountId != Guid.Empty;
        }
        catch
        {
            // Assinatura, lifetime, issuer ou audience inválidos: ticket recusado (fail-closed).
            return false;
        }
    }
}
