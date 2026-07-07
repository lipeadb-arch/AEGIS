using System.Security.Cryptography;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Hashing de senha com PBKDF2 (HMAC-SHA256), sem dependências externas. O hash é self-describing —
/// formato <c>{iterações}.{salt-base64}.{hash-base64}</c> — o que permite reforçar o custo no futuro
/// sem invalidar hashes antigos. Verificação em tempo fixo (<see cref="CryptographicOperations.FixedTimeEquals"/>).
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;          // 128 bits
    private const int KeySize = 32;           // 256 bits
    private const int Iterations = 210_000;   // recomendação OWASP (PBKDF2-HMAC-SHA256)
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algo, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algo, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
