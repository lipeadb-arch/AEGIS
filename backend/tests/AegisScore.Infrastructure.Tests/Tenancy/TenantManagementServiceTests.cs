using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Tenancy;

/// <summary>
/// Testes do <see cref="TenantManagementService"/> — o serviço de onboarding. Mesmo harness dos demais:
/// SQLite in-memory (banco relacional real, então o índice único de Tenant.Slug e o Global Query Filter
/// são exercitados de verdade).
/// </summary>
public sealed class TenantManagementServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Identidade global que "cria" os tenants nos testes — recebe o membership TenantAdmin.</summary>
    private static readonly Guid CreatorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>Workspace ID do Sentinel — GUID VÁLIDO (o hub valida o formato antes de qualquer escrita).</summary>
    private const string WorkspaceGuid = "abcdefab-1234-5678-9abc-abcdefabcdef";

    private readonly SqliteConnection _connection;

    public TenantManagementServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();

        // ConnectorConfig.TenantId tem FK REAL para Tenant (via Tenant.Connectors), então os clientes
        // dos testes de conector precisam existir de fato — como em produção.
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Cliente A", Slug = "fixture-a", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Cliente B", Slug = "fixture-b", Status = TenantStatus.Active });
        // A identidade criadora precisa preexistir: CreateTenantAsync concede a ela um membership TenantAdmin
        // no ambiente novo (FK real Users → IdentityAccounts).
        ctx.IdentityAccounts.Add(new IdentityAccount
        {
            Id = CreatorId, Email = "fundadora@demo.example.com",
            PasswordHash = new Pbkdf2PasswordHasher().Hash("uma frase longa e boa"),
        });
        ctx.SaveChanges();
    }

    /// <summary>Clientes semeados pelo fixture — a linha de base das contagens de provisionamento.</summary>
    private const int SeededTenants = 2;

    public void Dispose() => _connection.Dispose();

    // ---- CreateTenantAsync ----------------------------------------------------

    [Fact]
    public async Task CreateTenantAsync_NormalizaSlug_ENasceEmOnboarding()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateTenantAsync(
            new CreateTenantCommand("  Acme Corporation  ", "  ACME-Corp  ", CreatorId));

        result.Succeeded.Should().BeTrue();
        result.Slug.Should().Be("acme-corp", "o slug é normalizado antes de tocar o índice único");

        var saved = await db.Tenants.SingleAsync(t => t.Id == result.TenantId);
        saved.Slug.Should().Be("acme-corp");
        saved.Name.Should().Be("Acme Corporation", "o nome é trimado");
        saved.Status.Should().Be(TenantStatus.Onboarding, "cliente novo não nasce Active");

        // O criador nasce como TenantAdmin ATIVO no ambiente novo (concessão atômica).
        await using var assert = NewContext(null);
        var membership = await assert.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.TenantId == result.TenantId && u.IdentityAccountId == CreatorId);
        membership.Role.Should().Be(TenantRole.TenantAdmin);
        membership.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTenantAsync_SlugDuplicadoPorCaixa_EhConflito()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        (await svc.CreateTenantAsync(new CreateTenantCommand("Acme", "acme", CreatorId))).Succeeded.Should().BeTrue();

        // Sem normalização, "ACME" passaria pelo índice único e criaria um cliente-fantasma.
        var second = await svc.CreateTenantAsync(new CreateTenantCommand("Acme de novo", "  ACME ", CreatorId));

        second.Status.Should().Be(TenantProvisioningStatus.SlugAlreadyInUse);
        (await db.Tenants.CountAsync(t => t.Slug == "acme")).Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]                    // curto demais
    [InlineData("acme corp")]            // espaço
    [InlineData("acme/corp")]            // separador de rota
    [InlineData("-acme")]                // hífen na borda
    [InlineData("acme_corp")]            // underscore fora do padrão
    public async Task CreateTenantAsync_SlugMalformado_EhRejeitado(string slug)
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateTenantAsync(new CreateTenantCommand("Acme", slug, CreatorId));

        result.Status.Should().Be(TenantProvisioningStatus.InvalidSlug);
        (await db.Tenants.CountAsync()).Should().Be(SeededTenants, "nada foi provisionado");
    }

    [Fact]
    public async Task CreateTenantAsync_NomeVazio_EhRejeitado()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateTenantAsync(new CreateTenantCommand("   ", "acme", CreatorId));

        result.Succeeded.Should().BeFalse();
        (await db.Tenants.CountAsync()).Should().Be(SeededTenants, "nada foi provisionado");
    }

    // ---- ConfigureConnectorAsync ----------------------------------------------

    [Fact]
    public async Task ConfigureConnectorAsync_CifraCredenciaisEmRepouso()
    {
        const string segredo = """{"clientSecret":"super-secreto"}""";

        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var result = await ServiceFor(db, TenantA, protector).ConfigureConnectorAsync(
            Command(settings: segredo));

        result.Created.Should().BeTrue();

        var saved = await db.Connectors.SingleAsync();
        saved.EncryptedSettings.Should().NotContain("super-secreto", "o segredo nunca fica em claro no banco");
        protector.Unprotect(saved.EncryptedSettings).Should().Be(segredo, "e é recuperável na coleta");
        saved.TenantId.Should().Be(TenantA, "carimbado pelo SaveChanges, não pelo chamador");
    }

    [Fact]
    public async Task ConfigureConnectorAsync_EhUpsertPelaChaveNatural_NaoEmpilhaDuplicatas()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        var first = await svc.ConfigureConnectorAsync(Command(displayName: "Graph (prod)"));
        var second = await svc.ConfigureConnectorAsync(Command(displayName: "Graph (renomeado)"));

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse("o mesmo Provider+Capability RECONFIGURA");
        second.ConnectorId.Should().Be(first.ConnectorId);

        var saved = await db.Connectors.SingleAsync();
        saved.DisplayName.Should().Be("Graph (renomeado)");
    }

    [Fact]
    public async Task ConfigureConnectorAsync_ReconfiguracaoSemSegredo_PreservaOVigente()
    {
        const string segredo = "credencial-original";

        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        await svc.ConfigureConnectorAsync(Command(settings: segredo));
        var cifradoOriginal = (await db.Connectors.SingleAsync()).EncryptedSettings;

        // Só muda o intervalo — não manda credencial. Não pode APAGAR a que já funcionava.
        await svc.ConfigureConnectorAsync(Command(settings: null, syncIntervalMinutes: 120));

        var saved = await db.Connectors.SingleAsync();
        saved.EncryptedSettings.Should().Be(cifradoOriginal, "rotação de credencial é ato explícito");
        protector.Unprotect(saved.EncryptedSettings).Should().Be(segredo);
        saved.SyncIntervalMinutes.Should().Be(120);
    }

    [Fact]
    public async Task ConfigureConnectorAsync_SemSegredoNaCriacao_NaoFingeCredencialPresente()
    {
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command(settings: null));

        // Protect("") devolveria blob NÃO vazio e faria o TestAsync dos conectores mentir "Healthy".
        var saved = await db.Connectors.SingleAsync();
        saved.EncryptedSettings.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigureConnectorAsync_AplicaPisoDoIntervaloDeSync()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command(syncIntervalMinutes: 0));

        result.SyncIntervalMinutes.Should().Be(5, "intervalo 0 viraria hot loop contra a API do cliente");
        (await db.Connectors.SingleAsync()).SyncIntervalMinutes.Should().Be(5);
    }

    [Fact]
    public async Task ConfigureConnectorAsync_SemTenantNoContexto_FalhaFechado()
    {
        await using var db = NewContext(null);
        var act = () => ServiceFor(db, null).ConfigureConnectorAsync(Command());

        await act.Should().ThrowAsync<TenantSecurityException>();
    }

    // ---- Isolamento multitenant ------------------------------------------------

    [Fact]
    public async Task GetConnectorAsync_NaoEnxergaConectorDeOutroTenant()
    {
        Guid connectorId;
        await using (var db = NewContext(TenantA))
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command())).ConnectorId;

        // Mesmo id, tenant errado: indistinguível de "não existe".
        await using (var db = NewContext(TenantB))
            (await ServiceFor(db, TenantB).GetConnectorAsync(connectorId)).Should().BeNull();

        await using (var db = NewContext(TenantA))
            (await ServiceFor(db, TenantA).GetConnectorAsync(connectorId)).Should().NotBeNull();
    }

    [Fact]
    public async Task ConfigureConnectorAsync_TenantsDistintosMantemConectoresSeparados()
    {
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command(displayName: "A"));
        await using (var db = NewContext(TenantB))
            await ServiceFor(db, TenantB).ConfigureConnectorAsync(Command(displayName: "B"));

        // Mesma chave natural, tenants diferentes → duas linhas, sem o upsert cruzar a fronteira.
        await using var assert = NewContext(null);
        (await assert.Connectors.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    // ---- Unicidade da chave natural: invariante de BANCO ------------------------

    [Fact]
    public async Task IndiceUnico_RejeitaSegundoConectorComMesmaChaveNatural()
    {
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command());

        // Insert CRU, contornando o upsert do serviço: é o índice que precisa barrar, não o if do C#.
        db.Connectors.Add(new ConnectorConfig
        {
            TenantId = TenantA,
            Provider = ConnectorProvider.Microsoft,
            Capability = ConnectorCapability.SecureScore,
            DisplayName = "clone",
            EncryptedSettings = "",
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "a unicidade do conector é invariante de banco, não promessa do read-then-write");
    }

    [Fact]
    public async Task IndiceUnico_NaoImpedeMesmoProvedorEmCapacidadesDiferentes()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        await svc.ConfigureConnectorAsync(Command());
        var outra = new ConfigureConnectorCommand(
            ConnectorProvider.Microsoft, ConnectorCapability.PolicyDocuments, "SharePoint",
            ConnectorAuthType.OAuthClientCredentials, "{}");

        (await svc.ConfigureConnectorAsync(outra)).Created.Should().BeTrue();
        (await db.Connectors.CountAsync()).Should().Be(2, "a capacidade faz parte da chave natural");
    }

    [Fact]
    public async Task ConfigureConnectorAsync_ChamadasConcorrentes_ConvergemParaUmaLinhaSemFalhar()
    {
        // Contextos distintos = change trackers distintos, então os dois SELECTs podem enxergar a base
        // vazia e ambos tentarem INSERT. O índice único deixa só um passar; o perdedor precisa
        // reconverger para UPDATE em vez de estourar.
        await using var dbA = NewContext(TenantA);
        await using var dbB = NewContext(TenantA);

        var act = () => Task.WhenAll(
            ServiceFor(dbA, TenantA).ConfigureConnectorAsync(Command(displayName: "A")),
            ServiceFor(dbB, TenantA).ConfigureConnectorAsync(Command(displayName: "B")));

        await act.Should().NotThrowAsync("configurar um conector é idempotente por intenção");

        await using var assert = NewContext(TenantA);
        (await assert.Connectors.CountAsync()).Should().Be(1, "a chave natural admite uma linha só");
    }

    [Fact]
    public async Task RecordSyncResultAsync_GravaSinaisECarimboJuntos()
    {
        Guid connectorId;
        await using (var db = NewContext(TenantA))
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command())).ConnectorId;

        await using (var db = NewContext(TenantA))
        {
            var ok = await ServiceFor(db, TenantA).RecordSyncResultAsync(
                connectorId,
                new[]
                {
                    new EvidenceSignal
                    {
                        TenantId = TenantA, ConnectorConfigId = connectorId,
                        SignalKey = "secureScore.overall", NumericValue = 53.77,
                    },
                },
                ConnectorStatus.Healthy);
            ok.Should().BeTrue();
        }

        await using var assert = NewContext(TenantA);
        (await assert.Signals.CountAsync()).Should().Be(1);
        var cfg = await assert.Connectors.SingleAsync();
        cfg.LastStatus.Should().Be(ConnectorStatus.Healthy);
        cfg.LastSyncAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordSyncResultAsync_ConectorDeOutroTenant_NaoGravaNada()
    {
        Guid connectorId;
        await using (var db = NewContext(TenantA))
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command())).ConnectorId;

        await using (var db = NewContext(TenantB))
        {
            var ok = await ServiceFor(db, TenantB).RecordSyncResultAsync(
                connectorId, Array.Empty<EvidenceSignal>(), ConnectorStatus.Healthy);
            ok.Should().BeFalse();
        }

        await using var assert = NewContext(TenantA);
        (await assert.Connectors.SingleAsync()).LastSyncAt.Should().BeNull("nenhuma escrita cruzou a fronteira");
    }

    // ---- [AEGIS-MVP-MICROSOFT-HUB] Conexão Microsoft unificada -----------------

    [Fact]
    public async Task ConfigureMicrosoftHub_AplicaCredencialComumEmTodosOsServicos()
    {
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        var results = await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.IdentityPosture),
            HubService(ConnectorCapability.VulnerabilityScanner),
            HubService(ConnectorCapability.Siem, workspaceId: WorkspaceGuid)));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => r.Created && r.HasCredentials);

        // Um conector por serviço, com o provider derivado da capacidade (Siem ⇒ MicrosoftSentinel).
        var saved = await db.Connectors.ToListAsync();
        saved.Should().HaveCount(4);
        saved.Should().ContainSingle(c => c.Provider == ConnectorProvider.MicrosoftSentinel && c.Capability == ConnectorCapability.Siem);
        saved.Where(c => c.Capability != ConnectorCapability.Siem)
            .Should().OnlyContain(c => c.Provider == ConnectorProvider.Microsoft);

        // A MESMA credencial comum (informada uma vez) está em cada serviço — decifrável na coleta.
        foreach (var c in saved)
        {
            var settings = protector.Unprotect(c.EncryptedSettings);
            settings.Should().Contain("tenant-aaa").And.Contain("client-bbb").And.Contain("secret-ccc");
        }
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_WorkspaceIdSomenteNoSentinel_NaoContaminaOsDemais()
    {
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.Siem, workspaceId: WorkspaceGuid)));

        var sentinel = await db.Connectors.SingleAsync(c => c.Capability == ConnectorCapability.Siem);
        var secureScore = await db.Connectors.SingleAsync(c => c.Capability == ConnectorCapability.SecureScore);

        WorkspaceIdOf(protector.Unprotect(sentinel.EncryptedSettings)).Should().Be(WorkspaceGuid);
        WorkspaceIdOf(protector.Unprotect(secureScore.EncryptedSettings))
            .Should().BeNull("workspaceId é exclusivo do Sentinel e não pode contaminar os demais serviços");
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_WorkspaceIdAusente_FalhaSomenteParaSentinel()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        // Sentinel sem workspaceId → borda 400.
        var comSentinel = () => svc.ConfigureMicrosoftHubAsync(HubCommand(HubService(ConnectorCapability.Siem)));
        await comSentinel.Should().ThrowAsync<MicrosoftHubValidationException>();

        // Os demais serviços NÃO exigem workspaceId — configuram normalmente.
        var semSentinel = await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.VulnerabilityScanner)));
        semSentinel.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_WorkspaceIdInvalido_RejeitaSemEscritaParcial()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        // Seleção com um serviço Microsoft VÁLIDO + Sentinel com workspaceId NÃO-GUID.
        var act = () => svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.Siem, workspaceId: "nao-e-guid")));

        await act.Should().ThrowAsync<MicrosoftHubValidationException>();

        // A validação ocorre ANTES do primeiro upsert: NENHUM conector da seleção (nem o Secure Score válido) foi
        // inserido ou atualizado.
        (await db.Connectors.CountAsync()).Should().Be(0,
            "workspaceId inválido rejeita a seleção inteira, sem escrita parcial");
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_NaoEcoaSegredoNoResultado()
    {
        await using var db = NewContext(TenantA);
        var results = await ServiceFor(db, TenantA).ConfigureMicrosoftHubAsync(
            HubCommand(HubService(ConnectorCapability.SecureScore)));

        // O resultado de saída não carrega o blob de credencial — só o booleano HasCredentials.
        var asText = System.Text.Json.JsonSerializer.Serialize(results);
        asText.Should().NotContain("secret-ccc").And.NotContain("client-bbb");
        results.Single().HasCredentials.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_Reaplicar_AtualizaSemDuplicar()
    {
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.Siem, workspaceId: WorkspaceGuid)));

        // Atualiza a credencial comum (novo segredo) — deve atualizar os serviços selecionados, sem duplicar.
        var again = await svc.ConfigureMicrosoftHubAsync(HubCommandSecret(
            "secret-rotacionado",
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.Siem, workspaceId: WorkspaceGuid)));

        again.Should().OnlyContain(r => !r.Created, "reaplicar RECONFIGURA (upsert pela chave natural)");
        (await db.Connectors.CountAsync()).Should().Be(2, "sem duplicidade de provider/capability");
        foreach (var c in await db.Connectors.ToListAsync())
            protector.Unprotect(c.EncryptedSettings).Should().Contain("secret-rotacionado");
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_EntradaInvalida_Rejeita()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        // Segredo comum ausente.
        await FluentActions.Awaiting(() => svc.ConfigureMicrosoftHubAsync(
                HubCommandSecret("  ", HubService(ConnectorCapability.SecureScore))))
            .Should().ThrowAsync<MicrosoftHubValidationException>();

        // Nenhum serviço selecionado.
        await FluentActions.Awaiting(() => svc.ConfigureMicrosoftHubAsync(HubCommand()))
            .Should().ThrowAsync<MicrosoftHubValidationException>();

        // Capacidade fora da família Microsoft (Edr é push genérico, não pertence ao hub).
        await FluentActions.Awaiting(() => svc.ConfigureMicrosoftHubAsync(
                HubCommand(HubService(ConnectorCapability.Edr))))
            .Should().ThrowAsync<MicrosoftHubValidationException>();

        (await db.Connectors.CountAsync()).Should().Be(0, "nenhuma escrita parcial numa entrada inválida");
    }

    // ---- Fixture ----------------------------------------------------------------

    private static ConfigureConnectorCommand Command(
        string? settings = "{}", string displayName = "Graph", int syncIntervalMinutes = 360) =>
        new(ConnectorProvider.Microsoft, ConnectorCapability.SecureScore, displayName,
            ConnectorAuthType.OAuthClientCredentials, settings, syncIntervalMinutes);

    private static MicrosoftHubServiceSelection HubService(
        ConnectorCapability capability, string? workspaceId = null) =>
        new(capability, SyncIntervalMinutes: 360, WorkspaceId: workspaceId);

    private static ConfigureMicrosoftHubCommand HubCommand(
        params MicrosoftHubServiceSelection[] services) =>
        new("tenant-aaa", "client-bbb", "secret-ccc", services);

    private static ConfigureMicrosoftHubCommand HubCommandSecret(
        string clientSecret, params MicrosoftHubServiceSelection[] services) =>
        new("tenant-aaa", "client-bbb", clientSecret, services);

    /// <summary>Lê o campo <c>workspaceId</c> de um blob de settings em claro (null quando ausente).</summary>
    private static string? WorkspaceIdOf(string settingsJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(settingsJson);
        return doc.RootElement.TryGetProperty("workspaceId", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() : null;
    }

    /// <summary>Opções sobre a MESMA conexão in-memory — o db2 que o CreateTenantAsync abre (para carimbar
    /// o membership no tenant novo) precisa enxergar as mesmas linhas.</summary>
    private DbContextOptions<AegisScoreDbContext> Options =>
        new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(Options, new SystemTenantContext(tenantId));

    private ITenantManagementService ServiceFor(
        AegisScoreDbContext db, Guid? tenantId, IConnectorSecretProtector? protector = null) =>
        new TenantManagementService(
            db, Options, new SystemTenantContext(tenantId), protector ?? new FakeProtector(),
            NullLogger<TenantManagementService>.Instance);

    /// <summary>
    /// Protetor reversível de teste. Substitui a Data Protection real (que exige o key ring do ASP.NET
    /// Core) mantendo a propriedade que os testes verificam: o que sai é diferente do que entrou, e o
    /// round-trip devolve o original.
    /// </summary>
    private sealed class FakeProtector : IConnectorSecretProtector
    {
        private const string Prefix = "enc:";
        public string Protect(string plaintext) =>
            Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext ?? ""));
        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
    }
}
