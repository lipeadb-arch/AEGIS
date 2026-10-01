using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Autenticação da aplicação por CERTIFICADO: a asserção de cliente (RFC 7523) que o
/// Microsoft Entra ID aceita — assinada com a chave do certificado, com a impressão digital no cabeçalho, para o
/// endpoint de token do locatário e com validade curta — e o que acontece quando o certificado não serve. Isto é a
/// autenticação IMPLEMENTADA; a aceitação pelo locatário real é homologação, e não se comprova aqui.
/// </summary>
public sealed class MicrosoftAppCredentialTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComCertificado_PedidoLevaAssercaoAssinada_ENuncaOSegredo()
    {
        var cfg = M365ServicesScenario.Configuration(KnightSourceType.MicrosoftSharePoint);
        var fields = MicrosoftClientCredentialForm.Fields(cfg, "https://clientedemo-admin.sharepoint.com/.default", new FixedClock(Agora));

        fields.Should().NotContainKey("client_secret");
        fields["grant_type"].Should().Be("client_credentials");
        fields["client_id"].Should().Be("client-demo");
        fields["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");

        var parts = fields["client_assertion"].Split('.');
        parts.Should().HaveCount(3);
        var header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
        var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;

        header.GetProperty("alg").GetString().Should().Be("RS256");
        var cert = M365ServicesScenario.Certificate;
        header.GetProperty("x5t#S256").GetString().Should().Be(MicrosoftClientCredentialForm.Base64Url(cert.GetCertHash(HashAlgorithmName.SHA256)));
        header.GetProperty("x5t").GetString().Should().Be(MicrosoftClientCredentialForm.Base64Url(cert.GetCertHash(HashAlgorithmName.SHA1)));

        payload.GetProperty("aud").GetString().Should().Be("https://login.microsoftonline.com/dir-demo-0001/oauth2/v2.0/token");
        payload.GetProperty("iss").GetString().Should().Be("client-demo");
        payload.GetProperty("sub").GetString().Should().Be("client-demo");
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).Should().Be(600, "validade curta: 10 minutos");
        payload.GetProperty("jti").GetString().Should().NotBeNullOrEmpty();

        // A assinatura confere com a chave PÚBLICA do certificado — é o que o Entra ID verifica.
        using var rsa = cert.GetRSAPublicKey()!;
        rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Convert.FromBase64String(Pad(parts[2])),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Fact]
    public void CadaPedido_TemIdentificadorProprio()
    {
        var cfg = M365ServicesScenario.Configuration(KnightSourceType.MicrosoftFabric);
        string Jti() => JsonDocument.Parse(FromBase64Url(MicrosoftClientCredentialForm.Fields(cfg, "s")["client_assertion"].Split('.')[1]))
            .RootElement.GetProperty("jti").GetString()!;
        Jti().Should().NotBe(Jti());
    }

    [Fact]
    public void SemCertificado_UsaOSegredo()
    {
        var cfg = M365ServicesScenario.Configuration(KnightSourceType.MicrosoftIntune, withCertificate: false);
        var fields = MicrosoftClientCredentialForm.Fields(cfg, "https://graph.microsoft.com/.default");
        fields["client_secret"].Should().Be("segredo-sintetico");
        fields.Should().NotContainKey("client_assertion");
    }

    [Theory]
    [InlineData("isto-nao-e-base64!", "base64")]
    [InlineData("QUVHSVM=", "PFX")]
    public void CertificadoInvalido_FalhaDeclarada_SemRepetirConteudo(string pfx, string esperado)
    {
        var cert = new MicrosoftClientCertificate(pfx, "senha-que-nao-pode-vazar");
        var ex = FluentActions.Invoking(() => MicrosoftCertificates.Load(cert)).Should().Throw<MicrosoftCertificateException>().Which;
        ex.Message.Should().Contain(esperado);
        ex.Message.Should().NotContain("senha-que-nao-pode-vazar").And.NotContain(pfx);
    }

    [Fact]
    public void SenhaErrada_FalhaDeclarada()
    {
        var cert = M365ServicesScenario.ClientCertificate with { Password = "outra-senha" };
        FluentActions.Invoking(() => MicrosoftCertificates.Load(cert)).Should().Throw<MicrosoftCertificateException>()
            .Which.Message.Should().Contain("senha");
    }

    [Fact]
    public void Resumo_TrazSoImpressaoDigitalEValidade()
    {
        var s = MicrosoftCertificates.Describe(M365ServicesScenario.ClientCertificate);
        s.Thumbprint.Should().Be(M365ServicesScenario.Certificate.Thumbprint);
        s.HasRsaPrivateKey.Should().BeTrue();
        s.CurrentlyValid.Should().BeTrue();
        M365ServicesScenario.ClientCertificate.ToString().Should().NotContain(M365ServicesScenario.ClientCertificate.PfxBase64[..20]);
    }

    /// <summary>
    /// O motivo de falha de conexão do Exchange Online/Security &amp; Compliance diz qual método de autenticação foi
    /// usado: o certificado é o documentado; o segredo, sem confirmação documental. Nenhum dos dois textos afirma que o
    /// locatário real aceitou.
    /// </summary>
    [Fact]
    public void MotivoDeConexao_NomeiaOMetodoUsado()
    {
        ProtectionCollectorBase.ConnectionReason(KnightCapabilityOutcome.InsufficientPermission, compliance: true, certificate: true)
            .Should().Contain("certificado, o método documentado").And.Contain("Microsoft Exchange Online Protection");
        ProtectionCollectorBase.ConnectionReason(KnightCapabilityOutcome.InsufficientPermission, compliance: false, certificate: false)
            .Should().Contain("segredo de cliente").And.Contain("Office 365 Exchange Online");
    }

    private static string Pad(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }

    private static byte[] FromBase64Url(string b64url) => Convert.FromBase64String(Pad(b64url));

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

