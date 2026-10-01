using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AegisScore.Application.Knight;

// [AEGIS-KNIGHT-COVERAGE-04] Leitura do certificado da aplicação Microsoft (PFX), usada por quem pede token (coletores)
// e por quem valida a configuração (conexão Microsoft unificada). Só a biblioteca-base do .NET: nenhum dado do
// certificado sai daqui, além do resumo público (impressão digital e validade).

/// <summary>Falha ao usar o certificado configurado — a mensagem nunca repete conteúdo do arquivo nem a senha.</summary>
public sealed class MicrosoftCertificateException : Exception
{
    public MicrosoftCertificateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Leitura e validação do PFX guardado no conector.</summary>
public static class MicrosoftCertificates
{
    /// <summary>Carrega o PFX com chave efêmera (não toca o repositório de certificados do sistema).</summary>
    public static X509Certificate2 Load(MicrosoftClientCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(certificate.PfxBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new MicrosoftCertificateException("o certificado da aplicação não está em base64 válido.", ex);
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(bytes, certificate.Password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            throw new MicrosoftCertificateException(
                "o arquivo do certificado não pôde ser aberto — confira se é um PFX com a chave privada e se a senha confere.", ex);
        }
    }

    /// <summary>Resumo NÃO sensível do certificado (para validação e diagnóstico): impressão digital e validade.</summary>
    public static MicrosoftCertificateSummary Describe(MicrosoftClientCertificate certificate, TimeProvider? clock = null)
    {
        using var cert = Load(certificate);
        using var rsa = cert.GetRSAPrivateKey();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return new MicrosoftCertificateSummary(
            cert.Thumbprint,
            new DateTimeOffset(cert.NotBefore.ToUniversalTime()),
            new DateTimeOffset(cert.NotAfter.ToUniversalTime()),
            rsa is not null,
            now >= cert.NotBefore.ToUniversalTime() && now <= cert.NotAfter.ToUniversalTime());
    }
}

/// <summary>Resumo público do certificado: nada aqui permite reconstruir a chave.</summary>
public sealed record MicrosoftCertificateSummary(
    string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool HasRsaPrivateKey, bool CurrentlyValid);
