using Microsoft.AspNetCore.DataProtection;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-AUD-041] Proteção do payload BRUTO da evidência em repouso (Data Protection), com purpose PRÓPRIO
/// e DISTINTO do purpose dos segredos de conector (<c>AegisScore.ConnectorConfig.Secrets.v1</c>): o "purpose"
/// isola criptograficamente os dois usos, de modo que um não pode decifrar o outro. Reusa o mesmo key ring
/// persistente da plataforma (ver <c>AddAegisDataProtection</c>). O bruto nunca é devolvido pela API/tela.
/// </summary>
public sealed class EvidenceRawPayloadProtector : IEvidenceRawPayloadProtector
{
    private const string Purpose = "AegisScore.EvidenceSignal.RawPayload.v1";
    private readonly IDataProtector _protector;

    public EvidenceRawPayloadProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext ?? "");

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
