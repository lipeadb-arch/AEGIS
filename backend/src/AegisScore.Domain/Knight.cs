using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  AEGIS KNIGHT — assessment de postura de identidade e exposição
// ============================================================================
// Estrutura DEDICADA, deliberadamente desacoplada do Assessment de processos
// organizacionais (campanha Processo × Área) e do TenantControlState (ledger do
// AEGIS Score geral). O KNIGHT tem execução própria, catálogo próprio de
// indicadores, vereditos próprios e fórmula própria — o score KNIGHT NÃO é o
// AEGIS Score. As duas coisas não devem ser confundidas nem no domínio nem na UI.

/// <summary>Modo de execução do assessment KNIGHT: demonstração sintética ou coleta real (futura).</summary>
public enum KnightAssessmentMode
{
    /// <summary>Dados 100% sintéticos (domínios example.com). NUNCA consultou fonte real (Graph/AD/Okta).</summary>
    Demo = 0,

    /// <summary>Coleta de uma fonte real conectada. Reservado para as próximas entregas — não usado nesta.</summary>
    Live = 1,
}

/// <summary>Estágio de uma execução do assessment KNIGHT.</summary>
public enum KnightRunStatus
{
    /// <summary>Criada, ainda não avaliada.</summary>
    Pending = 0,

    /// <summary>Coleta/avaliação em andamento.</summary>
    Running = 1,

    /// <summary>Concluída — vereditos determinísticos persistidos (a IA pode ter falhado; isso NÃO reprova a execução).</summary>
    Completed = 2,

    /// <summary>Falha operacional na coleta/avaliação determinística (não confundir com IA indisponível).</summary>
    Failed = 3,
}

/// <summary>
/// Veredito determinístico de UM indicador KNIGHT. A ausência de dados NUNCA equivale a aprovação:
/// falta de sinal é <see cref="NotEvaluated"/> (reduz cobertura), não <see cref="Passed"/>.
/// </summary>
public enum KnightIndicatorStatus
{
    /// <summary>Sem exposição detectada para a regra do indicador.</summary>
    Passed = 0,

    /// <summary>Exposição confirmada pela regra determinística.</summary>
    Exposed = 1,

    /// <summary>Exposição coberta por controle compensatório COMPROVADO no snapshot (não um toggle visual).</summary>
    Mitigated = 2,

    /// <summary>Faltou o sinal necessário para avaliar — fora do score, reduz a cobertura. NUNCA é aprovação.</summary>
    NotEvaluated = 3,

    /// <summary>Erro ao avaliar o indicador — fora do score, reduz a cobertura.</summary>
    Error = 4,

    /// <summary>O indicador não se aplica a este ambiente — fora do score E da cobertura aplicável.</summary>
    NotApplicable = 5,
}

/// <summary>Agrupamento temático de um indicador KNIGHT (dimensão de identidade avaliada).</summary>
public enum KnightIndicatorCategory
{
    /// <summary>Acesso privilegiado (contas administrativas e sua proteção).</summary>
    PrivilegedAccess = 0,

    /// <summary>Governança de identidade (menor privilégio, dimensionamento do acesso administrativo).</summary>
    IdentityGovernance = 1,

    /// <summary>Higiene de contas (superfície residual, contas esquecidas).</summary>
    AccountHygiene = 2,

    /// <summary>Acesso de convidados/terceiros.</summary>
    GuestAccess = 3,

    /// <summary>Contas técnicas/de serviço e suas isenções.</summary>
    ServiceAccounts = 4,
}

