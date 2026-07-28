using System.Text.RegularExpressions;
using AegisScore.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-009] O hasher é o único ponto que calcula o hash do refresh token. Estes testes travam o
/// contrato do qual TUDO depende: formato (hex minúsculo, 64 chars), determinismo (lookup por igualdade)
/// e o vetor conhecido do SHA-256 — a MESMA função que o backfill SQL da migration usa no PostgreSQL.
/// </summary>
public sealed class RefreshTokenHasherTests
{
    private static readonly Sha256RefreshTokenHasher Hasher = new();

    [Fact]
    public void Hash_ProduzHexMinusculoDe64Chars()
    {
        var hash = Hasher.Hash("qualquer-token-de-alta-entropia");

        hash.Should().HaveLength(64);
        Regex.IsMatch(hash, "^[0-9a-f]{64}$").Should().BeTrue("SHA-256 hex minúsculo, comprimento fixo");
    }

    [Fact]
    public void Hash_EhDeterministico()
    {
        // Determinismo é o que permite usar o hash como CHAVE DE BUSCA indexada.
        Hasher.Hash("mesmo-token").Should().Be(Hasher.Hash("mesmo-token"));
        Hasher.Hash("token-a").Should().NotBe(Hasher.Hash("token-b"));
    }

    [Fact]
    public void Hash_CasaComVetorConhecidoDoSha256()
    {
        // SHA-256("abc") — vetor público. Garante que é SHA-256 puro (sem salt/pepper) e que casa byte a
        // byte com encode(sha256(convert_to('abc','UTF8')),'hex') do PostgreSQL (backfill da migration).
        Hasher.Hash("abc").Should().Be(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }
}
