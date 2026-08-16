using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AegisScore.Infrastructure.DataProtection;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.DataProtection;

/// <summary>
/// [AEGIS-AUD-053] Política de configuração do Data Protection — o fail-fast.
///
/// Estes testes existem porque cada recusa abaixo, se passasse em silêncio, produziria uma proteção
/// que PARECE funcionar: o serviço sobe, cifra segredos de conector e só revela o defeito quando o
/// próximo restart (ou a próxima réplica) não conseguir decifrá-los.
///
/// Os certificados são gerados em memória, então a suíte não depende de nada instalado na máquina.
/// </summary>
public sealed class DataProtectionPlanTests
{
    private static DataProtectionOptions Options(
        string? applicationName = null,
        DataProtectionPersistence persistence = DataProtectionPersistence.DbContext,
        bool requirePersistence = false,
        bool requireKeyEncryption = false,
        DataProtectionCertificateOptions? certificate = null) => new()
        {
            ApplicationName = applicationName,
            PersistenceProvider = persistence,
            RequirePersistence = requirePersistence,
            RequireKeyEncryption = requireKeyEncryption,
            Certificate = certificate,
        };

    private static X509Certificate2 SelfSigned(
        DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AegisScore-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
    }

    // ---- Application discriminator -------------------------------------------

    [Fact]
    public void ApplicationName_Ausente_EhDerivadoDoAmbiente()
    {
        var plan = DataProtectionPlan.Resolve(Options(), "Staging", isProduction: false);

        plan.ApplicationName.Should().Be("AegisScore:Staging",
            "o discriminator precisa ser estável entre réplicas e deploys, e isolado por ambiente");
    }

    [Fact]
    public void ApplicationName_DeclaradoVazio_FalhaRapido()
    {
        var act = () => DataProtectionPlan.Resolve(Options(applicationName: "   "), "Development", false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApplicationName*",
                "aceitar em branco devolveria o app ao discriminator implícito (o caminho físico), " +
                "que muda entre réplicas e invalidaria todo o ciphertext");
    }

    [Fact]
    public void ApplicationName_Customizado_EhPreservado()
    {
        var plan = DataProtectionPlan.Resolve(
            Options(applicationName: " AegisScore:Legado "), "Development", false);

        plan.ApplicationName.Should().Be("AegisScore:Legado", "o override é trimado, não reinterpretado");
    }

    [Fact]
    public void Ambientes_Diferentes_ProduzemDiscriminatorsDiferentes()
    {
        var dev = DataProtectionPlan.Resolve(Options(), "Development", false).ApplicationName;
        var prod = DataProtectionPlan.Resolve(
            Options(certificate: null), "Production", false).ApplicationName;

        dev.Should().NotBe(prod, "um ciphertext de desenvolvimento nunca deve ser legível em produção");
    }

    // ---- Persistência ---------------------------------------------------------

