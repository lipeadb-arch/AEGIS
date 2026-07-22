using System.Security.Cryptography.X509Certificates;

namespace AegisScore.Infrastructure.DataProtection;

/// <summary>
/// [AEGIS-AUD-053] Opções do Data Protection (seção <c>DataProtection</c>).
///
/// ⚠️ NENHUM material criptográfico pertence a <c>appsettings.json</c>. A senha do PKCS#12 chega por
/// variável de ambiente (<c>DataProtection__Certificate__Password</c>), <c>dotnet user-secrets</c> ou
/// secret manager — todos entram pelo mesmo <c>IConfiguration</c>, sem tocar o arquivo versionado.
/// </summary>
public sealed class DataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Override do application discriminator.
    ///
    /// AUSENTE (null) = derivado como <c>AegisScore:{EnvironmentName}</c> — o caminho normal.
    /// PRESENTE porém vazio/whitespace = erro de configuração, e falha no boot: aceitar em silêncio
    /// devolveria o app ao discriminator implícito do framework (o ContentRootPath), que muda entre
    /// réplicas e deploys e invalidaria todo o ciphertext já gravado.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>Onde o key ring é persistido. Ver <see cref="DataProtectionPersistence"/>.</summary>
    public DataProtectionPersistence PersistenceProvider { get; set; } = DataProtectionPersistence.DbContext;

    /// <summary>
    /// Exige key ring durável. Sempre tratado como <c>true</c> em Production, mesmo se o arquivo de
    /// configuração esquecer — chave efêmera em produção significa perder credenciais no restart.
    /// </summary>
    public bool RequirePersistence { get; set; }

    /// <summary>
    /// Exige envelope das chaves em repouso. Sempre tratado como <c>true</c> em Production.
    /// </summary>
    public bool RequireKeyEncryption { get; set; }

    /// <summary>Certificado X.509 do envelope. Obrigatório em Production.</summary>
    public DataProtectionCertificateOptions? Certificate { get; set; }
}

/// <summary>Estratégia de persistência do key ring.</summary>
public enum DataProtectionPersistence
{
    /// <summary>PostgreSQL via <c>DataProtectionKeyDbContext</c> — compartilhado entre réplicas.</summary>
    DbContext = 0,

    /// <summary>
    /// Chaves em memória, perdidas no encerramento do processo. Aceitável apenas em testes
    /// automatizados; recusado quando a persistência é exigida.
    /// </summary>
    Ephemeral = 1,
}

/// <summary>
/// Origem do certificado de envelope. Neutro em relação a cloud: um PKCS#12 montado no host serve
/// tanto para arquivo local quanto para segredo projetado por qualquer orquestrador, e o certificate
/// store cobre os hosts que já gerenciam certificados pelo sistema operacional.
/// </summary>
public sealed class DataProtectionCertificateOptions
{
    /// <summary>Caminho de um PKCS#12 (<c>.pfx</c>/<c>.p12</c>) montado no host.</summary>
    public string? Path { get; set; }

    /// <summary>Senha do PKCS#12. NUNCA em <c>appsettings.json</c> — ver a nota da classe de opções.</summary>
    public string? Password { get; set; }

    /// <summary>Thumbprint no certificate store do sistema operacional (alternativa a <see cref="Path"/>).</summary>
    public string? Thumbprint { get; set; }

    public StoreName StoreName { get; set; } = StoreName.My;

    public StoreLocation StoreLocation { get; set; } = StoreLocation.CurrentUser;

    /// <summary>Nenhuma origem informada — usado para distinguir "seção ausente" de "seção incompleta".</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Path) && string.IsNullOrWhiteSpace(Thumbprint);
}
