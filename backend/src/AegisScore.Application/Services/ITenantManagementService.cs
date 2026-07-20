using AegisScore.Domain;

namespace AegisScore.Application.Services;

// ---- Comandos de entrada ----------------------------------------------------

/// <summary>
/// Provisionamento de um novo cliente. O <paramref name="Slug"/> chega CRU do onboarding — a
/// normalização (trim + minúsculas) e a validação de formato são responsabilidade do serviço, não do
/// chamador: é o slug NORMALIZADO que o índice único de <c>Tenant.Slug</c> compara, então normalizar
/// na borda deixaria "Acme" e "acme" conviverem como dois clientes distintos.
/// </summary>
public record CreateTenantCommand(string Name, string Slug);

/// <summary>
/// Configuração (criação OU atualização) de um conector do tenant ambiente.
///
/// ⚠️ <paramref name="Settings"/> trafega em CLARO (protegido pelo TLS) e é cifrado NO SERVIDOR antes
/// de persistir — nunca se confia num blob "já cifrado" pelo cliente. Em uma reconfiguração, deixá-lo
/// vazio PRESERVA o segredo vigente: rotação de credencial é ato explícito, não efeito colateral de
/// quem só quis renomear o conector ou mudar o intervalo de sync.
/// </summary>
public record ConfigureConnectorCommand(
    ConnectorProvider Provider,
    ConnectorCapability Capability,
    string DisplayName,
    ConnectorAuthType AuthType,
    string? Settings,
    int SyncIntervalMinutes = 360,
    bool Enabled = true);

// ---- Resultados de saída ----------------------------------------------------

/// <summary>
/// Desfecho do provisionamento. Slug duplicado e slug malformado são resultados ESPERADOS do fluxo de
/// onboarding (409 e 400 na borda HTTP), não falhas excepcionais — por isso viajam como valor e não como
/// exceção: o <c>GlobalExceptionHandlingMiddleware</c> traduziria qualquer throw num 500 opaco.
/// </summary>
public enum TenantProvisioningStatus { Created = 0, SlugAlreadyInUse = 1, InvalidSlug = 2 }

/// <summary>
/// Resultado do provisionamento. O <paramref name="Slug"/> é sempre o NORMALIZADO — o que de fato
/// colidiu no índice único —, para que a mensagem de conflito descreva o que o banco viu.
/// </summary>
public record TenantProvisioningResult(TenantProvisioningStatus Status, Guid TenantId, string Slug)
{
    public bool Succeeded => Status == TenantProvisioningStatus.Created;

    public static TenantProvisioningResult Created(Guid id, string slug) =>
        new(TenantProvisioningStatus.Created, id, slug);

    public static TenantProvisioningResult SlugConflict(string slug) =>
        new(TenantProvisioningStatus.SlugAlreadyInUse, Guid.Empty, slug);

    public static TenantProvisioningResult InvalidSlug(string slug) =>
        new(TenantProvisioningStatus.InvalidSlug, Guid.Empty, slug);
}

/// <summary>
/// Conector configurado, na visão de LEITURA (tela de integrações).
///
/// <paramref name="HasCredentials"/> responde "este conector tem segredo guardado?" sem revelar nada
/// do segredo — é o que a UI precisa para distinguir "configurado" de "cadastrado mas sem credencial",
/// e é exatamente a checagem que o <c>TestAsync</c> dos conectores faz.
/// </summary>
public record ConnectorSummary(
    Guid ConnectorId,
    ConnectorProvider Provider,
    ConnectorCapability Capability,
    string DisplayName,
    ConnectorAuthType AuthType,
    bool Enabled,
    int SyncIntervalMinutes,
    DateTimeOffset? LastSyncAt,
    ConnectorStatus LastStatus,
    bool HasCredentials);

/// <summary>
/// Projeção SEGURA de um conector configurado. Deliberadamente SEM <c>EncryptedSettings</c>: o segredo
/// (cifrado ou não) não tem por que atravessar a fronteira de saída da aplicação — só o coletor o
/// decifra, no momento da coleta.
/// </summary>
/// <param name="Created">True quando a chamada INSERIU o conector; false quando reconfigurou o existente.</param>
/// <param name="SyncIntervalMinutes">O intervalo EFETIVO após o piso de segurança — pode diferir do pedido.</param>
public record ConnectorConfigurationResult(
    Guid ConnectorId,
    bool Created,
    ConnectorProvider Provider,
    ConnectorCapability Capability,
    string DisplayName,
    ConnectorAuthType AuthType,
    bool Enabled,
    int SyncIntervalMinutes,
    DateTimeOffset? LastSyncAt,
    ConnectorStatus LastStatus,
    bool HasCredentials);

