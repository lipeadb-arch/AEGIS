using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>
/// NATUREZA da prova que falta para um controle — o eixo que o <see cref="ControlStatus"/> não expressa.
/// "NonCompliant" não distingue o SOC que não emite log do processo que nunca foi escrito, e a ação
/// corretiva de cada caso é radicalmente diferente: um é trabalho de engenharia, o outro é de governança.
///
/// ⚠️ Eixo ORTOGONAL ao <see cref="VerdictSource"/>, com o qual é fácil confundir: <c>VerdictSource</c>
/// diz o que PRODUZIU o veredito vigente; este diz o que FALTA para prová-lo. Um controle avaliado por
/// telemetria (<c>VerdictSource.Telemetry</c>) pode perfeitamente estar carecendo de documentação.
/// </summary>
public enum ComplianceRequirementType
{
    /// <summary>Falta sinal técnico: a ferramenta não cobre o ativo, não emite o log ou não foi integrada.</summary>
    Telemetry = 0,

    /// <summary>Falta prova documental: política, procedimento ou registro formal inexistente/desatualizado.</summary>
    Documentation = 1,

    /// <summary>
    /// Uma ÚNICA lacuna que só fecha com as duas provas — a política escrita E a telemetria que demonstra
    /// a política em vigor. Não é "duas pendências": é uma pendência de dupla evidência, e marcá-la assim
    /// evita que fechar metade dela pareça progresso.
    /// </summary>
    Both = 2,
}

/// <summary>
/// Uma lacuna de evidência ESPECÍFICA por trás de uma não-conformidade — o "o que exatamente falta",
/// tipado, em vez de sepultado na prosa de <see cref="TenantControlState.AiEvidence"/>.
///
/// Persistido como item de uma lista <c>jsonb</c> no estado do controle (ver o mapeamento no
/// <c>AegisScoreDbContext</c>). O enum viaja como TEXTO no JSON, não como número: um ledger de
/// conformidade precisa ser auditável direto no SQL, e um <c>{"type": 1}</c> vira dado ilegível — pior,
/// reordenar o enum reinterpretaria silenciosamente o histórico.
/// </summary>
/// <param name="Type">Natureza da prova ausente — decide se a correção é de engenharia ou de governança.</param>
/// <param name="SourceIdentifier">
/// Identificador da FONTE que deveria ter suprido a prova, no nome que o operador reconhece:
/// o conector/ferramenta quando <see cref="ComplianceRequirementType.Telemetry"/> ("EntraID",
/// "SentinelOne"), ou a chave do documento quando <see cref="ComplianceRequirementType.Documentation"/>
/// ("Policy_Access_Control"). É o elo acionável: sem ele a lacuna não tem dono.
/// </param>
/// <param name="Description">O que falta, em português e em uma frase — texto de tela, não de log.</param>
public record MissingRequirement(
    ComplianceRequirementType Type,
    string SourceIdentifier,
    string Description);

