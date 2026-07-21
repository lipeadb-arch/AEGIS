using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.DataProtection;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AegisScore.Infrastructure.Tests.DataProtection;

/// <summary>
/// [AEGIS-AUD-053] Comportamento real do key ring persistido.
///
/// Harness dos demais testes da suíte: SQLite in-memory com a conexão mantida aberta, então o key
/// ring é gravado num banco relacional de verdade — o que permite simular restart (recriar o service
/// provider sobre o MESMO store) e scale-out (dois providers simultâneos sobre o mesmo store), que
/// são exatamente os dois cenários que o achado descreve.
///
/// O purpose replicado aqui é o de produção (<c>ConnectorSecretProtector</c>): se ele mudar sem que
/// alguém pense na compatibilidade do ciphertext existente, estes testes deixam de refletir o sistema.
/// </summary>
public sealed class DataProtectionKeyRingTests : IDisposable
{
    private const string Purpose = "AegisScore.ConnectorConfig.Secrets.v1";
    private const string AppName = "AegisScore:Test";
    private const string Segredo = """{"clientSecret":"valor-de-teste"}""";

    private readonly SqliteConnection _connection;

    public DataProtectionKeyRingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewKeyRingContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private DataProtectionKeyDbContext NewKeyRingContext() =>
        new(new DbContextOptionsBuilder<DataProtectionKeyDbContext>()
            .UseSqlite(_connection).Options);

    /// <summary>Um "processo" do AEGIS: service provider próprio sobre o key ring compartilhado.</summary>
    private ServiceProvider BuildProcess(string applicationName, X509Certificate2? certificate = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DataProtectionKeyDbContext>(o => o.UseSqlite(_connection));

        var builder = services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<DataProtectionKeyDbContext>();

        if (certificate is not null)
            builder.ProtectKeysWithCertificate(certificate);

        return services.BuildServiceProvider();
    }

    private static IDataProtector ProtectorOf(ServiceProvider sp) =>
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AegisScore-KeyRing-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    // ---- Round-trip e durabilidade -------------------------------------------

    [Fact]
    public void RoundTrip_RecuperaOSegredo()
    {
        using var sp = BuildProcess(AppName);
        var protector = ProtectorOf(sp);

        var ciphertext = protector.Protect(Segredo);

        ciphertext.Should().NotContain("valor-de-teste", "o segredo nunca trafega em claro");
        protector.Unprotect(ciphertext).Should().Be(Segredo);
    }

    [Fact]
    public void KeyRing_EhPersistidoNoBanco()
    {
        using var sp = BuildProcess(AppName);
        ProtectorOf(sp).Protect(Segredo);

        using var ctx = NewKeyRingContext();
        ctx.DataProtectionKeys.Should().NotBeEmpty(
            "sem linha no store, a chave só existiria em memória e morreria com o processo");
    }

    [Fact]
    public void Restart_DoProcesso_AindaDecifraOCiphertextAnterior()
    {
        string ciphertext;
        using (var antes = BuildProcess(AppName))
            ciphertext = ProtectorOf(antes).Protect(Segredo);

        // Novo service provider sobre o MESMO store: é o restart do container.
        using var depois = BuildProcess(AppName);

        ProtectorOf(depois).Unprotect(ciphertext).Should().Be(Segredo,
            "era exatamente isto que o AddDataProtection() nu não garantia");
    }

    [Fact]
    public void ScaleOut_ReplicaBDecifraOQueAReplicaACifrou()
    {
        using var replicaA = BuildProcess(AppName);
        using var replicaB = BuildProcess(AppName);

        var ciphertext = ProtectorOf(replicaA).Protect(Segredo);

        ProtectorOf(replicaB).Unprotect(ciphertext).Should().Be(Segredo,
            "duas réplicas atrás do mesmo balanceador precisam ler o mesmo segredo");
    }

    // ---- Isolamento e integridade --------------------------------------------

    [Fact]
    public void DiscriminatorDiferente_NaoDecifra_MesmoComOMesmoKeyRing()
    {
        using var producao = BuildProcess("AegisScore:Production");
        var ciphertext = ProtectorOf(producao).Protect(Segredo);

        using var desenvolvimento = BuildProcess("AegisScore:Development");
        var act = () => ProtectorOf(desenvolvimento).Unprotect(ciphertext);

        act.Should().Throw<CryptographicException>(
            "o discriminator entra na cadeia de purpose: preservar o key ring NÃO basta para " +
            "manter ciphertext legível quando o ApplicationName muda");
    }

