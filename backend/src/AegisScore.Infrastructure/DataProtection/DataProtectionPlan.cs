using System.Security.Cryptography.X509Certificates;

namespace AegisScore.Infrastructure.DataProtection;

/// <summary>
/// [AEGIS-AUD-053] Decisão resolvida de Data Protection: o que efetivamente será aplicado ao
/// <c>IDataProtectionBuilder</c>. Separado do registro no DI para que TODA a política de segurança
/// (discriminator estável, persistência obrigatória, envelope em repouso) seja testável sem subir
/// um host — é aqui que mora o fail-fast.
/// </summary>
/// <param name="ApplicationName">Application discriminator efetivo.</param>
/// <param name="PersistToDbContext">Persistir o key ring no PostgreSQL compartilhado.</param>
/// <param name="Certificate">Certificado de envelope, quando houver.</param>
/// <param name="UseDpapi">Envelope por DPAPI (somente Windows fora de Production).</param>
public sealed record DataProtectionPlan(
    string ApplicationName,
    bool PersistToDbContext,
    X509Certificate2? Certificate,
    bool UseDpapi)
{
    /// <summary>Prefixo do discriminator. O sufixo é o nome do ambiente.</summary>
    public const string ApplicationNamePrefix = "AegisScore";

    /// <summary>
    /// Resolve e VALIDA a configuração, lançando <see cref="InvalidOperationException"/> com mensagem
    /// acionável em qualquer condição que produziria uma proteção silenciosamente frágil.
    ///
    /// Production endurece por conta própria: persistência e envelope são exigidos mesmo que o arquivo
    /// de configuração não os declare, e DPAPI (atrelado a uma única máquina/usuário) é recusado.
    /// </summary>
    /// <param name="options">Seção <c>DataProtection</c> ligada.</param>
    /// <param name="environmentName">Nome do ambiente (<c>IHostEnvironment.EnvironmentName</c>).</param>
    /// <param name="isProduction">Se o ambiente é Production.</param>
    public static DataProtectionPlan Resolve(
        DataProtectionOptions options, string environmentName, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);

        var applicationName = ResolveApplicationName(options, environmentName);

        // Production não confia no arquivo de configuração para decidir se quer ser seguro.
        var requirePersistence = options.RequirePersistence || isProduction;
        var requireKeyEncryption = options.RequireKeyEncryption || isProduction;

        var persistToDbContext = options.PersistenceProvider == DataProtectionPersistence.DbContext;
        if (!persistToDbContext && requirePersistence)
            throw new InvalidOperationException(
                $"DataProtection:PersistenceProvider está '{options.PersistenceProvider}', mas a persistência " +
                "do key ring é obrigatória neste ambiente. Chaves efêmeras tornam ilegível, a cada reinício, " +
                "todo segredo de conector já cifrado. Use 'DbContext'.");

        var certificate = ResolveCertificate(options.Certificate, isProduction);

        // DPAPI é conveniência de desenvolvimento no Windows: as chaves ficam atreladas ao usuário e à
        // máquina, o que é inaceitável para um serviço com múltiplas réplicas. Nunca em Production, e
        // nunca fora do Windows (onde a API sequer é suportada).
        var useDpapi = certificate is null && !isProduction && OperatingSystem.IsWindows();

        if (certificate is null && !useDpapi && requireKeyEncryption)
            throw new InvalidOperationException(
                "Envelope das chaves em repouso é obrigatório neste ambiente, mas nenhum certificado X.509 " +
                "foi configurado. Defina DataProtection:Certificate:Path (PKCS#12 montado no host) ou " +
                "DataProtection:Certificate:Thumbprint (certificate store). Sem envelope, o key ring fica " +
                "legível para quem alcançar o banco de dados.");

        return new DataProtectionPlan(applicationName, persistToDbContext, certificate, useDpapi);
    }

    private static string ResolveApplicationName(DataProtectionOptions options, string environmentName)
    {
        // Ausente = derivar. Presente porém em branco = erro: o operador quis dizer algo e não disse.
        if (options.ApplicationName is null)
        {
            if (string.IsNullOrWhiteSpace(environmentName))
                throw new InvalidOperationException(
                    "Não foi possível derivar o application discriminator: o nome do ambiente está vazio. " +
                    "Defina DataProtection:ApplicationName explicitamente.");

            return $"{ApplicationNamePrefix}:{environmentName.Trim()}";
        }

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
            throw new InvalidOperationException(
                "DataProtection:ApplicationName foi declarado vazio. Remova a chave para derivar " +
                $"'{ApplicationNamePrefix}:{{Ambiente}}', ou informe um valor não vazio. Aceitar em branco " +
                "devolveria o app ao discriminator implícito do framework (o caminho físico da aplicação), " +
                "que muda entre réplicas e deploys e invalidaria todo o ciphertext existente.");

        return options.ApplicationName.Trim();
    }

    private static X509Certificate2? ResolveCertificate(
        DataProtectionCertificateOptions? options, bool isProduction)
    {
        if (options is null || options.IsEmpty)
        {
            if (isProduction)
                throw new InvalidOperationException(
                    "Production exige certificado X.509 para o envelope do key ring, e nenhum foi " +
                    "configurado. Informe DataProtection:Certificate:Path ou " +
                    "DataProtection:Certificate:Thumbprint.");

            return null;
        }

        var hasPath = !string.IsNullOrWhiteSpace(options.Path);
        var hasThumbprint = !string.IsNullOrWhiteSpace(options.Thumbprint);
        if (hasPath && hasThumbprint)
            throw new InvalidOperationException(
                "DataProtection:Certificate define Path e Thumbprint ao mesmo tempo. Escolha UMA origem — " +
                "arquivo PKCS#12 montado ou certificate store — para que não haja ambiguidade sobre qual " +
                "chave protege o key ring.");

        var certificate = hasPath
            ? LoadFromFile(options.Path!, options.Password)
            : LoadFromStore(options.Thumbprint!, options.StoreName, options.StoreLocation);

        ValidateCertificate(certificate);
        return certificate;
    }

    private static X509Certificate2 LoadFromFile(string path, string? password)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Certificado do Data Protection não encontrado em '{path}'. Confirme se o segredo foi " +
                "montado no host antes do início da aplicação.");

        try
        {
            // X509CertificateLoader é a API não obsoleta a partir do .NET 9 (o construtor de
            // X509Certificate2 com caminho gera SYSLIB0057).
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (Exception ex)
        {
            // A mensagem original pode conter detalhes do arquivo; encapsulamos sem repassá-la ao log
            // de borda. A exceção interna preserva o diagnóstico para quem opera o boot.
            throw new InvalidOperationException(
                $"Falha ao carregar o certificado PKCS#12 de '{path}'. Verifique se o arquivo é um PKCS#12 " +
                "válido e se a senha foi fornecida por variável de ambiente, user-secrets ou secret manager " +
                "(DataProtection:Certificate:Password).", ex);
        }
    }

    private static X509Certificate2 LoadFromStore(
        string thumbprint, StoreName storeName, StoreLocation storeLocation)
    {
        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);

        // validOnly: false — a validação de validade é nossa, logo abaixo, para produzir uma mensagem
        // específica ("expirado") em vez de um "não encontrado" enganoso.
        var normalized = thumbprint.Replace(" ", "").Trim();
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);

        if (found.Count == 0)
            throw new InvalidOperationException(
                $"Certificado com thumbprint '{normalized}' não encontrado em {storeLocation}/{storeName}.");

        return found[0];
    }

    /// <summary>
    /// Exigências mínimas do certificado de envelope. Público para que os testes cubram cada recusa
    /// sem depender de um certificado real instalado na máquina do desenvolvedor.
    /// </summary>
    public static void ValidateCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException(
                $"O certificado '{certificate.Subject}' não possui chave privada. O envelope do key ring " +
                "precisa DECIFRAR as chaves no próximo boot — sem a chave privada, o serviço subiria e só " +
                "descobriria a falha ao tentar ler um segredo já gravado.");

        var now = DateTimeOffset.UtcNow;
        if (now > certificate.NotAfter)
            throw new InvalidOperationException(
                $"O certificado '{certificate.Subject}' expirou em {certificate.NotAfter:u}. " +
                "Renove-o antes de iniciar a aplicação.");

        if (now < certificate.NotBefore)
            throw new InvalidOperationException(
                $"O certificado '{certificate.Subject}' só passa a valer em {certificate.NotBefore:u}.");
    }
}