// ---- Porta ------------------------------------------------------------------

/// <summary>
/// Serviço de aplicação do ONBOARDING: provisionamento de clientes (tenants) e configuração dos seus
/// conectores. Concentra três regras que antes viviam soltas no <c>TenantsController</c>:
/// normalização/unicidade do slug, cifragem estática das credenciais e vínculo ao tenant correto.
///
/// <b>Isolamento (Zero Trust).</b> Com exceção de <see cref="CreateTenantAsync"/> — operação de
/// PLATAFORMA, anterior à existência do tenant —, todos os métodos operam EXCLUSIVAMENTE no tenant
/// ambiente (claim <c>tenant_id</c> do JWT, via <see cref="Abstractions.ITenantContext"/>). Nenhum
/// método aceita um <c>tenantId</c> por parâmetro: o que não trafega não pode ser forjado, e é isso que
/// elimina o IDOR na raiz em vez de mitigá-lo com uma checagem que alguém esquece de repetir.
///
/// A implementação vive na Infrastructure (toca o DbContext); a porta, aqui — mesmo desenho de
/// <see cref="IControlStateWriter"/>.
/// </summary>
public interface ITenantManagementService
{
    /// <summary>
    /// Provisiona um cliente com os padrões corretos: <c>Status = Onboarding</c> (só vira
    /// <c>Active</c> quando o onboarding fecha) e slug normalizado.
    ///
    /// A unicidade do slug é invariante de BANCO (índice único em <c>Tenant.Slug</c>): a checagem prévia
    /// é só fast-path, e a corrida perdida entre o SELECT e o INSERT resolve no MESMO
    /// <see cref="TenantProvisioningStatus.SlugAlreadyInUse"/> — mesmo idioma do dedupe de
    /// <c>GovernanceDocument</c>.
    /// </summary>
    Task<TenantProvisioningResult> CreateTenantAsync(CreateTenantCommand command, CancellationToken ct = default);

    /// <summary>
    /// Configura um conector do tenant ambiente, cifrando as credenciais em repouso via
    /// <see cref="Abstractions.IConnectorSecretProtector"/>.
    ///
    /// É um UPSERT pela chave natural (tenant, <c>Provider</c>, <c>Capability</c>): "configurar" o mesmo
    /// provedor+capacidade duas vezes RECONFIGURA, não empilha. Duplicatas seriam ambíguas para o
    /// <see cref="Abstractions.IConnectorRegistry"/> e fariam o <c>PolicyIngestionWorker</c> sincronizar
    /// a mesma integração N vezes por ciclo.
    ///
    /// A unicidade é invariante de BANCO (índice único <c>(TenantId, Provider, Capability)</c>), não
    /// promessa do read-then-write: o SELECT prévio é fast-path e a corrida perdida no INSERT reconverge
    /// para UPDATE sobre a linha vencedora. Configurar é IDEMPOTENTE por intenção — duas chamadas
    /// simultâneas convergem para uma linha só, e nenhuma delas falha.
    /// </summary>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<ConnectorConfigurationResult> ConfigureConnectorAsync(
        ConfigureConnectorCommand command, CancellationToken ct = default);

    /// <summary>
    /// Lista os conectores do tenant ambiente (Global Query Filter). Somente leitura e SEM segredo —
    /// alimenta a tela de integrações. Sem parâmetro de tenant: o que não trafega não pode ser forjado.
    /// </summary>
    Task<IReadOnlyList<ConnectorSummary>> ListConnectorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolve um conector DENTRO do tenant ambiente (Global Query Filter fail-closed). Devolve
    /// <c>null</c> quando o id não existe OU pertence a outro cliente — os dois casos são
    /// indistinguíveis por design: a borda responde 404 em ambos, sem confirmar a existência de um
    /// recurso alheio.
    /// </summary>
    Task<ConnectorConfig?> GetConnectorAsync(Guid connectorId, CancellationToken ct = default);

    /// <summary>
    /// Persiste o desfecho de uma coleta: grava os sinais colhidos e atualiza a telemetria operacional
    /// do conector (<c>LastSyncAt</c>/<c>LastStatus</c>) numa ÚNICA transação — sinais sem o carimbo de
    /// sync, ou o inverso, descreveriam um estado que não aconteceu.
    /// </summary>
    /// <param name="signals">Sinais colhidos; lista vazia é válida (coleta sem novidade, ou falha).</param>
    /// <returns>False quando o conector não existe no tenant ambiente — nada foi gravado.</returns>
    Task<bool> RecordSyncResultAsync(
        Guid connectorId, IReadOnlyList<EvidenceSignal> signals, ConnectorStatus status,
        CancellationToken ct = default);
}
