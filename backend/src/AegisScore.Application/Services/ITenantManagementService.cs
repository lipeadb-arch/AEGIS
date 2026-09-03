using AegisScore.Domain;

namespace AegisScore.Application.Services;

// ---- Comandos de entrada ----------------------------------------------------

/// <summary>
/// Provisionamento de um novo cliente. O <paramref name="Slug"/> chega CRU do onboarding — a
/// normalização (trim + minúsculas) e a validação de formato são responsabilidade do serviço, não do
/// chamador: é o slug NORMALIZADO que o índice único de <c>Tenant.Slug</c> compara, então normalizar
/// na borda deixaria "Acme" e "acme" conviverem como dois clientes distintos.
/// </summary>
/// <param name="CreatorAccountId">
/// Identidade autenticada que cria o ambiente. Recebe um membership <c>TenantAdmin</c> no tenant
/// recém-criado, ATOMICAMENTE — sem depender de o novo tenant já estar no token. Vem da claim
/// <c>account_id</c>, NUNCA do corpo.
/// </param>
/// <param name="CreatorDisplayName">
/// Nome de exibição do criador no novo tenant (da claim <c>name</c>; fallback ao e-mail da identidade).
/// </param>
public record CreateTenantCommand(
    string Name, string Slug, Guid CreatorAccountId, string? CreatorDisplayName = null);

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

/// <summary>
/// [AEGIS-MVP-MICROSOFT-HUB] Seleção de UM serviço Microsoft dentro da conexão unificada. A capacidade é a
/// chave (cada capacidade Microsoft aparece uma única vez na família): <see cref="ConnectorCapability.SecureScore"/>,
/// <see cref="ConnectorCapability.IdentityPosture"/>, <see cref="ConnectorCapability.VulnerabilityScanner"/> e
/// <see cref="ConnectorCapability.Siem"/> (Microsoft Sentinel). O provider é DERIVADO da capacidade pelo serviço —
/// Siem ⇒ <see cref="ConnectorProvider.MicrosoftSentinel"/>, as demais ⇒ <see cref="ConnectorProvider.Microsoft"/>.
///
/// <paramref name="WorkspaceId"/> é EXCLUSIVO do Sentinel (Log Analytics workspace): obrigatório para o Siem e
/// ignorado nas demais — nunca entra no blob cifrado dos outros serviços, para não contaminá-los.
/// </summary>
public record MicrosoftHubServiceSelection(
    ConnectorCapability Capability,
    int SyncIntervalMinutes = 360,
    string? WorkspaceId = null,
    string? DisplayName = null);

/// <summary>
/// [AEGIS-MVP-MICROSOFT-HUB] Configuração da CONEXÃO MICROSOFT UNIFICADA. O usuário informa a credencial comum
/// (<paramref name="TenantId"/>/<paramref name="ClientId"/>/<paramref name="ClientSecret"/>) UMA vez, e o backend
/// a aplica+cifra em cada serviço selecionado (fan-out sobre <see cref="ITenantManagementService.ConfigureConnectorAsync"/>,
/// upsert pela chave natural — sem duplicatas). Cada serviço permanece um <c>ConnectorConfig</c> INDEPENDENTE
/// (habilitação, intervalo, última sync, status, erro, testar/sincronizar e isolamento de falha próprios).
///
/// ⚠️ O segredo trafega em claro só sob o TLS e é cifrado NO SERVIDOR; nunca retorna pela API nem vai a log.
/// Configurações específicas de uma capacidade (o <c>workspaceId</c> do Sentinel) NÃO contaminam as demais.
/// </summary>
public record ConfigureMicrosoftHubCommand(
    string TenantId,
    string ClientId,
    string ClientSecret,
    IReadOnlyList<MicrosoftHubServiceSelection> Services);

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
    bool HasCredentials,
    // [AEGIS-AUD-020] Há chave de ingestão configurada? (só o booleano — a chave nunca sai). Distingue um
    // conector genérico de push pronto para receber de um ainda sem credencial própria.
    bool HasIngestionKey);