/// <summary>
/// Execução persistida de um assessment do AEGIS KNIGHT — tenant-owned. Guarda o modo, a fonte, o estado,
/// a versão do catálogo, os instantes, o score anulável e a cobertura, os totais por veredito e o resumo
/// consultivo da IA (com indicação explícita se veio de IA ou de fallback determinístico).
/// </summary>
public class KnightAssessmentRun : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Demo (sintético) ou Live (real). Nesta entrega, sempre Demo.</summary>
    public KnightAssessmentMode Mode { get; set; } = KnightAssessmentMode.Demo;

    /// <summary>Rótulo legível da fonte da coleta (ex.: "Provedor de Demonstração KNIGHT"). Nunca uma marca de terceiro.</summary>
    public string Source { get; set; } = "";

    public KnightRunStatus Status { get; set; } = KnightRunStatus.Pending;

    /// <summary>Versão do catálogo de indicadores usado (ex.: "ak-knight-v1") — rastreabilidade do veredito.</summary>
    public string CatalogVersion { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Score KNIGHT (0..100) ANULÁVEL: <c>null</c> quando nenhum indicador foi efetivamente avaliado — nunca 0.
    /// Calculado pela fórmula própria do KNIGHT (não a fórmula global aegis-score-v1).
    /// </summary>
    public double? Score { get; set; }

    /// <summary>Cobertura (0..100): indicadores avaliados / aplicáveis. NotEvaluated/Error reduzem; NotApplicable sai do denominador.</summary>
    public double Coverage { get; set; }

    /// <summary>Versão da fórmula de score/cobertura aplicada (ex.: "knight-score-v1") — transparência do cálculo.</summary>
    public string ScoreFormulaVersion { get; set; } = "";

    // ---- Totais por veredito (denormalizados para leitura barata) ----
    public int PassedCount { get; set; }
    public int ExposedCount { get; set; }
    public int MitigatedCount { get; set; }
    public int NotEvaluatedCount { get; set; }
    public int ErrorCount { get; set; }
    public int NotApplicableCount { get; set; }

    /// <summary>
    /// Resumo consultivo estruturado (JSON) produzido pela camada de IA — ou o fallback determinístico.
    /// Nulo apenas se a execução não chegou a gerar narrativa. A IA jamais decide status/severidade/score.
    /// </summary>
    public string? AdvisoryJson { get; set; }

    /// <summary>
    /// TRUE se o <see cref="AdvisoryJson"/> veio do motor de IA; FALSE se é o fallback determinístico
    /// (IA indisponível/inválida). A falha da IA NUNCA invalida nem reprova o assessment.
    /// </summary>
    public bool AdvisoryFromAi { get; set; }

    public ICollection<KnightIndicatorResult> Indicators { get; set; } = new List<KnightIndicatorResult>();
}

/// <summary>
/// Resultado de UM indicador dentro de uma execução KNIGHT — tenant-owned. Registra o veredito
/// determinístico e a evidência factual, sem persistir segredo, token ou payload bruto sensível.
/// </summary>
public class KnightIndicatorResult : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }
    public KnightAssessmentRun? Run { get; set; }

    /// <summary>ID estável do indicador no catálogo (ex.: "AK-ENTRA-001").</summary>
    public string IndicatorId { get; set; } = "";

    /// <summary>Título original AEGIS do indicador (texto próprio, não copiado de terceiros).</summary>
    public string Title { get; set; } = "";

    public KnightIndicatorCategory Category { get; set; }

    /// <summary>Severidade do indicador (régua ÚNICA do produto — <see cref="SeverityLevel"/>).</summary>
    public SeverityLevel Severity { get; set; }

    public KnightIndicatorStatus Status { get; set; }

    /// <summary>Evidência factual e legível do veredito (números do snapshot), sem PII nem segredo.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>Quantidade de objetos afetados pela exposição (ex.: nº de contas). 0 quando não se aplica.</summary>
    public int AffectedObjectCount { get; set; }

    /// <summary>Códigos NIST CSF 2.0 endereçados pelo indicador (jsonb). Projeção informativa — não altera o AEGIS Score.</summary>
    public List<string> NistCodes { get; set; } = new();

    /// <summary>Técnicas MITRE ATT&amp;CK, SOMENTE quando fundamentadas (jsonb). Vazio quando não defensável.</summary>
    public List<string> MitreTechniques { get; set; } = new();

    /// <summary>Recomendação curta e determinística (texto do catálogo). A IA não a substitui.</summary>
    public string Recommendation { get; set; } = "";

    /// <summary>Instante da coleta do snapshot que originou este resultado.</summary>
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
}
