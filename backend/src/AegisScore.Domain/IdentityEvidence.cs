using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-EVIDENCE-FABRIC-01] Evidência de identidade normalizada e compartilhada
// ============================================================================
// Fotografia NORMALIZADA e PROVIDER-NEUTRAL da evidência de identidade colhida do
// Microsoft Entra ID por UMA aquisição real (o coletor do AEGIS KNIGHT). É a fonte
// ÚNICA que alimenta o AEGIS KNIGHT, as projeções NIST determinísticas autorizadas,
// a postura/dashboard e os relatórios — sem uma segunda integração, um segundo
// cliente Graph ou uma segunda credencial.
//
// PROVENIÊNCIA e COMPLETUDE são cidadãos de primeira classe: o snapshot distingue o
// estado dos DADOS ARMAZENADOS (que sobrevivem a uma coleta posterior que falhe) do
// desfecho da ÚLTIMA TENTATIVA (que pode ser falha/degradação). Uma coleta nova que
// falhe NÃO apaga nem falsifica o último snapshot válido — só marca a degradação.
//
// PRIVACIDADE: NUNCA persiste nome, e-mail, ID de usuário, nome de aplicação, token,
// segredo ou payload de identidade. Só AGREGADOS tipados (contagens/razões/flags) e
// estados por capacidade sanitizados. Autoridade determinística e IA permanecem
// INTOCADAS: este fato NÃO é EvidenceSignal, NÃO altera TenantControlState/ledger,
// NÃO concede pontos ao AEGIS Score e NÃO decide conformidade NIST por si só.

/// <summary>
/// Snapshot ATUAL da evidência de identidade de UM conector (Microsoft Entra ID · AEGIS KNIGHT), isolado por
/// tenant. Chave natural (TenantId, ConnectorConfigId) — índice único que torna o upsert idempotente uma
/// invariante de banco. Guarda os fatos AGREGADOS normalizados (JSON tipado, sem PII) e os estados por
/// capacidade da última coleta que PRODUZIU dados, mais o desfecho da última TENTATIVA, para sobreviver a
/// reload/restart e preservar honestamente a última evidência completa mesmo quando uma coleta posterior falha.
/// </summary>
public class IdentityEvidenceSnapshot : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Conector (Microsoft/IdentityPosture) que produziu a evidência — a fonte concreta desta postura.</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>Rótulo estável e legível da fonte (ex.: "Microsoft Entra ID"). Nunca endpoint/credencial/segredo.</summary>
    public string Source { get; set; } = "";

    /// <summary>Fonte CONCRETA da coleta — o eixo do multicoletor (MicrosoftEntraId nesta entrega).</summary>
    public KnightSourceType SourceType { get; set; } = KnightSourceType.MicrosoftEntraId;

    /// <summary>Versão do schema do snapshot (ex.: "aegis-identity-evidence-v1") — rastreabilidade do contrato.</summary>
    public string SchemaVersion { get; set; } = "";

    /// <summary>
    /// Estado da coleta que PRODUZIU os dados armazenados (Completed ou PartialCollection). Preservado mesmo
    /// quando a última tentativa falha — os totais só podem ser lidos como verdade completa quando Completed.
    /// </summary>
    public KnightSourceState DataState { get; set; } = KnightSourceState.NotConfigured;

    /// <summary>
    /// Desfecho da tentativa MAIS RECENTE (pode ser AuthenticationFailure/Throttled/InsufficientPermission/
    /// Unavailable mesmo com dados completos preservados). É onde a degradação/falha aparece, sem destruir a
    /// última evidência válida.
    /// </summary>
    public KnightSourceState LastAttemptState { get; set; } = KnightSourceState.NotConfigured;

    /// <summary>Instante da última TENTATIVA de coleta (sucesso, parcial ou falha).</summary>
    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>Instante da última coleta que PRODUZIU os dados armazenados. Null enquanto nunca houve dados.</summary>
    public DateTimeOffset? LastCollectionAt { get; set; }

    /// <summary>Detalhe sanitizado do último desfecho (sem token/segredo/PII). Ajuda a UI a explicar a degradação.</summary>
    public string? LastAttemptDetail { get; set; }

    /// <summary>
    /// Fatos AGREGADOS normalizados da coleta (JSON tipado): a lista de observações por chave de sinal, com
    /// contagem/razão/flag e — quando ausente — o motivo sanitizado. NUNCA nome/e-mail/ID/segredo.
    /// </summary>
    public string FactsJson { get; set; } = "[]";

    /// <summary>Estado por CAPACIDADE (JSON): o que foi coletado e o que faltou (permissão/indisponibilidade), sanitizado.</summary>
    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>
    /// Fingerprint determinístico (SHA-256 hex) dos DADOS armazenados (fatos + capacidades + estado). Impede
    /// reescritas desnecessárias: uma coleta idêntica não reescreve o corpo do snapshot.
    /// </summary>
    public string Fingerprint { get; set; } = "";
}