    [Fact]
    public void PayloadAdulterado_NaoDecifra()
    {
        using var sp = BuildProcess(AppName);
        var protector = ProtectorOf(sp);
        var ciphertext = protector.Protect(Segredo);

        // Vira um caractere no meio do payload — a autenticação do Data Protection precisa barrar.
        var meio = ciphertext.Length / 2;
        var adulterado = ciphertext[..meio]
            + (ciphertext[meio] == 'A' ? 'B' : 'A')
            + ciphertext[(meio + 1)..];

        var act = () => protector.Unprotect(adulterado);

        act.Should().Throw<CryptographicException>();
    }

    // ---- Envelope das chaves em repouso ---------------------------------------

    [Fact]
    public void ComEnvelope_AChaveMestraNaoFicaEmTextoClaro()
    {
        using var certificate = SelfSigned();
        using var sp = BuildProcess(AppName, certificate);
        ProtectorOf(sp).Protect(Segredo);

        var xml = StoredKeyRingXml();

        xml.Should().NotBeNullOrWhiteSpace();
        xml.Should().NotContain("unencrypted",
            "o framework marca explicitamente o key ring sem envelope; a marca não pode aparecer");
        xml.Should().Contain("EncryptedKey", "a chave mestra precisa estar envelopada pelo certificado");
    }

    [Fact]
    public void SemEnvelope_AChaveMestraFicaEmTextoClaro()
    {
        // Contraprova: garante que a asserção do teste acima realmente distingue os dois casos, em vez
        // de passar por acidente. É este o estado que o AEGIS-AUD-053 recusa em Production.
        using var sp = BuildProcess(AppName);
        ProtectorOf(sp).Protect(Segredo);

        StoredKeyRingXml().Should().Contain("unencrypted");
    }

    private string StoredKeyRingXml()
    {
        using var ctx = NewKeyRingContext();
        return string.Join("\n", ctx.DataProtectionKeys.Select(k => k.Xml).ToList());
    }

    // ---- Isolamento multi-tenant ----------------------------------------------

    [Fact]
    public void DataProtectionKey_NaoEhEntidadeDeTenant()
    {
        typeof(DataProtectionKey).Should().NotBeAssignableTo<ITenantOwned>(
            "por isso o stamping fail-closed, que itera apenas Entries<ITenantOwned>, jamais a alcança");
    }

    [Fact]
    public void KeyRing_EGravadoSemQualquerContextoDeTenant()
    {
        // O contexto dedicado sequer recebe ITenantContext: não há tenant a carimbar, e a gravação
        // precisa funcionar em background (refresh do key ring), fora de qualquer request HTTP.
        using var ctx = NewKeyRingContext();
        ctx.DataProtectionKeys.Add(new DataProtectionKey
        {
            FriendlyName = "teste",
            Xml = "<key/>",
        });

        var act = () => ctx.SaveChanges();

        act.Should().NotThrow<TenantSecurityException>();
        ctx.DataProtectionKeys.Should().HaveCount(1);
    }

    [Fact]
    public void ModeloDedicado_ContemApenasOKeyRing_ESemFiltroGlobal()
    {
        using var ctx = NewKeyRingContext();

        var entidades = ctx.Model.GetEntityTypes().ToList();

        entidades.Should().ContainSingle("o contexto do key ring não conhece o domínio")
            .Which.ClrType.Should().Be<DataProtectionKey>();

        // Sem filtro global, uma consulta enxerga todas as linhas independentemente de tenant ambiente.
        ctx.DataProtectionKeys.AddRange(
            new DataProtectionKey { FriendlyName = "a", Xml = "<key/>" },
            new DataProtectionKey { FriendlyName = "b", Xml = "<key/>" });
        ctx.SaveChanges();

        ctx.DataProtectionKeys.Count().Should().Be(2);
    }

    // ---- Composição do DI ------------------------------------------------------

    [Fact]
    public void AddAegisDataProtection_UsaTabelaDeHistoricoDedicada()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=irrelevante;Username=u;Password=p",
            ["DataProtection:PersistenceProvider"] = "DbContext",
        }).Build();

        services.AddAegisDataProtection(configuration, new FakeEnvironment());

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<DbContextOptions<DataProtectionKeyDbContext>>();
        var relational = options.Extensions.OfType<RelationalOptionsExtension>().Single();

        relational.MigrationsHistoryTableName.Should().Be(
            DataProtectionKeyDbContext.MigrationsHistoryTableName,
            "compartilhar __EFMigrationsHistory faria os dois contextos tratarem as migrations do " +
            "outro como desconhecidas");
    }

    [Fact]
    public void AddAegisDataProtection_DerivaDiscriminatorDoAmbiente()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=irrelevante;Username=u;Password=p",
        }).Build();

        var act = () => services.AddAegisDataProtection(
            configuration, new FakeEnvironment { EnvironmentName = "Production" });

        act.Should().Throw<InvalidOperationException>(
            "Production sem certificado precisa abortar já na composição, não no primeiro segredo lido");
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "AegisScore.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