/// <summary>
/// [AEGIS-AUD-020] A chave de ingestão fornecida não atende à política mínima de entropia/comprimento. É um
/// resultado ESPERADO da borda (400), não uma falha excepcional — o controller a traduz numa mensagem clara.
/// </summary>
public sealed class WeakIngestionKeyException : Exception
{
    public WeakIngestionKeyException(string message) : base(message) { }
}

/// <summary>
/// [AEGIS-MVP-MICROSOFT-HUB] A configuração da conexão Microsoft unificada é inválida (credencial comum ausente,
/// nenhum serviço selecionado, capacidade fora da família Microsoft, capacidade repetida ou <c>workspaceId</c>
/// ausente para o Sentinel). É resultado ESPERADO da borda (400), não falha excepcional; a mensagem descreve o
/// problema SEM ecoar segredo/credencial. Mesmo idioma de <see cref="WeakIngestionKeyException"/>.
/// </summary>
public sealed class MicrosoftHubValidationException : Exception
{
    public MicrosoftHubValidationException(string message) : base(message) { }
}

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
    bool HasCredentials,
    bool HasIngestionKey);

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Edição administrativa de um conector do tenant ambiente: só nome de
/// exibição e intervalo de coleta. ⚠️ NÃO carrega segredo — editar um conector jamais reescreve a credencial
/// (a rotação é o caminho explícito de <see cref="ConfigureConnectorCommand"/>). O alvo é o
/// <see cref="ConnectorId"/> resolvido DENTRO do tenant ambiente (query filter fail-closed).
/// </summary>
public record UpdateConnectorCommand(Guid ConnectorId, string DisplayName, int SyncIntervalMinutes);

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Desfecho das ações administrativas de conector (editar/habilitar/desabilitar/
/// desconectar). Como no resto do modelo, recusas ESPERADAS viajam como VALOR (o boundary global traduziria um
/// throw num 500 opaco); só <see cref="TenantSecurityException"/> sobe. <see cref="Connector"/> só vem no sucesso.
/// </summary>
public enum ConnectorAdminStatus
{
    /// <summary>Ação aplicada (idempotente).</summary>
    Updated = 0,

    /// <summary>Conector inexistente OU de OUTRO tenant — indistinguíveis por design (404 na borda).</summary>
    NotFound = 1,

    /// <summary>Nome de exibição ausente ou acima do teto (400).</summary>
    InvalidDisplayName = 2,

    /// <summary>
    /// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Tentativa de HABILITAR um conector DESCONECTADO — sem material de
    /// autenticação compatível (o pull precisa de <c>EncryptedSettings</c>; o push genérico, de
    /// <c>IngestionKeyHash</c>). Habilitar não recria credencial: reconectar é o caminho explícito. A linha
    /// NÃO é alterada. Conflito de estado ESPERADO (409 na borda), não falha excepcional.
    /// </summary>
    MissingCredential = 3,
}

/// <summary>Resultado de uma ação administrativa de conector. <see cref="Connector"/> traz o estado APÓS a escrita (sem segredo).</summary>
public record ConnectorAdminResult(ConnectorAdminStatus Status, ConnectorSummary? Connector = null, string? Detail = null)
{
    public bool Succeeded => Status == ConnectorAdminStatus.Updated;

    public static ConnectorAdminResult Ok(ConnectorSummary connector) => new(ConnectorAdminStatus.Updated, connector);

    public static ConnectorAdminResult Rejected(ConnectorAdminStatus status, string? detail = null) =>
        new(status, null, detail);
}

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
    /// <c>Active</c> quando o onboarding fecha) e slug normalizado, E concede ao criador
    /// (<see cref="CreateTenantCommand.CreatorAccountId"/>) um membership <c>TenantAdmin</c> no ambiente
    /// recém-criado — as DUAS escritas numa ÚNICA transação (sem tenant órfão sem administrador).
    ///
    /// O membership do criador é <see cref="ITenantOwned"/> e precisa ser carimbado com o tenant NOVO, não
    /// com o tenant ambiente do operador — por isso a gravação usa um contexto ligado ao tenant recém-criado
    /// (mesmo padrão dos workers). A separação global × tenant-scoped é preservada: o <c>PlatformRole</c> da
    /// identidade NÃO é tocado.
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
    /// [AEGIS-MVP-MICROSOFT-HUB] Configura a CONEXÃO MICROSOFT UNIFICADA: uma credencial comum informada uma vez é
    /// aplicada+cifrada em cada serviço Microsoft selecionado. Internamente é um FAN-OUT sobre
    /// <see cref="ConfigureConnectorAsync"/> (um upsert por serviço, pela chave natural — sem duplicatas), então
    /// cada serviço permanece um conector INDEPENDENTE com estado/sincronização/falha próprios. NÃO existe uma
    /// operação que carregue todos os serviços numa mesma transação/unidade de memória — cada filho é uma escrita
    /// isolada e idempotente.
    ///
    /// O <c>workspaceId</c> só entra no blob do Sentinel (Siem); as demais capacidades recebem apenas a credencial
    /// comum. Devolve o resultado (sem segredo) de cada serviço configurado, na ordem pedida.
    /// </summary>
    /// <exception cref="MicrosoftHubValidationException">Entrada inválida (borda 400) — ver a exceção.</exception>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<IReadOnlyList<ConnectorConfigurationResult>> ConfigureMicrosoftHubAsync(
        ConfigureMicrosoftHubCommand command, CancellationToken ct = default);

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

    // ---- [AEGIS-MVP-ADMIN-LIFECYCLE-01] Ciclo de vida administrativo do conector (tenant-scoped) ----

    /// <summary>
    /// Edita APENAS o nome de exibição e o intervalo de coleta de um conector do tenant ambiente. NUNCA toca a
    /// credencial: editar sem enviar segredo PRESERVA o vigente por construção (esta operação não conhece o
    /// segredo). O intervalo recebe o mesmo piso de segurança da configuração. Conector inexistente/cross-tenant
    /// → <see cref="ConnectorAdminStatus.NotFound"/> (nada gravado).
    /// </summary>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<ConnectorAdminResult> UpdateConnectorAsync(UpdateConnectorCommand command, CancellationToken ct = default);

    /// <summary>
    /// Habilita ou desabilita (pausa) um conector do tenant ambiente. Desabilitar INTERROMPE novas coletas
    /// (os workers e a ingestão push já respeitam <c>Enabled</c>), mas PRESERVA a credencial para futura
    /// reativação — não é desconexão; é sempre idempotente (desabilitar o já-desabilitado é sucesso sem efeito).
    /// HABILITAR EXIGE material de autenticação compatível: um conector DESCONECTADO (pull sem
    /// <c>EncryptedSettings</c>, ou push genérico sem <c>IngestionKeyHash</c>) NÃO pode ser habilitado —
    /// habilitar não recria credencial. Nesse caso a linha permanece intacta e o desfecho é
    /// <see cref="ConnectorAdminStatus.MissingCredential"/> (reconecte pelo fluxo de configuração). Habilitar o
    /// já-habilitado e credenciado é sucesso sem efeito.
    /// </summary>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<ConnectorAdminResult> SetConnectorEnabledAsync(
        Guid connectorId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// DESCONECTA um conector do tenant ambiente: desabilita E elimina TODO o material secreto armazenado —
    /// <c>EncryptedSettings</c> e <c>IngestionKeyHash</c> — mas PRESERVA a linha e a proveniência histórica
    /// (sinais, exposições, vulnerabilidades, cobertura). NÃO há exclusão física de <c>ConnectorConfig</c> nem
    /// das evidências que apontam para ele. Reconfigurar depois REATIVA a MESMA linha (upsert pela chave
    /// natural), sem duplicar. Idempotente. Uma coleta em andamento que conclua depois NÃO ressuscita a conexão:
    /// o registro de sync só carimba <c>LastSyncAt</c>/<c>LastStatus</c> e nunca reescreve o segredo, então o
    /// estado de conexão permanece derivado da ausência de credencial.
    /// </summary>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<ConnectorAdminResult> DisconnectConnectorAsync(Guid connectorId, CancellationToken ct = default);
}
