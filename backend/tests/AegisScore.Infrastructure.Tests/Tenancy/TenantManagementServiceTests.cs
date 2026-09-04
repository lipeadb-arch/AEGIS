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

    // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-02] Intune como QUINTO serviço do hub ----

    [Fact]
    public async Task ConfigureMicrosoftHub_IntuneEntraComoQuintoServico_ReusandoACredencialComum()
    {
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        var results = await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.IdentityPosture),
            HubService(ConnectorCapability.VulnerabilityScanner),
            HubService(ConnectorCapability.Siem, workspaceId: WorkspaceGuid),
            HubService(ConnectorCapability.ConfigAnalyzer)));

        results.Should().HaveCount(5);
        results.Should().OnlyContain(r => r.Created && r.HasCredentials);

        var intune = await db.Connectors.SingleAsync(c => c.Capability == ConnectorCapability.ConfigAnalyzer);
        intune.Provider.Should().Be(ConnectorProvider.Microsoft, "o Intune é um filho Microsoft, não um provider novo");
        intune.DisplayName.Should().Be("Microsoft Intune · Configuração e Conformidade");

        // A MESMA credencial comum — nenhum segredo adicional foi pedido para o Intune.
        var settings = protector.Unprotect(intune.EncryptedSettings);
        settings.Should().Contain("tenant-aaa").And.Contain("client-bbb").And.Contain("secret-ccc");
        WorkspaceIdOf(settings).Should().BeNull("workspaceId continua exclusivo do Sentinel");
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_ReaplicarComIntune_NaoDuplicaNemAlteraOsDemaisServicos()
    {
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.ConfigAnalyzer)));
        var secureScoreIdBefore = (await db.Connectors
            .SingleAsync(c => c.Capability == ConnectorCapability.SecureScore)).Id;

        var again = await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.ConfigAnalyzer)));

        again.Should().OnlyContain(r => !r.Created, "reaplicar RECONFIGURA (upsert pela chave natural)");
        (await db.Connectors.CountAsync()).Should().Be(2, "sem duplicidade de provider/capability");
        (await db.Connectors.SingleAsync(c => c.Capability == ConnectorCapability.SecureScore)).Id
            .Should().Be(secureScoreIdBefore, "os demais serviços Microsoft permanecem os MESMOS conectores");
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_ComIntune_NaoEcoaSegredoNoResultado()
    {
        await using var db = NewContext(TenantA);
        var results = await ServiceFor(db, TenantA).ConfigureMicrosoftHubAsync(
            HubCommand(HubService(ConnectorCapability.ConfigAnalyzer)));

        var serialized = System.Text.Json.JsonSerializer.Serialize(results);
        serialized.Should().NotContain("secret-ccc", "o segredo comum nunca volta para o frontend");
    }

    [Fact]
    public async Task ConfigureMicrosoftHub_IntuneTemCicloDeVidaIndependente()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        await svc.ConfigureMicrosoftHubAsync(HubCommand(
            HubService(ConnectorCapability.SecureScore),
            HubService(ConnectorCapability.ConfigAnalyzer)));

        var intuneId = (await db.Connectors.SingleAsync(c => c.Capability == ConnectorCapability.ConfigAnalyzer)).Id;
        var disabled = await svc.SetConnectorEnabledAsync(intuneId, enabled: false);
        disabled.Status.Should().Be(ConnectorAdminStatus.Updated);

        var intune = await db.Connectors.SingleAsync(c => c.Id == intuneId);
        var secureScore = await db.Connectors.SingleAsync(c => c.Capability == ConnectorCapability.SecureScore);
        intune.Enabled.Should().BeFalse("desabilitar o Intune é um ato próprio dele");
        intune.EncryptedSettings.Should().NotBeNullOrEmpty("a credencial é PRESERVADA para reativação");
        secureScore.Enabled.Should().BeTrue("os demais serviços Microsoft seguem inalterados");
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

    // ---- [AEGIS-MVP-ADMIN-LIFECYCLE-01] Ciclo de vida administrativo do conector -----------------

    [Fact]
    public async Task UpdateConnectorAsync_EditaNomeEIntervalo_PreservaSegredo()
    {
        const string segredo = "credencial-vigente";
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        var created = await svc.ConfigureConnectorAsync(Command(settings: segredo));
        var cifradoOriginal = (await db.Connectors.SingleAsync()).EncryptedSettings;

        var result = await svc.UpdateConnectorAsync(
            new UpdateConnectorCommand(created.ConnectorId, "Graph (novo nome)", 120));

        result.Succeeded.Should().BeTrue();
        result.Connector!.DisplayName.Should().Be("Graph (novo nome)");
        result.Connector.SyncIntervalMinutes.Should().Be(120);
        result.Connector.HasCredentials.Should().BeTrue("editar não pode apagar a credencial");

        var saved = await db.Connectors.SingleAsync();
        saved.EncryptedSettings.Should().Be(cifradoOriginal, "editar nome/intervalo NUNCA reescreve o segredo");
        protector.Unprotect(saved.EncryptedSettings).Should().Be(segredo);
    }

    [Fact]
    public async Task UpdateConnectorAsync_AplicaPisoDoIntervalo()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        var created = await svc.ConfigureConnectorAsync(Command());

        var result = await svc.UpdateConnectorAsync(new UpdateConnectorCommand(created.ConnectorId, "Graph", 0));
        result.Connector!.SyncIntervalMinutes.Should().Be(5, "intervalo 0 viraria hot loop contra a API do cliente");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateConnectorAsync_NomeInvalido_EhRejeitado(string nome)
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        var created = await svc.ConfigureConnectorAsync(Command(displayName: "Original"));

        var result = await svc.UpdateConnectorAsync(new UpdateConnectorCommand(created.ConnectorId, nome, 360));
        result.Status.Should().Be(ConnectorAdminStatus.InvalidDisplayName);
        (await db.Connectors.SingleAsync()).DisplayName.Should().Be("Original", "nada foi alterado");
    }

    [Fact]
    public async Task UpdateConnectorAsync_ConectorDeOutroTenant_EhNotFound()
    {
        Guid connectorId;
        await using (var db = NewContext(TenantA))
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command())).ConnectorId;

        await using (var db = NewContext(TenantB))
        {
            var result = await ServiceFor(db, TenantB).UpdateConnectorAsync(
                new UpdateConnectorCommand(connectorId, "sequestro", 360));
            result.Status.Should().Be(ConnectorAdminStatus.NotFound, "cross-tenant é indistinguível de inexistente");
        }
    }

    [Fact]
    public async Task SetConnectorEnabledAsync_Desabilita_PreservaSegredo_EhIdempotente()
    {
        const string segredo = "credencial";
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);
        var created = await svc.ConfigureConnectorAsync(Command(settings: segredo));

        var disabled = await svc.SetConnectorEnabledAsync(created.ConnectorId, enabled: false);
        disabled.Connector!.Enabled.Should().BeFalse();
        disabled.Connector.HasCredentials.Should().BeTrue("desabilitar PRESERVA a credencial para reativação");

        // Idempotente: desabilitar de novo é sucesso sem efeito.
        (await svc.SetConnectorEnabledAsync(created.ConnectorId, enabled: false)).Succeeded.Should().BeTrue();

        var saved = await db.Connectors.SingleAsync();
        saved.Enabled.Should().BeFalse();
        protector.Unprotect(saved.EncryptedSettings).Should().Be(segredo);

        (await svc.SetConnectorEnabledAsync(created.ConnectorId, enabled: true)).Connector!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task DisconnectConnectorAsync_EliminaAmbasCredenciais_DesabilitaEPreservaLinha()
    {
        // Conector genérico de PUSH: tem chave de ingestão (hash) além (potencialmente) de settings.
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        var pushCmd = new ConfigureConnectorCommand(
            ConnectorProvider.Generic, ConnectorCapability.Siem, "SIEM push",
            ConnectorAuthType.ApiKey, """{"ingestionKey":"chave-de-ingestao-de-alta-entropia-1234"}""");
        var created = await svc.ConfigureConnectorAsync(pushCmd);
        (await db.Connectors.SingleAsync()).IngestionKeyHash.Should().NotBeNull("push nasce com hash da chave");

        var result = await svc.DisconnectConnectorAsync(created.ConnectorId);
        result.Succeeded.Should().BeTrue();
        result.Connector!.Enabled.Should().BeFalse();
        result.Connector.HasCredentials.Should().BeFalse();
        result.Connector.HasIngestionKey.Should().BeFalse();

        var saved = await db.Connectors.SingleAsync();
        saved.Should().NotBeNull("a linha é PRESERVADA — nunca exclusão física");
        saved.EncryptedSettings.Should().BeEmpty("EncryptedSettings eliminado");
        saved.IngestionKeyHash.Should().BeNull("IngestionKeyHash eliminado");
        saved.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectConnectorAsync_DepoisReconfigurar_ReativaMesmaLinhaSemDuplicar()
    {
        await using var db = NewContext(TenantA);
        var protector = new FakeProtector();
        var svc = ServiceFor(db, TenantA, protector);

        var created = await svc.ConfigureConnectorAsync(Command(settings: "segredo-antigo"));
        await svc.DisconnectConnectorAsync(created.ConnectorId);

        // Reconfigurar o MESMO provider+capability reativa a linha existente (upsert pela chave natural).
        var again = await svc.ConfigureConnectorAsync(Command(settings: "segredo-novo"));
        again.Created.Should().BeFalse("reconfigurar reativa a linha existente, não cria outra");
        again.ConnectorId.Should().Be(created.ConnectorId);

        (await db.Connectors.CountAsync()).Should().Be(1, "sem duplicidade de provider/capability");
        var saved = await db.Connectors.SingleAsync();
        saved.Enabled.Should().BeTrue("reconfigurar volta a habilitar");
        protector.Unprotect(saved.EncryptedSettings).Should().Be("segredo-novo");
    }

    [Fact]
    public async Task DisconnectConnectorAsync_NaoRessuscitaSeUmaColetaConcluiDepois()
    {
        // Corrida: uma coleta em andamento conclui APÓS a desconexão e chama RecordSyncResultAsync. O carimbo de
        // sync NUNCA reescreve a credencial (o EF só emite UPDATE das colunas alteradas), então a conexão NÃO
        // ressuscita — HasCredentials permanece falso.
        Guid connectorId;
        await using (var db = NewContext(TenantA))
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command(settings: "segredo"))).ConnectorId;

        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA).DisconnectConnectorAsync(connectorId);

        // "Coleta que conclui tarde": contexto separado, como o background sync real.
        await using (var db = NewContext(TenantA))
        {
            var ok = await ServiceFor(db, TenantA).RecordSyncResultAsync(
                connectorId, Array.Empty<EvidenceSignal>(), ConnectorStatus.Healthy);
            ok.Should().BeTrue("registrar o desfecho é permitido — só não pode ressuscitar a credencial");
        }

        await using var assert = NewContext(TenantA);
        var saved = await assert.Connectors.SingleAsync();
        saved.EncryptedSettings.Should().BeEmpty("a coleta tardia NÃO reescreveu o segredo eliminado");
        saved.Enabled.Should().BeFalse("continua desconectado/desabilitado");
        saved.LastStatus.Should().Be(ConnectorStatus.Healthy, "o carimbo histórico do sync é permitido");

        // E a PROJEÇÃO de leitura (a mesma que alimenta a UI) continua sem credencial: a coleta tardia registra
        // só o desfecho histórico (LastStatus), nunca reapresenta a conexão como operacional. A UI deriva
        // "Desconectado" da ausência de credencial — coberto no lado do frontend por connector-lifecycle.models.spec.
        var summary = (await ServiceFor(assert, TenantA).ListConnectorsAsync()).Single();
        summary.HasCredentials.Should().BeFalse("a projeção permanece sem credencial após a coleta tardia");
        summary.HasIngestionKey.Should().BeFalse();
        summary.Enabled.Should().BeFalse("a coleta tardia não habilita a conexão");
    }

    [Fact]
    public async Task SetConnectorEnabledAsync_HabilitarDesconectadoPull_EhMissingCredential_NaoAlteraLinha()
    {
        // Um conector PULL desconectado (credencial eliminada) não pode ser HABILITADO: habilitar não recria
        // segredo. A UI não oferece a ação, mas uma chamada direta à API não pode criar Enabled=true sem credencial.
        Guid connectorId;
        await using (var db = NewContext(TenantA))
        {
            var svc = ServiceFor(db, TenantA);
            connectorId = (await svc.ConfigureConnectorAsync(Command(settings: "segredo"))).ConnectorId;
            await svc.DisconnectConnectorAsync(connectorId);
        }

        await using var assert = NewContext(TenantA);
        var result = await ServiceFor(assert, TenantA).SetConnectorEnabledAsync(connectorId, enabled: true);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(ConnectorAdminStatus.MissingCredential, "pull sem EncryptedSettings não habilita");
        result.Connector.Should().BeNull("recusa não projeta estado");
        var saved = await assert.Connectors.SingleAsync();
        saved.Enabled.Should().BeFalse("a linha permanece intacta — nada foi habilitado");
        saved.EncryptedSettings.Should().BeEmpty("a recusa não fabricou credencial");
    }

    [Fact]
    public async Task SetConnectorEnabledAsync_HabilitarDesconectadoPush_EhMissingCredential_NaoAlteraLinha()
    {
        // Idem para o push genérico, cujo material de autenticação é a CHAVE DE INGESTÃO (hash). Desconectado,
        // sem hash, também não pode ser habilitado.
        Guid connectorId;
        await using (var db = NewContext(TenantA))
        {
            var svc = ServiceFor(db, TenantA);
            var pushCmd = new ConfigureConnectorCommand(
                ConnectorProvider.Generic, ConnectorCapability.Siem, "SIEM push",
                ConnectorAuthType.ApiKey, """{"ingestionKey":"chave-de-ingestao-de-alta-entropia-1234"}""");
            connectorId = (await svc.ConfigureConnectorAsync(pushCmd)).ConnectorId;
            await svc.DisconnectConnectorAsync(connectorId);
        }

        await using var assert = NewContext(TenantA);
        var result = await ServiceFor(assert, TenantA).SetConnectorEnabledAsync(connectorId, enabled: true);

        result.Status.Should().Be(ConnectorAdminStatus.MissingCredential, "push sem IngestionKeyHash não habilita");
        result.Connector.Should().BeNull();
        var saved = await assert.Connectors.SingleAsync();
        saved.Enabled.Should().BeFalse();
        saved.IngestionKeyHash.Should().BeNull();
    }

    [Fact]
    public async Task SetConnectorEnabledAsync_ReabilitarDesabilitadoCredenciado_Sucede()
    {
        // Contraprova: um conector apenas DESABILITADO (credencial preservada) PODE ser reabilitado — o guard
        // de credencial só barra o desconectado, nunca a pausa idempotente.
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        var created = await svc.ConfigureConnectorAsync(Command(settings: "segredo"));
        await svc.SetConnectorEnabledAsync(created.ConnectorId, enabled: false);

        var reenabled = await svc.SetConnectorEnabledAsync(created.ConnectorId, enabled: true);
        reenabled.Succeeded.Should().BeTrue("desabilitado com credencial reativa normalmente");
        reenabled.Connector!.Enabled.Should().BeTrue();
        reenabled.Connector.HasCredentials.Should().BeTrue();
    }

    [Fact]
    public async Task SetConnectorEnabledAsync_HabilitarDesconectado_NaoVazaSegredoNoDesfecho()
    {
        // A recusa de habilitação não pode ecoar segredo: o desfecho não projeta conector, e a mensagem de
        // ação orienta a reconectar sem citar credencial.
        Guid connectorId;
        await using (var db = NewContext(TenantA))
        {
            var svc = ServiceFor(db, TenantA);
            connectorId = (await svc.ConfigureConnectorAsync(Command(settings: "s3gr3d0-supersecreto"))).ConnectorId;
            await svc.DisconnectConnectorAsync(connectorId);
        }

        await using var assert = NewContext(TenantA);
        var result = await ServiceFor(assert, TenantA).SetConnectorEnabledAsync(connectorId, enabled: true);

        result.Connector.Should().BeNull("nenhuma projeção de estado na recusa");
        (result.Detail ?? "").Should().NotContain("s3gr3d0", "a mensagem orienta a reconectar, sem citar segredo");
    }

    [Fact]
    public async Task SetConnectorEnabledAsync_ConectorDeOutroTenant_EhNotFound()
    {
        Guid connectorId;
        await using (var db = NewContext(TenantA))
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command())).ConnectorId;

        await using (var db = NewContext(TenantB))
        {
            var result = await ServiceFor(db, TenantB).SetConnectorEnabledAsync(connectorId, enabled: true);
            result.Status.Should().Be(ConnectorAdminStatus.NotFound, "cross-tenant é indistinguível de inexistente");
        }
    }

    [Fact]
    public async Task DisconnectConnectorAsync_PreservaEvidenciaHistorica()
    {
        // A desconexão elimina só a CREDENCIAL — a proveniência histórica (sinais coletados) permanece.
        Guid connectorId;
        await using (var db = NewContext(TenantA))
        {
            connectorId = (await ServiceFor(db, TenantA).ConfigureConnectorAsync(Command(settings: "segredo"))).ConnectorId;
            await ServiceFor(db, TenantA).RecordSyncResultAsync(
                connectorId,
                new[]
                {
                    new EvidenceSignal
                    {
                        TenantId = TenantA, ConnectorConfigId = connectorId,
                        SignalKey = "secureScore.overall", NumericValue = 42,
                    },
                },
                ConnectorStatus.Healthy);
        }

        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA).DisconnectConnectorAsync(connectorId);

        await using var assert = NewContext(TenantA);
        (await assert.Signals.CountAsync()).Should().Be(1, "a evidência histórica é preservada na desconexão");
        (await assert.Connectors.CountAsync()).Should().Be(1, "a linha do conector também é preservada");
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
