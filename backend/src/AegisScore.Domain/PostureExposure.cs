using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-POSTURE-02] Exposição de CONFIGURAÇÃO (postura) — provider-neutral
// ============================================================================
// Uma PostureExposureFinding é uma LACUNA DE CONFIGURAÇÃO detectada por um conector
// de postura (o primeiro é o Microsoft Secure Score, via secureScoreControlProfiles
// cruzados com o secureScore mais recente). É deliberadamente PROVIDER-NEUTRAL para
// receber, no futuro, coletores de Defender Exposure, AWS, Google Cloud e scanners —
// sem forçar o conceito no AssetThreatExposure (que exige um vínculo ATIVO↔AMEAÇA e
// não serve para um achado tenant-wide de postura, que não tem ativo específico).
//
// NÃO é uma vulnerabilidade/CVE de ativo: é uma "recomendação de postura" / "exposição
// de configuração". O AEGIS Score permanece determinístico (SignalMapping.ScoringHint +
// autoridades existentes); o estado informado pela fonte (ignored/thirdParty/reviewed) é
// METADADO — nunca prova independente e nunca altera o score.

/// <summary>
/// Estado do CICLO DE VIDA AEGIS de uma exposição de postura. Distinto do "estado informado pela fonte"
/// (<see cref="PostureExposureFinding.SourceState"/>, mero metadado): este é a autoridade do AEGIS sobre
/// se a lacuna está aberta ou já foi resolvida numa coleta completa. Nunca há exclusão silenciosa — uma
/// exposição que deixa de ter gap numa coleta COMPLETA vira <see cref="Resolved"/>, preservando o histórico.
/// </summary>
public enum PostureExposureState
{
    Open = 0,
    Resolved = 1,
}

/// <summary>
/// Uma lacuna de configuração (exposição de postura) detectada por um conector, isolada por tenant. A chave
/// natural é <c>(TenantId, ConnectorConfigId, ExternalId)</c> — índice único que torna a reconciliação
/// (upsert + resolução) uma invariante de banco, não uma promessa do read-then-write. Campos sensíveis da
/// fonte (resposta bruta, bearer, updatedBy, comentários, e-mails, actionUrl) NUNCA são persistidos aqui.
/// </summary>
public class PostureExposureFinding : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Conector que produziu a exposição (a fonte concreta desta lacuna).</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>
    /// Identificador ÚNICO do controle no fornecedor (ex.: o <c>id</c>/controlName do secureScoreControlProfile).
    /// Compõe a chave natural com (TenantId, ConnectorConfigId) — é o que liga coletas sucessivas ao MESMO achado.
    /// </summary>
    public string ExternalId { get; set; } = "";

    /// <summary>Título legível da recomendação (ex.: "Ensure multifactor authentication is enabled...").</summary>
    public string Title { get; set; } = "";

    /// <summary>Categoria de postura (ex.: Identity, Data, Device, Apps) — normalizada pelo coletor.</summary>
    public string? Category { get; set; }

    /// <summary>Serviço/produto ao qual a recomendação se aplica (ex.: "Microsoft 365", "Exchange Online").</summary>
    public string? Service { get; set; }

    /// <summary>Tipo de ação da recomendação no fornecedor (ex.: "Config", "Review", "Behavior").</summary>
    public string? ActionType { get; set; }

    /// <summary>Pontuação ATUAL do controle na fotografia mais recente (do controlScore correspondente).</summary>
    public double CurrentScore { get; set; }

    /// <summary>Pontuação MÁXIMA do controle (do perfil). Base do gap; a exposição só existe com <c>MaxScore &gt; 0</c>.</summary>
    public double MaxScore { get; set; }

    /// <summary>Gap CALCULADO na coleta (<c>MaxScore - CurrentScore</c>). Persistido para ordenação/consulta direta.</summary>
    public double Gap { get; set; }

    /// <summary>Rank de prioridade do fornecedor (menor = mais prioritário) — a ordenação padrão da tela.</summary>
    public int? SourceRank { get; set; }

    /// <summary>Camada do controle informada pelo fornecedor (ex.: "Core", "Defense in Depth", "Advanced").</summary>
    public string? Tier { get; set; }

    /// <summary>Custo de implementação estimado pelo fornecedor (ex.: "Low", "Moderate", "High").</summary>
    public string? ImplementationCost { get; set; }

    /// <summary>Impacto ao usuário estimado pelo fornecedor (ex.: "Low", "Moderate", "High").</summary>
    public string? UserImpact { get; set; }

    /// <summary>Passo a passo de remediação informado pelo fornecedor (texto). Nunca inclui actionUrl/PII.</summary>
    public string? Remediation { get; set; }

    /// <summary>Impacto esperado da remediação, informado pelo fornecedor (texto).</summary>
    public string? RemediationImpact { get; set; }

    /// <summary>Ameaças normalizadas associadas ao controle pelo fornecedor (ex.: "Account Breach"). jsonb.</summary>
    public List<string> Threats { get; set; } = new();

    /// <summary>
    /// Estado informado pela FONTE quando houver (ex.: "Default", "Ignored", "ThirdParty", "Reviewed") — apenas
    /// o rótulo do estado. É METADADO: não é prova independente e NÃO altera o AEGIS Score. Sem updatedBy/comentário.
    /// </summary>
    public string? SourceState { get; set; }

    /// <summary>Estado do ciclo de vida AEGIS (Open/Resolved) — a autoridade do AEGIS, distinta do SourceState.</summary>
    public PostureExposureState LifecycleState { get; set; } = PostureExposureState.Open;

    /// <summary>Instante da PRIMEIRA detecção — preservado por todas as coletas seguintes.</summary>
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Instante da coleta mais recente que observou este controle.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Instante em que a exposição foi RESOLVIDA (gap sumiu numa coleta completa); null enquanto Open.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
