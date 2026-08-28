using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Tenancy;

/// <summary>
/// Implementação do serviço de onboarding (ver <see cref="ITenantManagementService"/> para o contrato e
/// as decisões de desenho). Adapter da Infrastructure: é aqui que a porta encosta no DbContext.
///
/// Secure-by-design, nos mesmos termos do <c>ControlStateWriter</c>: a escrita de conector opera SEMPRE
/// dentro do tenant ambiente (Global Query Filter na leitura + stamping fail-closed no
/// <c>SaveChanges</c>), e o TenantId NUNCA é atribuído à mão — quem carimba é o
/// <see cref="AegisScoreDbContext"/>, que revalida contra o contexto e lança se houver divergência.
/// </summary>
public sealed class TenantManagementService : ITenantManagementService
{
    /// <summary>
    /// Piso do intervalo de coleta. Não é preferência de estilo: um intervalo de 0/1 minuto transforma o
    /// agendador num hot loop contra a API do cliente — throttling do lado dele, custo do nosso, e o
    /// conector acaba banido. O valor EFETIVO volta no resultado, então quem pediu menos fica sabendo.
    /// </summary>
    private const int MinimumSyncIntervalMinutes = 5;

    /// <summary>
    /// Slug: minúsculas, dígitos e hífens internos, 2–64 caracteres. É identificador público (URL, chave
    /// de onboarding), então o formato é restrito na origem — não adianta escapar depois em cada
    /// consumidor. <c>RegexOptions.Compiled</c> porque o padrão é estático e avaliado por provisionamento.
    /// </summary>
    private static readonly Regex SlugPattern = new(
        "^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AegisScoreDbContext _db;
    private readonly DbContextOptions<AegisScoreDbContext> _dbOptions;
    private readonly ITenantContext _tenant;
    private readonly IConnectorSecretProtector _secrets;
    private readonly ILogger<TenantManagementService> _log;

    public TenantManagementService(
        AegisScoreDbContext db,
        DbContextOptions<AegisScoreDbContext> dbOptions,
        ITenantContext tenant,
        IConnectorSecretProtector secrets,
        ILogger<TenantManagementService> log)
    {
        _db = db;
        _dbOptions = dbOptions;
        _tenant = tenant;
        _secrets = secrets;
        _log = log;
    }

    public async Task<TenantProvisioningResult> CreateTenantAsync(
        CreateTenantCommand command, CancellationToken ct = default)
    {
        var slug = NormalizeSlug(command.Slug);
        if (!SlugPattern.IsMatch(slug))
            return TenantProvisioningResult.InvalidSlug(slug);

        var name = (command.Name ?? "").Trim();
        if (name.Length == 0)
            return TenantProvisioningResult.InvalidSlug(slug);   // nome vazio: mesmo desfecho 400 da borda

        if (command.CreatorAccountId == Guid.Empty)
            // Sem criador resolvido não há a quem conceder o acesso administrativo — fail-closed.
            throw new TenantSecurityException("Criação de tenant sem identidade criadora resolvida (fail-closed).");

        // O tenant NASCE com um administrador. O membership do criador é ITenantOwned e precisa ser carimbado
        // com o tenant NOVO — não com o tenant AMBIENTE do operador, que o StampTenant do _db usaria (o
        // PlatformAdmin chega com o tenant DELE no contexto). Por isso a gravação usa um contexto DEDICADO
        // ligado ao id recém-gerado (mesmo padrão do AuthService.IssuePairAsync e dos workers). Tenant em si
        // é global (não carimbado); o membership é carimbado com newTenantId pelo SystemTenantContext abaixo.
        var newTenantId = Guid.NewGuid();
        await using var db = new AegisScoreDbContext(_dbOptions, new SystemTenantContext(newTenantId));

        // Fast-path do conflito. `Tenant` NÃO é ITenantOwned: não tem query filter, então esta consulta
        // enxerga a base inteira — é justamente o que a checagem de unicidade global exige.
        if (await db.Tenants.AsNoTracking().AnyAsync(x => x.Slug == slug, ct))
            return TenantProvisioningResult.SlugConflict(slug);

        // A identidade criadora precisa existir (FK do membership). Também alimenta o fallback do nome de
        // exibição quando a claim `name` não veio. IdentityAccount é global (sem query filter).
        var creator = await db.IdentityAccounts.FirstOrDefaultAsync(a => a.Id == command.CreatorAccountId, ct);
        if (creator is null)
            throw new TenantSecurityException("Identidade criadora inexistente (fail-closed).");

        // Cliente recém-criado nasce em ONBOARDING, não Active — quem o promove é o fim do onboarding.
        // (`AuthService` só barra login em Suspended, então o estado inicial não trava o acesso.)
        var tenant = new Tenant
        {
            Id = newTenantId,
            Name = name,
            Slug = slug,
            Status = TenantStatus.Onboarding,
        };
        var adminMembership = new User
        {
            // TenantId carimbado com newTenantId no SaveChanges (SystemTenantContext acima), nunca à mão.
            IdentityAccountId = creator.Id,
            DisplayName = ResolveCreatorDisplayName(command.CreatorDisplayName, creator.Email),
            Role = TenantRole.TenantAdmin,
            IsActive = true,
        };

        db.Tenants.Add(tenant);
        db.Users.Add(adminMembership);
        try
        {
            // ATÔMICO: ou nascem o tenant E o acesso administrativo do criador, ou nenhum dos dois.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Corrida perdida: outro provisionamento gravou o mesmo slug entre o AnyAsync e este INSERT. O
            // índice único de Tenant.Slug rejeitou a duplicata — resolve no MESMO conflito da checagem prévia.
            // Nada foi persistido (uma transação só).
            db.Entry(tenant).State = EntityState.Detached;
            db.Entry(adminMembership).State = EntityState.Detached;
            _log.LogWarning(ex,
                "Provisionamento concorrente do slug '{Slug}' rejeitado pelo índice único — tratado como conflito.",
                slug);
            return TenantProvisioningResult.SlugConflict(slug);
        }

        _log.LogInformation(
            "Onboarding: cliente '{Name}' provisionado como {Slug} ({TenantId}) em {Status}; criador {AccountId} " +
            "recebeu acesso TenantAdmin.",
            tenant.Name, tenant.Slug, tenant.Id, tenant.Status, creator.Id);

        return TenantProvisioningResult.Created(tenant.Id, tenant.Slug);
    }

    /// <summary>
    /// Nome de exibição do criador no novo tenant: a claim <c>name</c> quando válida; senão o e-mail da
    /// identidade (aparado ao teto da coluna). Nunca vazio — o membership sempre nasce com um rótulo.
    /// </summary>
    private static string ResolveCreatorDisplayName(string? claimName, string email)
    {
        if (TenantAccessPolicy.IsValidDisplayName(claimName))
            return TenantAccessPolicy.NormalizeDisplayName(claimName);
        return email.Length <= TenantAccessPolicy.MaxDisplayNameLength
            ? email
            : email[..TenantAccessPolicy.MaxDisplayNameLength];
    }

    public async Task<ConnectorConfigurationResult> ConfigureConnectorAsync(
        ConfigureConnectorCommand command, CancellationToken ct = default)
    {
        // Defesa em profundidade (idioma do ControlStateWriter): o stamping do SaveChanges já é
        // fail-closed, mas falhar AQUI dá a mensagem certa e evita montar a entidade à toa.
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Configuração de conector sem tenant resolvido no contexto (fail-closed).");

        var syncInterval = Math.Max(command.SyncIntervalMinutes, MinimumSyncIntervalMinutes);

        // Upsert pela chave natural. O query filter já restringe ao tenant ambiente — repetir
        // `c.TenantId == tenantId` seria redundante e mascararia a dependência do filtro.
        var config = await _db.Connectors.FirstOrDefaultAsync(
            c => c.Provider == command.Provider && c.Capability == command.Capability, ct);

        var created = config is null;
        if (config is null)
        {
            config = new ConnectorConfig
            {
                Provider = command.Provider,
                Capability = command.Capability,
                // TenantId é carimbado no SaveChanges (fail-closed) — nunca atribuído aqui.
            };
            _db.Connectors.Add(config);
        }

        Apply(config, isInsert: created);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (created)
        {
            // Corrida perdida no INSERT: outra configuração do MESMO (tenant, provider, capability)
            // venceu entre o nosso SELECT e este INSERT, e o índice único a rejeitou.
            //
            // A recuperação aqui é DIFERENTE da de CreateTenantAsync. Provisionar um tenant duas vezes é
            // erro real (409); "configurar este conector" é IDEMPOTENTE por intenção — o operador quer o
            // conector no estado que ele descreveu, não uma reclamação sobre quem chegou primeiro. Então
            // reconvergimos para UPDATE sobre a linha vencedora. Uma tentativa só: se o re-SELECT não
            // achar nada, a violação não foi a do índice natural e o erro sobe para o boundary global.
            _db.Entry(config).State = EntityState.Detached;

            var winner = await _db.Connectors.FirstOrDefaultAsync(
                c => c.Provider == command.Provider && c.Capability == command.Capability, ct);
            if (winner is null) throw;

            _log.LogWarning(ex,
                "Configuração concorrente do conector {Provider}/{Capability} no tenant {TenantId} — " +
                "INSERT rejeitado pelo índice único; reconvergindo para atualização do registro vigente.",
                command.Provider, command.Capability, tenantId);

            config = winner;
            created = false;
            // isInsert: false — numa reconvergência o segredo ausente PRESERVA o que o vencedor gravou,
            // em vez de zerá-lo. Perder a corrida não pode apagar credencial de ninguém.
            Apply(config, isInsert: false);
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "Onboarding: conector {Provider}/{Capability} {Action} para o tenant {TenantId} " +
            "(sync a cada {Interval} min, habilitado={Enabled}).",
            config.Provider, config.Capability, created ? "criado" : "reconfigurado",
            tenantId, config.SyncIntervalMinutes, config.Enabled);

        return Project(config, created);

        // Projeta o comando sobre a entidade. Local function: a reconvergência acima precisa reaplicar
        // exatamente as mesmas regras sobre OUTRA instância, e duplicá-las abriria espaço para divergir.
        void Apply(ConnectorConfig target, bool isInsert)
        {
            target.DisplayName = command.DisplayName;
            target.AuthType = command.AuthType;
            target.Enabled = command.Enabled;
            target.SyncIntervalMinutes = syncInterval;

            // Cifragem ESTÁTICA das credenciais (Data Protection). Segredo ausente numa reconfiguração
            // PRESERVA o vigente — ver ConfigureConnectorCommand. Na criação, ausência grava "" em vez do
            // ciframento de string vazia: `Protect("")` devolve um blob NÃO vazio, o que faria o TestAsync
            // dos conectores (que checa `IsNullOrWhiteSpace(EncryptedSettings)`) reportar "credenciais
            // presentes" para um conector que nunca recebeu nenhuma.
            if (string.IsNullOrWhiteSpace(command.Settings))
            {
                if (isInsert) target.EncryptedSettings = "";
                return;
            }

            // [AEGIS-AUD-020] A CHAVE DE INGESTÃO é EXCLUSIVA dos conectores genéricos de PUSH (Generic/Siem,
            // Generic/Edr). Só nesses casos ela é extraída dos settings, validada e persistida (como HASH) —
            // para qualquer outro provedor os settings são segredos "clássicos" e vão INTEIROS para a cifragem,
            // sem tratar um eventual campo `ingestionKey` como credencial de ingestão.
            var isGenericPush = command.Provider == ConnectorProvider.Generic
                && (command.Capability == ConnectorCapability.Siem || command.Capability == ConnectorCapability.Edr);

            if (!isGenericPush)
            {
                target.EncryptedSettings = _secrets.Protect(command.Settings);
                return;
            }

            // Extrai a chave ANTES de proteger o RESTO: a chave de entrada não precisa ser recuperável — só
            // seu HASH permanece. Uma chave nova ROTACIONA a anterior; settings SEM chave PRESERVA o hash
            // vigente (renomear ou mudar o intervalo não apaga a credencial de ingestão).
            var (remaining, ingestionKey) = ExtractIngestionKey(command.Settings);
            if (ingestionKey is not null)
            {
                if (!IngestionKey.MeetsPolicy(ingestionKey))
                    throw new WeakIngestionKeyException(
                        $"A chave de ingestão deve ter ao menos {IngestionKey.MinLength} caracteres.");
                target.IngestionKeyHash = IngestionKey.Hash(ingestionKey);
            }

            // O que sobra (se algo) são segredos "clássicos". Vazio (só a chave de ingestão trafegou) → não
            // fabrica um blob: "" na criação, preservado na reconfiguração.
            if (!string.IsNullOrWhiteSpace(remaining))
                target.EncryptedSettings = _secrets.Protect(remaining);
            else if (isInsert)
                target.EncryptedSettings = "";
        }
    }

    // ---- [AEGIS-MVP-MICROSOFT-HUB] Conexão Microsoft unificada -------------------------------------

    /// <summary>Serviços que compõem a família Microsoft (uma credencial comum, coletores independentes).</summary>
    private static readonly IReadOnlyList<ConnectorCapability> MicrosoftHubCapabilities = new[]
    {
        ConnectorCapability.SecureScore,
        ConnectorCapability.IdentityPosture,
        ConnectorCapability.VulnerabilityScanner,
        ConnectorCapability.Siem,
    };

    public async Task<IReadOnlyList<ConnectorConfigurationResult>> ConfigureMicrosoftHubAsync(
        ConfigureMicrosoftHubCommand command, CancellationToken ct = default)
    {
        // Fail-closed antes de qualquer escrita (mesmo idioma de ConfigureConnectorAsync): sem tenant no
        // contexto não há a quem vincular os conectores.
        _ = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Configuração da conexão Microsoft sem tenant resolvido no contexto (fail-closed).");

        // Credencial comum: as três partes são obrigatórias. O usuário as informa UMA vez; o backend as replica
        // (cifradas) em cada serviço. Mensagem NUNCA ecoa o segredo.
        var tenantId = (command.TenantId ?? "").Trim();
        var clientId = (command.ClientId ?? "").Trim();
        var clientSecret = command.ClientSecret ?? "";
        if (tenantId.Length == 0 || clientId.Length == 0 || string.IsNullOrWhiteSpace(clientSecret))
            throw new MicrosoftHubValidationException(
                "Informe Directory (tenant) ID, Application (client) ID e Client secret da conexão Microsoft.");

        if (command.Services is null || command.Services.Count == 0)
            throw new MicrosoftHubValidationException("Selecione ao menos um serviço Microsoft para conectar.");

        // Valida TODA a seleção ANTES de escrever qualquer filho: uma seleção inválida não pode deixar
        // conectores meio-configurados.
        var seen = new HashSet<ConnectorCapability>();
        foreach (var svc in command.Services)
        {
            if (!MicrosoftHubCapabilities.Contains(svc.Capability))
                throw new MicrosoftHubValidationException(
                    $"Capacidade '{svc.Capability}' não pertence à conexão Microsoft.");
            if (!seen.Add(svc.Capability))
                throw new MicrosoftHubValidationException(
                    $"Serviço '{svc.Capability}' repetido na seleção Microsoft.");

            // workspaceId é OBRIGATÓRIO e EXCLUSIVO do Sentinel (Siem). Ausente ⇒ falha SÓ para o Sentinel.
            if (svc.Capability == ConnectorCapability.Siem && string.IsNullOrWhiteSpace(svc.WorkspaceId))
                throw new MicrosoftHubValidationException(
                    "O Microsoft Sentinel exige o Log Analytics Workspace ID.");
        }

        // Fan-out: um upsert INDEPENDENTE por serviço (cada um com SaveChanges próprio, isolado). O provider é
        // derivado da capacidade; o blob cifrado leva a credencial comum + (só no Sentinel) o workspaceId.
        var results = new List<ConnectorConfigurationResult>(command.Services.Count);
        foreach (var svc in command.Services)
        {
            var provider = ProviderFor(svc.Capability);
            var settings = BuildMicrosoftChildSettings(
                tenantId, clientId, clientSecret,
                svc.Capability == ConnectorCapability.Siem ? svc.WorkspaceId!.Trim() : null);

            var displayName = string.IsNullOrWhiteSpace(svc.DisplayName)
                ? DefaultDisplayNameFor(svc.Capability)
                : svc.DisplayName!.Trim();

            var result = await ConfigureConnectorAsync(
                new ConfigureConnectorCommand(
                    provider, svc.Capability, displayName,
                    ConnectorAuthType.OAuthClientCredentials, settings, svc.SyncIntervalMinutes, Enabled: true),
                ct);
            results.Add(result);
        }

        _log.LogInformation(
            "Conexão Microsoft unificada aplicada: {Count} serviço(s) configurado(s) com a credencial comum.",
            results.Count);
        return results;
    }

    /// <summary>Provider da família Microsoft por capacidade: Siem ⇒ MicrosoftSentinel; demais ⇒ Microsoft.</summary>
    private static ConnectorProvider ProviderFor(ConnectorCapability capability) =>
        capability == ConnectorCapability.Siem ? ConnectorProvider.MicrosoftSentinel : ConnectorProvider.Microsoft;

    /// <summary>Rótulo canônico de cada serviço Microsoft (fallback quando a interface não envia um nome).</summary>
    private static string DefaultDisplayNameFor(ConnectorCapability capability) => capability switch
    {
        ConnectorCapability.SecureScore          => "Microsoft 365 · Secure Score",
        ConnectorCapability.IdentityPosture      => "Microsoft Entra ID · AEGIS KNIGHT",
        ConnectorCapability.VulnerabilityScanner => "Microsoft Defender Vulnerability Management",
        ConnectorCapability.Siem                 => "Microsoft Sentinel · SIEM",
        _                                        => "Microsoft",
    };

    /// <summary>
    /// Monta o blob de settings de UM filho Microsoft: credencial comum (tenantId/clientId/clientSecret) e — SÓ no
    /// Sentinel — o <c>workspaceId</c>. camelCase para casar com o que a interface envia; os conectores decifram
    /// com <c>PropertyNameCaseInsensitive</c>. Nunca escreve workspaceId nos serviços que não são Sentinel.
    /// </summary>
    private static string BuildMicrosoftChildSettings(
        string tenantId, string clientId, string clientSecret, string? workspaceId)
    {
        // Dictionary ordenado por inserção → JSON estável e legível; JsonSerializer escapa os valores.
        var map = new Dictionary<string, string>
        {
            ["tenantId"] = tenantId,
            ["clientId"] = clientId,
            ["clientSecret"] = clientSecret,
        };
        if (!string.IsNullOrWhiteSpace(workspaceId))
            map["workspaceId"] = workspaceId!;
        return JsonSerializer.Serialize(map);
    }

    /// <summary>
    /// [AEGIS-AUD-020] Separa a chave de ingestão (<c>ingestionKey</c>) do restante dos settings. Devolve o
    /// RESTANTE re-serializado (ou <c>null</c> quando nada sobra) e a chave em claro (ou <c>null</c>). Um blob
    /// que não seja um objeto JSON é tratado como opaco (sem chave) — nunca falha por formato do conteúdo.
    /// </summary>
    private static (string? Remaining, string? IngestionKey) ExtractIngestionKey(string settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(settings);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (settings, null);

            string? key = null;
            var rest = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "ingestionKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        key = prop.Value.GetString();
                }
                else
                {
                    rest[prop.Name] = prop.Value.Clone();   // Clone: sobrevive ao dispose do JsonDocument
                }
            }

