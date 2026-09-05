using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Um documento de política/norma PUXADO de uma fonte externa de governança (SharePoint, Google
/// Workspace, Confluence, Egnyte…), já normalizado na borda de integração. É o contrato AGNÓSTICO de
/// fonte: o núcleo do Aegis Score nunca vê a API do fornecedor — só este DTO. O worker de ingestão o
/// converte num <c>GovernanceDocument</c> (Source = Integracao), guarda o binário e o enfileira para a
/// leitura da IA — a partir daqui é o MESMO pipeline do upload manual (teto documental de 50%).
/// </summary>
/// <param name="Title">Título de exibição do documento na origem.</param>
/// <param name="SourceReference">Referência estável na origem (URL do item no SharePoint, pageId do Confluence…) — rastreabilidade + dedupe lógico.</param>
/// <param name="FileName">Nome do arquivo; o extrator escolhe o parser por ele (extensão) e pelo ContentType.</param>
/// <param name="ContentType">MIME do conteúdo ("application/pdf", "text/plain"…), lido pelo <see cref="AegisScore.Application.Abstractions.IDocumentTextExtractor"/>.</param>
/// <param name="Type">Natureza do documento (Política, Norma, Contrato…), preservada no hub.</param>
/// <param name="Content">Bytes do documento. O provedor real baixa da origem; o stub sintetiza. O worker calcula o SHA-256 (integridade + dedupe) e persiste no <see cref="AegisScore.Application.Abstractions.IDocumentStorage"/>.</param>
public record DocumentDto(
    string Title,
    string SourceReference,
    string FileName,
    string ContentType,
    GovernanceDocumentType Type,
    byte[] Content);

/// <summary>
/// Padrão Strategy (Provider Pattern) para a ingestão AGNÓSTICA de documentos de governança do pilar
/// Govern. Cada fonte corporativa (Microsoft 365/SharePoint, Google Workspace, Confluence…) é UMA
/// implementação; o núcleo depende apenas desta porta e jamais se acopla a uma API de fornecedor — a
/// defesa contra vendor lock-in. Espelha o contrato irmão
/// <see cref="AegisScore.Application.Abstractions.IEvidenceConnector"/> (que coleta SINAIS de telemetria);
/// este coleta POLÍTICAS (documentos).
/// </summary>
public interface IDocumentIntegrationProvider
{
    /// <summary>A qual stack/fornecedor esta estratégia atende — a chave que a fábrica usa para resolvê-la.</summary>
    ConnectorProvider Provider { get; }

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-01] Esta estratégia SINTETIZA documentos em vez de ler a fonte real? Um provedor
    /// simulado produz conteúdo que atravessa o mesmo pipeline (extração, IA, teto documental) e acabaria
    /// apresentado ao cliente como política CORPORATIVA dele. Por isso a fábrica o esconde do caminho
    /// operacional a menos que o modo demonstrativo esteja explicitamente ligado — a honestidade é imposta
    /// no BACKEND, não por um aviso na tela. O padrão é <c>false</c>: um provedor real não declara nada.
    /// </summary>
    bool IsSimulated => false;

    /// <summary>
    /// Puxa da fonte externa os documentos de política/governança do tenant. Contrato: NÃO lança por
    /// "nada novo" (devolve vazio); só falha em erro real de transporte/credencial. Idempotência e
    /// deduplicação são responsabilidade do chamador (worker de ingestão), que descarta o que já ingeriu.
    /// </summary>
    Task<IEnumerable<DocumentDto>> FetchPoliciesAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Fábrica/roteador do Provider Pattern: dado o fornecedor configurado pelo tenant (o
/// <c>ConnectorProvider</c> do seu <c>ConnectorConfig</c> de capacidade <c>PolicyDocuments</c>), devolve a
/// estratégia de ingestão correspondente. "Instanciar" aqui é RESOLVER da DI — as estratégias são
/// serviços registrados — e não <c>new</c> manual: preserva a injeção de dependências de cada provedor
/// (HttpClient tipado, protetor de segredos…) e mantém a fábrica trivialmente testável. Mesmo idioma do
/// <see cref="AegisScore.Application.Abstractions.IConnectorRegistry"/>.
/// </summary>
public interface IDocumentIntegrationFactory
{
    /// <summary>
    /// Resolve a estratégia para o fornecedor informado, ou <c>null</c> quando nenhuma está registrada
    /// (ex.: o tenant configurou Google, mas o conector do Google ainda não foi implantado) — o chamador
    /// registra e ignora, sem quebrar os demais tenants.
    /// </summary>
    IDocumentIntegrationProvider? GetProvider(ConnectorProvider provider);

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-01] O que a ingestão documental REALMENTE consegue fazer neste ambiente — a
    /// resposta honesta para a interface, que até aqui anunciava "sincronize suas políticas corporativas"
    /// tendo por trás apenas um provedor simulado.
    /// </summary>
    DocumentIntegrationAvailability GetAvailability();
}

/// <summary>
/// [AEGIS-MVP-PRODUCT-01] Disponibilidade REAL da ingestão documental por integração corporativa.
/// </summary>
/// <param name="HasOperationalProvider">Existe ao menos uma fonte real capaz de puxar documentos.</param>
/// <param name="SimulatedModeEnabled">O modo demonstrativo está explicitamente ligado nesta instância.</param>
/// <param name="AvailableProviders">Fornecedores efetivamente resolvíveis agora (já respeitando o gate).</param>
public sealed record DocumentIntegrationAvailability(
    bool HasOperationalProvider,
    bool SimulatedModeEnabled,
    IReadOnlyList<string> AvailableProviders);

/// <summary>
/// [AEGIS-MVP-PRODUCT-01] Chave ÚNICA que libera provedores documentais SIMULADOS. Desligada por padrão:
/// numa instalação normal a sincronização corporativa simplesmente NÃO está disponível, e a interface diz
/// isso — em vez de entregar políticas fictícias como se fossem do cliente.
/// </summary>
public sealed class DocumentIntegrationOptions
{
    public const string SectionName = "DocumentIntegration";

    /// <summary>Ligar SOMENTE em demonstração explícita. Em produção permanece <c>false</c>.</summary>
    public bool AllowSimulatedProviders { get; set; }
}
