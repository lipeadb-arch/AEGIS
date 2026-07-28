using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Auth;

/// <summary>
/// [AEGIS-AUD-007] Policy explícita da troca federada. Além da validação criptográfica do esquema
/// <c>EntraId</c> (assinatura/issuer/audience/lifetime/RS256), exige que o token seja um token DELEGADO do
/// SPA configurado, com o scope certo e do tenant certo — a regra vive no <see cref="FederatedPrincipalValidator"/>,
/// compartilhado com o controller para não haver duas verdades.
/// </summary>
public sealed class FederatedExchangeRequirement : IAuthorizationRequirement
{
    /// <summary>Nome da policy aplicada a <c>POST /api/v1/auth/federation/exchange</c>.</summary>
    public const string PolicyName = "FederatedExchange";
}

public sealed class FederatedExchangeHandler : AuthorizationHandler<FederatedExchangeRequirement>
{
    private readonly FederationOptions _federation;

    public FederatedExchangeHandler(IOptions<FederationOptions> federation) => _federation = federation.Value;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, FederatedExchangeRequirement requirement)
    {
        if (FederatedPrincipalValidator.TryValidate(context.User, _federation, out _))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