    [Fact]
    public void Ephemeral_ComPersistenciaExigida_FalhaRapido()
    {
        var act = () => DataProtectionPlan.Resolve(
            Options(persistence: DataProtectionPersistence.Ephemeral, requirePersistence: true),
            "Development", isProduction: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PersistenceProvider*");
    }

    [Fact]
    public void Production_ComEphemeral_FalhaRapido_MesmoSemAsFlags()
    {
        // Nem RequirePersistence nem RequireKeyEncryption declarados: Production endurece sozinho.
        var act = () => DataProtectionPlan.Resolve(
            Options(persistence: DataProtectionPersistence.Ephemeral), "Production", isProduction: true);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DbContext_EhAPersistenciaResolvida()
    {
        var plan = DataProtectionPlan.Resolve(Options(), "Development", false);

        plan.PersistToDbContext.Should().BeTrue();
    }

    // ---- Envelope em repouso --------------------------------------------------

    [Fact]
    public void Production_SemCertificado_FalhaRapido()
    {
        var act = () => DataProtectionPlan.Resolve(Options(), "Production", isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*certificado X.509*",
                "persistir sem envelope deixaria o key ring legível para quem alcançar o banco");
    }

    [Fact]
    public void Production_ComCertificadoValido_NaoUsaDpapi()
    {
        using var certificate = SelfSigned();
        var path = ExportToTempPfx(certificate);
        try
        {
            var plan = DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path }),
                "Production", isProduction: true);

            plan.Certificate.Should().NotBeNull();
            plan.UseDpapi.Should().BeFalse(
                "DPAPI prende as chaves a um usuário e máquina — inaceitável com múltiplas réplicas");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Development_SemCertificado_NaoExigeEnvelope()
    {
        var plan = DataProtectionPlan.Resolve(Options(), "Development", isProduction: false);

        plan.Should().NotBeNull("o loop de desenvolvimento não pode depender de PKI");
    }

    [Fact]
    public void RequireKeyEncryption_ForaDoWindows_SemCertificado_FalhaRapido()
    {
        // No Windows o DPAPI cobre o desenvolvimento; fora dele, não há envelope disponível e a
        // exigência precisa ser recusada em vez de silenciosamente ignorada.
        if (OperatingSystem.IsWindows())
            return;

        var act = () => DataProtectionPlan.Resolve(
            Options(requireKeyEncryption: true), "Development", isProduction: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Envelope*");
    }

    [Fact]
    public void PathEThumbprint_Simultaneos_FalhaRapido()
    {
        var act = () => DataProtectionPlan.Resolve(
            Options(certificate: new DataProtectionCertificateOptions
            {
                Path = "/tmp/qualquer.pfx",
                Thumbprint = "AABBCC",
            }),
            "Development", isProduction: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ambiguidade*");
    }

    [Fact]
    public void Certificado_EmCaminhoInexistente_FalhaRapido()
    {
        var act = () => DataProtectionPlan.Resolve(
            Options(certificate: new DataProtectionCertificateOptions
            {
                Path = Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.pfx"),
            }),
            "Development", isProduction: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*não encontrado*");
    }

    // ---- Validação do certificado --------------------------------------------

    [Fact]
    public void Certificado_SemChavePrivada_EhRecusado()
    {
        using var full = SelfSigned();
        using var publicOnly = X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));

        var act = () => DataProtectionPlan.ValidateCertificate(publicOnly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*chave privada*",
                "sem a chave privada o serviço subiria e só falharia ao ler um segredo já gravado");
    }

    [Fact]
    public void Certificado_Expirado_EhRecusado()
    {
        using var expired = SelfSigned(
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var act = () => DataProtectionPlan.ValidateCertificate(expired);

        act.Should().Throw<InvalidOperationException>().WithMessage("*expirou*");
    }

    [Fact]
    public void Certificado_AindaNaoValido_EhRecusado()
    {
        using var future = SelfSigned(
            notBefore: DateTimeOffset.UtcNow.AddDays(5),
            notAfter: DateTimeOffset.UtcNow.AddDays(30));

        var act = () => DataProtectionPlan.ValidateCertificate(future);

        act.Should().Throw<InvalidOperationException>().WithMessage("*passa a valer*");
    }

    [Fact]
    public void Certificado_Valido_EhAceito()
    {
        using var certificate = SelfSigned();

        var act = () => DataProtectionPlan.ValidateCertificate(certificate);

        act.Should().NotThrow();
    }

    // ---- Secret File TEXTUAL Base64 (Render) ---------------------------------
    // O Render entrega Secret Files como TEXTO, então o PKCS#12 é transportado como Base64 (.b64). Base64
    // é só transporte; a senha continua protegendo o PKCS#12. O suporte binário permanece intacto.

    private const string PfxSenha = "pfx-test-pass";   // senha SINTÉTICA de teste — nunca um segredo real

    [Fact]
    public void Pfx_Binario_ComSenha_ContinuaCarregando()
    {
        using var cert = SelfSigned();
        var path = WriteTemp(cert.Export(X509ContentType.Pkcs12, PfxSenha), ".pfx");
        try
        {
            var plan = DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path, Password = PfxSenha }),
                "Production", isProduction: true);

            plan.Certificate.Should().NotBeNull();
            plan.Certificate!.HasPrivateKey.Should().BeTrue("o caminho do PKCS#12 binário permanece funcionando");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Base64_Valido_CarregaComChavePrivada()
    {
        using var cert = SelfSigned();
        var path = WriteTempB64(cert.Export(X509ContentType.Pkcs12, PfxSenha));
        try
        {
            var plan = DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path, Password = PfxSenha }),
                "Production", isProduction: true);

            plan.Certificate.Should().NotBeNull();
            plan.Certificate!.HasPrivateKey.Should().BeTrue("o Base64 do PKCS#12 carrega em memória com a chave privada");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Base64_ComQuebraFinal_Carrega()
    {
        using var cert = SelfSigned();
        var path = WriteTempB64(cert.Export(X509ContentType.Pkcs12, PfxSenha), trailer: "\r\n\n  \t");
        try
        {
            var plan = DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path, Password = PfxSenha }),
                "Development", isProduction: false);

