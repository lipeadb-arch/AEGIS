using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
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
    private readonly ITenantContext _tenant;
    private readonly IConnectorSecretProtector _secrets;
    private readonly ILogger<TenantManagementService> _log;

    public TenantManagementService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        IConnectorSecretProtector secrets,
        ILogger<TenantManagementService> log)
    {
        _db = db;
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

        // Fast-path do conflito. `Tenant` NÃO é ITenantOwned: não tem query filter, então esta consulta
        // enxerga a base inteira — é justamente o que a checagem de unicidade global exige.
        if (await _db.Tenants.AsNoTracking().AnyAsync(x => x.Slug == slug, ct))
            return TenantProvisioningResult.SlugConflict(slug);

        // Propriedades padrão explícitas: cliente recém-criado nasce em ONBOARDING, não Active — quem o
        // promove é o fim do fluxo de onboarding. (`AuthService` só barra login em Suspended, então o
        // estado inicial não trava o acesso; ele apenas conta a verdade sobre a maturidade do cadastro.)
        //
        // ⚠️ Só o agregado raiz é criado aqui. Um PlatformAdmin chega com o tenant DELE no contexto
        // (o TenantConsistencyMiddleware exige a claim), logo qualquer entidade ITenantOwned semeada
        // nesta chamada seria carimbada com o tenant do OPERADOR, não com o recém-criado. Semente de
        // dados do novo cliente exige um SystemTenantContext(novoId) — fora do escopo deste método.
        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            Status = TenantStatus.Onboarding,
        };

        _db.Tenants.Add(tenant);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Corrida perdida: outro provisionamento gravou o mesmo slug entre o AnyAsync e este INSERT.
            // O índice único de Tenant.Slug rejeitou a duplicata — resolve no MESMO conflito da checagem
            // prévia (idioma do dedupe de GovernanceDocument). Detach para não reenviar o INSERT num
            // SaveChanges posterior do mesmo escopo.
            _db.Entry(tenant).State = EntityState.Detached;
            _log.LogWarning(ex,
                "Provisionamento concorrente do slug '{Slug}' rejeitado pelo índice único — tratado como conflito.",
                slug);
            return TenantProvisioningResult.SlugConflict(slug);
        }

        _log.LogInformation(
            "Onboarding: cliente '{Name}' provisionado como {Slug} ({TenantId}) em {Status}.",
            tenant.Name, tenant.Slug, tenant.Id, tenant.Status);

        return TenantProvisioningResult.Created(tenant.Id, tenant.Slug);
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
            if (!string.IsNullOrWhiteSpace(command.Settings))
                target.EncryptedSettings = _secrets.Protect(command.Settings);
            else if (isInsert)
                target.EncryptedSettings = "";
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
                // Só o BOOLEANO atravessa a fronteira — nunca o blob, nem cifrado.
                HasCredentials = c.EncryptedSettings != "",
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new ConnectorSummary(
                r.Id, r.Provider, r.Capability, r.DisplayName, r.AuthType,
                r.Enabled, r.SyncIntervalMinutes, r.LastSyncAt, r.LastStatus, r.HasCredentials))
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
        HasCredentials: !string.IsNullOrWhiteSpace(c.EncryptedSettings));
}