/// <summary>
/// Estado de conformidade de UMA subcategoria NIST para UM tenant — o núcleo do Aegis Score.
///
/// Diferente de <see cref="SubcategoryEvaluation"/> (maturidade CMMI 1–5, avaliada por *scope*
/// Processo × BU dentro de uma campanha de assessment), este é um registro ÚNICO por
/// tenant × subcategoria: o "estado atual do controle", desacoplado de campanha.
///
/// O score do tenant é um Group By de soma sobre o catálogo global:
///   SUM(CurrentScore) / SUM(NistSubcategory.MaxScorePoints), agregável por Função/Categoria.
/// </summary>
public class TenantControlState : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    // ---- Elo com o catálogo global (imutável) ----
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }

    // ---- Estado do controle ----
    /// <summary>Default seguro: assume não-conforme até haver evidência (secure-by-design).</summary>
    public ControlStatus Status { get; set; } = ControlStatus.NonCompliant;

    /// <summary>Pontos obtidos (0..NistSubcategory.MaxScorePoints) — numerador do Aegis Score.</summary>
    public int CurrentScore { get; set; }

    /// <summary>
    /// Procedência do veredito que gravou o estado VIGENTE — define a precedência de escrita: um veredito
    /// <see cref="VerdictSource.Documentary"/> jamais sobrescreve um estado gravado por
    /// <see cref="VerdictSource.Telemetry"/>, nem para cima. A telemetria é a verdade absoluta sobre a
    /// implementação efetiva; um PDF de política não pode maquiar um controle comprovadamente falho.
    ///
    /// Default seguro <c>Documentary</c> (fonte fraca), espelhando o default <c>NonCompliant</c> de
    /// <see cref="Status"/>: estado de procedência desconhecida nunca bloqueia uma correção da telemetria.
    /// </summary>
    public VerdictSource LastVerdictSource { get; set; } = VerdictSource.Documentary;

    public DateTimeOffset LastEvaluatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Justificativa/evidência produzida pela IA (nulo até o motor avaliar).</summary>
    public string? AiEvidence { get; set; }

    /// <summary>
    /// Checklist técnico que justifica o veredito, serializado como JSON (lista de <c>ComplianceCheck</c>).
    /// String simples de propósito — o núcleo trata os checks como um blob explicável de leitura, sem
    /// modelá-los como entidades relacionais (não há consulta por check). Nulo até o motor decompor o veredito.
    /// </summary>
    public string? ChecksJson { get; set; }

    /// <summary>
    /// Contexto de inteligência do controle (<c>ControlIntelligence</c>) serializado como JSON: severidade,
    /// rastro cru da ferramenta, plano de ação, confiança da IA, ameaças abertas e MTTD/MTTR.
    ///
    /// Mesma decisão de <see cref="ChecksJson"/> — blob explicável de LEITURA, não modelo relacional: o
    /// enriquecimento é lido junto com a célula e nunca consultado por campo, então normalizá-lo custaria
    /// joins sem pagar nada. Nulo até o motor de IA emitir o bloco.
    /// </summary>
    public string? IntelligenceJson { get; set; }

    /// <summary>
    /// Lacunas de evidência que sustentam a não-conformidade, discriminadas por natureza
    /// (<see cref="ComplianceRequirementType"/>). Responde "por que este controle não pontua?" com
    /// estrutura em vez de prosa: telemetria ausente e documentação ausente exigem times, prazos e
    /// orçamentos diferentes, e agregá-las por <c>Type</c> é o que permite dizer ao board "78% das nossas
    /// lacunas são de processo, não de ferramenta".
    ///
    /// ⚠️ TIPADA, ao contrário de <see cref="ChecksJson"/>/<see cref="IntelligenceJson"/> (strings). Não é
    /// incoerência: aqueles são blobs de LEITURA que a UI repassa inteiros ao card, esta é dado que o
    /// domínio percorre, filtra e agrega — o mesmo idioma jsonb+ValueConverter das listas do catálogo NIST.
    ///
    /// Invariante mantida pelo <c>ControlStateWriter</c> (o escritor único do ledger): a lista é VAZIA
    /// quando o status é <see cref="ControlStatus.Compliant"/> — um controle conforme não tem pendência.
    /// <see cref="ControlStatus.MitigatedByThirdParty"/> PODE tê-las: o risco está coberto por terceiro,
    /// a lacuna própria continua aberta.
    /// </summary>
    public List<MissingRequirement> MissingRequirements { get; set; } = new();
}

/// <summary>
/// Foto agregada DIÁRIA da postura de segurança de UM tenant — a inteligência temporal do Aegis
/// Score, que alimenta o gráfico de tendência (modelo Microsoft Secure Score).
///
/// Estratégia de <b>Snapshot Agregado Diário</b>: uma única linha por tenant × dia, já com o
/// numerador (SUM <see cref="TenantControlState.CurrentScore"/>) e o denominador
/// (SUM <see cref="NistSubcategory.MaxScorePoints"/>) consolidados. Evita reexecutar o "Group By
/// de soma" histórico a cada leitura e mantém o overhead de armazenamento no PostgreSQL baixo.
///
/// O percentual exibido é DERIVADO na leitura (TotalAchievedScore / TotalMaxScore) e nunca
/// persistido. Herda de <see cref="Entity"/>, então <c>CreatedAt</c> registra o instante real da
/// geração (quando o worker rodou), complementando o dia lógico de <see cref="SnapshotDate"/>.
/// </summary>
public class TenantScoreSnapshot : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Dia lógico da foto (sem hora, sem fuso). Chave de idempotência junto com TenantId.</summary>
    public DateOnly SnapshotDate { get; set; }

    /// <summary>Numerador consolidado do dia: SUM(TenantControlState.CurrentScore).</summary>
    public int TotalAchievedScore { get; set; }

    /// <summary>Denominador consolidado do dia: SUM(NistSubcategory.MaxScorePoints) do catálogo vigente.</summary>
    public int TotalMaxScore { get; set; }
}