            plan.Certificate!.HasPrivateKey.Should().BeTrue("quebras de linha/espacos finais do Secret File sao normais");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Base64_Invalido_FalhaRapido()
    {
        var path = WriteTempRaw("conteudo invalido ### que nao e base64");
        try
        {
            var act = () => DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path }),
                "Development", isProduction: false);

            act.Should().Throw<InvalidOperationException>().WithMessage("*Base64*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Base64_SenhaErrada_FalhaRapido()
    {
        using var cert = SelfSigned();
        var path = WriteTempB64(cert.Export(X509ContentType.Pkcs12, PfxSenha));
        try
        {
            var act = () => DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path, Password = "senha-errada" }),
                "Development", isProduction: false);

            act.Should().Throw<InvalidOperationException>().WithMessage("*senha*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Base64_SemChavePrivada_FalhaRapido()
    {
        using var full = SelfSigned();
        using var publicOnly = X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));
        var path = WriteTempB64(publicOnly.Export(X509ContentType.Pkcs12));   // PKCS#12 sem chave privada
        try
        {
            var act = () => DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path }),
                "Development", isProduction: false);

            act.Should().Throw<InvalidOperationException>().WithMessage("*chave privada*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Base64_Ausente_FalhaRapido()
    {
        var act = () => DataProtectionPlan.Resolve(
            Options(certificate: new DataProtectionCertificateOptions
            {
                Path = Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.pfx.b64"),
            }),
            "Development", isProduction: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*não encontrado*");
    }

    [Fact]
    public void Base64_Vazio_FalhaRapido()
    {
        var path = WriteTempRaw("   \r\n  ");   // só whitespace → vazio após trim
        try
        {
            var act = () => DataProtectionPlan.Resolve(
                Options(certificate: new DataProtectionCertificateOptions { Path = path }),
                "Development", isProduction: false);

            act.Should().Throw<InvalidOperationException>().WithMessage("*vazio*");
        }
        finally { File.Delete(path); }
    }

    private static string ExportToTempPfx(X509Certificate2 certificate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aegis-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return path;
    }

    private static string WriteTemp(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aegis-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Escreve o Base64 do PKCS#12 num arquivo <c>.b64</c> (o transporte textual do Render).</summary>
    private static string WriteTempB64(byte[] pfxBytes, string? trailer = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aegis-test-{Guid.NewGuid():N}.pfx.b64");
        File.WriteAllText(path, Convert.ToBase64String(pfxBytes) + (trailer ?? ""));
        return path;
    }

    /// <summary>Escreve conteúdo TEXTUAL cru num arquivo <c>.b64</c> (para os casos inválido/vazio).</summary>
    private static string WriteTempRaw(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aegis-test-{Guid.NewGuid():N}.pfx.b64");
        File.WriteAllText(path, content);
        return path;
    }
}
