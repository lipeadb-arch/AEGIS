using System.Security.Cryptography;
using System.Text;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// [AEGIS-AUD-009] Hash determinístico do refresh token: SHA-256 do token bruto → hexadecimal minúsculo
/// de 64 caracteres. É o ÚNICO ponto que calcula esse hash — o serviço nunca chama SHA-256 direto.
///
/// SHA-256 puro (sem salt/pepper) é o algoritmo CORRETO aqui, ao contrário do PBKDF2 das senhas: o refresh
/// token é aleatório de 256 bits (não uma senha adivinhável), então não há brute-force a encarecer e o
/// lookup por igualdade precisa ser determinístico e indexável. O formato hex minúsculo casa byte a byte
/// com <c>encode(sha256(convert_to(token,'UTF8')),'hex')</c> do PostgreSQL — o mesmo transform usado no
/// backfill da migration, o que permite hashear sessões legadas sem novo login.
/// </summary>
public sealed class Sha256RefreshTokenHasher : IRefreshTokenHasher
{
    public string Hash(string rawToken)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(digest).ToLowerInvariant();   // 64 chars, minúsculo
    }
}