            var remaining = rest.Count == 0 ? null : JsonSerializer.Serialize(rest);
            return (remaining, string.IsNullOrWhiteSpace(key) ? null : key);
        }
        catch (JsonException)
        {
            return (settings, null);
        }
    }

    public async Task<IReadOnlyList<ConnectorSummary>> ListConnectorsAsync(CancellationToken ct = default)
    {
        // Query filter restringe ao tenant ambiente — sem tenant resolvido devolve vazio (fail-closed),
        // e não uma listagem de outro cliente. Projeta em ANÔNIMO no SQL e monta o record em memória:
        // projetar direto num record dentro da consulta é o que o EF 8 falhou em traduzir na §22.
        var rows = await _db.Connectors.AsNoTracking()
            .OrderBy(c => c.DisplayName)
            .Select(c => new
            {
                c.Id, c.Provider, c.Capability, c.DisplayName, c.AuthType,
                c.Enabled, c.SyncIntervalMinutes, c.LastSyncAt, c.LastStatus,
                // Só o BOOLEANO atravessa a fronteira — nunca o blob/hash, nem cifrado.
                HasCredentials = c.EncryptedSettings != "",
                HasIngestionKey = c.IngestionKeyHash != null,
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new ConnectorSummary(
                r.Id, r.Provider, r.Capability, r.DisplayName, r.AuthType,
                r.Enabled, r.SyncIntervalMinutes, r.LastSyncAt, r.LastStatus, r.HasCredentials,
                r.HasIngestionKey))
            .ToList();
    }

    public Task<ConnectorConfig?> GetConnectorAsync(Guid connectorId, CancellationToken ct = default) =>
        // Sem `AsNoTracking`: o chamador (sync) reusa a instância rastreada em RecordSyncResultAsync,
        // dentro do mesmo escopo — o change tracker evita um segundo SELECT.
        _db.Connectors.FirstOrDefaultAsync(c => c.Id == connectorId, ct);

    public async Task<bool> RecordSyncResultAsync(
        Guid connectorId, IReadOnlyList<EvidenceSignal> signals, ConnectorStatus status,
        CancellationToken ct = default)
    {
        var config = await _db.Connectors.FirstOrDefaultAsync(c => c.Id == connectorId, ct);
        if (config is null) return false;

        if (signals.Count > 0)
            _db.Signals.AddRange(signals);

        config.LastSyncAt = DateTimeOffset.UtcNow;
        config.LastStatus = status;

        // Uma transação só: os sinais e o carimbo de sync são o MESMO fato.
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Coleta do conector {Provider}/{Capability} ({ConnectorId}) concluída como {Status}: {Count} sinais.",
            config.Provider, config.Capability, config.Id, status, signals.Count);

        return true;
    }

    /// <summary>Normaliza o slug para a forma canônica comparada pelo índice único.</summary>
    private static string NormalizeSlug(string? raw) => (raw ?? "").Trim().ToLowerInvariant();

    /// <summary>Projeção de saída SEM o blob de credenciais (ver <see cref="ConnectorConfigurationResult"/>).</summary>
    private static ConnectorConfigurationResult Project(ConnectorConfig c, bool created) => new(
        c.Id, created, c.Provider, c.Capability, c.DisplayName, c.AuthType,
        c.Enabled, c.SyncIntervalMinutes, c.LastSyncAt, c.LastStatus,
        // Estado REAL após a escrita: numa reconfiguração sem segredo, o vigente foi preservado — dizer
        // "sem credencial" porque o cliente não reenviou seria mentira.
        HasCredentials: !string.IsNullOrWhiteSpace(c.EncryptedSettings),
        HasIngestionKey: !string.IsNullOrWhiteSpace(c.IngestionKeyHash));
}
