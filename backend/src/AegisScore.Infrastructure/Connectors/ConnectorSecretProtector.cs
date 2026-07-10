using Microsoft.AspNetCore.DataProtection;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [Médio 6 / Baixo] Encriptação server-side dos segredos de conector via Data Protection API do
/// ASP.NET Core. O "purpose" isola criptograficamente estes segredos de outros usos de proteção no app.
///
/// PRODUÇÃO — o key ring precisa ser persistido FORA do processo e protegido em repouso, senão os
/// segredos só são recuperáveis enquanto as chaves efêmeras (em memória) existirem, e cada reinício
/// invalida o que já está cifrado no banco. Configure no composition root, por exemplo:
///
///   builder.Services.AddDataProtection()
///       .PersistKeysToFileSystem(new DirectoryInfo("/var/keys"))   // ou PersistKeysToDbContext
///       .ProtectKeysWithAzureKeyVault(keyId, credential);          // envelope em prod
///
/// Ver <c>AddDataProtection()</c> em Program.cs.
/// </summary>
public sealed class ConnectorSecretProtector : IConnectorSecretProtector
{
    private const string Purpose = "AegisScore.ConnectorConfig.Secrets.v1";
    private readonly IDataProtector _protector;

    public ConnectorSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext ?? "");

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