/// <summary>[AEGIS-KNIGHT-COVERAGE-04] Consulta DNS de TXT: formato do pedido e interpretação da resposta.</summary>
public sealed class DnsTxtResolverTests
{
    [Fact]
    public void Pedido_TemUmaPerguntaTxt_ComEdns0()
    {
        var q = DnsTxtResolver.BuildQuery(0x1234, "_dmarc.clientedemo.example.com");
        q[0].Should().Be(0x12);
        q[1].Should().Be(0x34);
        (q[2] & 0x01).Should().Be(1, "recursão desejada");
        q[5].Should().Be(1, "uma pergunta");
        q[11].Should().Be(1, "um registro adicional (OPT do EDNS0)");
        Encoding.ASCII.GetString(q, 13, 6).Should().Be("_dmarc");
    }

    [Theory]
    [InlineData("clientedemo.example.com", true)]
    [InlineData("_dmarc.clientedemo.example.com", true)]
    [InlineData("", false)]
    [InlineData("a..b", false)]
    [InlineData("com espaço.example.com", false)]
    public void NomeValidado(string nome, bool valido) => DnsTxtResolver.IsValidName(nome).Should().Be(valido);

    [Fact]
    public void RespostaComTxtEmPedacos_ViraUmTextoSo()
    {
        var r = Response(0x0101, rcode: 0, "v=spf1 include:spf.protection.outlook.com", " -all");
        var parsed = DnsTxtResolver.ParseResponse(r, 0x0101)!;
        parsed.Resolved.Should().BeTrue();
        parsed.Records.Should().ContainSingle().Which.Should().Be("v=spf1 include:spf.protection.outlook.com -all");
    }

    [Fact]
    public void NomeInexistente_EhResolvidoSemRegistro()
    {
        var parsed = DnsTxtResolver.ParseResponse(Response(0x0202, rcode: 3), 0x0202)!;
        parsed.Resolved.Should().BeTrue("NXDOMAIN é resposta: o nome não existe, então não há registro publicado");
        parsed.Records.Should().BeEmpty();
    }

    [Fact]
    public void FalhaDoServidor_OuIdentificadorTrocado_NaoEhResposta()
    {
        DnsTxtResolver.ParseResponse(Response(0x0303, rcode: 2), 0x0303).Should().BeNull("SERVFAIL não diz se o registro existe");
        DnsTxtResolver.ParseResponse(Response(0x0404, rcode: 0, "v=spf1 -all"), 0x9999).Should().BeNull("resposta de outro pedido");
        DnsTxtResolver.ParseResponse(new byte[] { 1, 2, 3 }, 0x0102).Should().BeNull("resposta truncada");
    }

    [Fact]
    public async Task SemResolvedor_NaoResolvido_ComMotivo()
    {
        var r = await new DnsTxtResolver(() => Array.Empty<System.Net.IPAddress>()).ResolveTxtAsync("clientedemo.example.com", default);
        r.Resolved.Should().BeFalse();
        r.Failure.Should().Contain("resolvedor");
    }

    /// <summary>Monta uma resposta DNS com a pergunta ecoada e um registro TXT (com os pedaços dados).</summary>
    private static byte[] Response(ushort id, int rcode, params string[] chunks)
    {
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        U16(id);
        U16(0x8180 | rcode);
        U16(1); U16(chunks.Length == 0 ? 0 : 1); U16(0); U16(0);
        foreach (var l in "clientedemo.example.com".Split('.')) { b.Add((byte)l.Length); b.AddRange(Encoding.ASCII.GetBytes(l)); }
        b.Add(0); U16(16); U16(1);
        if (chunks.Length > 0)
        {
            U16(0xC00C);   // ponteiro para o nome da pergunta
            U16(16); U16(1); b.AddRange(new byte[] { 0, 0, 1, 44 });
            var data = chunks.SelectMany(c => new[] { (byte)c.Length }.Concat(Encoding.ASCII.GetBytes(c))).ToArray();
            U16(data.Length);
            b.AddRange(data);
        }
        return b.ToArray();
    }
}
