using System;
using System.Security.Cryptography;
using System.Text;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-AUD-020] Hashing e verificação da CHAVE DE INGESTÃO dos conectores genéricos de push. A chave é
/// um SEGREDO DE ALTA ENTROPIA (não uma senha de baixa entropia), então usa-se SHA-256 determinístico — o
/// mesmo raciocínio do hash de refresh token (AUD-009): verificação por hash indexável, sem KDF caro, e a
/// chave em claro NUNCA é persistida nem devolvida. Verificação em tempo ~constante (FixedTimeEquals).
/// NÃO reutiliza a chave de assinatura JWT nem qualquer segredo compartilhado — SHA-256 não tem chave.
/// </summary>
public static class IngestionKey
{
    /// <summary>Comprimento mínimo razoável para a entropia da chave de ingestão.</summary>
    public const int MinLength = 24;

    /// <summary>A chave respeita a política mínima (presente e comprimento suficiente)?</summary>
    public static bool MeetsPolicy(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Trim().Length >= MinLength;

    /// <summary>SHA-256 do valor em hexadecimal minúsculo de 64 chars. Determinístico.</summary>
    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? ""))).ToLowerInvariant();

    /// <summary>
    /// Verifica a chave apresentada contra o hash persistido em tempo ~constante. <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// devolve false com segurança para comprimentos diferentes — um hash ausente/curto simplesmente falha.
    /// </summary>
    public static bool Verify(string presentedKey, string? storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(presentedKey)),
            Encoding.UTF8.GetBytes(storedHash ?? ""));
}
