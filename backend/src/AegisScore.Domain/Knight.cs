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

/// <summary>
/// Fonte de coleta do AEGIS KNIGHT. O núcleo é MULTICOLETOR: não presume Microsoft Graph, tenant do Entra,
/// Conditional Access nem formato de papéis/grupos do Entra. Cada valor mapeia um coletor que normaliza os
/// dados em fatos/observações tipados; a arquitetura acomoda novas fontes (Google Workspace, AD, Okta…) sem
/// refatorar o núcleo. <see cref="GoogleWorkspace"/> existe como capacidade arquitetural — o coletor real
/// somente-leitura é a próxima entrega técnica.
/// </summary>
public enum KnightSourceType
{
    /// <summary>Dados 100% sintéticos (example.com), sem rede. Nunca consultou fonte real.</summary>
    Demo = 0,

    /// <summary>Microsoft Entra ID via Microsoft Graph (somente leitura).</summary>
    MicrosoftEntraId = 1,

    /// <summary>Google Workspace (somente leitura) — capacidade arquitetural; coletor real na próxima entrega.</summary>
    GoogleWorkspace = 2,

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams (somente leitura), pelo módulo oficial Teams PowerShell com
    /// autenticação de APLICATIVO. Fonte SEPARADA do Entra ID de propósito: a credencial é a mesma aplicação
    /// registrada, mas o acesso exige um papel de diretório próprio (Leitor do Teams) e o transporte é outro —
    /// uma falha aqui não pode apagar, nem substituir, a avaliação do Entra ID.
    /// </summary>
    MicrosoftTeams = 3,
}

/// <summary>
/// Estado da fonte/coleta de um assessment KNIGHT — distingue "não configurado" de "sem permissão", "parcial",
/// "throttling" etc. Guia a UI e reflete o desfecho da coleta. Dados NÃO obtidos viram NotEvaluated/Error e
/// reduzem a cobertura — NUNCA Passed.
/// </summary>
public enum KnightSourceState
{
    /// <summary>Não há configuração aplicável para a fonte neste tenant.</summary>
    NotConfigured = 0,

    /// <summary>Há configuração, ainda não coletada.</summary>
    Configured = 1,

    /// <summary>Coleta em andamento.</summary>
    Collecting = 2,

    /// <summary>Coleta concluída integralmente.</summary>
    Completed = 3,

    /// <summary>Coleta parcial: parte das capacidades falhou/faltou (permissão/indisponibilidade), reduzindo a cobertura.</summary>
    PartialCollection = 4,

    /// <summary>Permissões insuficientes para a coleta pretendida.</summary>
    InsufficientPermission = 5,

    /// <summary>Falha de autenticação da aplicação junto à fonte.</summary>
    AuthenticationFailure = 6,

    /// <summary>Throttling/limite de taxa da fonte.</summary>
    Throttled = 7,

    /// <summary>Fonte indisponível (rede/servidor).</summary>
    Unavailable = 8,

    /// <summary>Erro inesperado durante a coleta.</summary>
    Error = 9,
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

    /// <summary>[AEGIS-KNIGHT-COVERAGE-01] Configurações gerais do locatário (o que usuários podem criar e usar).</summary>
    TenantConfiguration = 5,

    /// <summary>[AEGIS-KNIGHT-COVERAGE-01] Políticas de autenticação (métodos, senhas, acesso condicional).</summary>
    AuthenticationPolicy = 6,

    /// <summary>[AEGIS-KNIGHT-COVERAGE-01] Governança de aplicações (consentimento, credenciais).</summary>
    ApplicationGovernance = 7,

    /// <summary>[AEGIS-KNIGHT-COVERAGE-01] Registro e gerenciamento de dispositivos.</summary>
    DeviceGovernance = 8,

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] Colaboração e comunicação: com quem se fala, quem entra numa reunião, o que sai
    /// da organização por um canal de conversa. Distinta de <see cref="GuestAccess"/>, que trata do acesso de
    /// convidados ao DIRETÓRIO.
    /// </summary>
    CollaborationSecurity = 9,
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

    /// <summary>Demo (sintético) ou Live (real). Derivado da fonte: Demo → Demo; fontes reais → Live.</summary>
    public KnightAssessmentMode Mode { get; set; } = KnightAssessmentMode.Demo;

    /// <summary>Fonte CONCRETA da coleta (Demo/MicrosoftEntraId/GoogleWorkspace) — o eixo do multicoletor.</summary>
    public KnightSourceType SourceType { get; set; } = KnightSourceType.Demo;

    /// <summary>Estado da coleta desta execução (Completed/PartialCollection/InsufficientPermission…).</summary>
    public KnightSourceState SourceState { get; set; } = KnightSourceState.Completed;

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
    /// Estado por CAPACIDADE da fonte (JSON): o que foi coletado e o que faltou (permissão/indisponibilidade),
    /// alimentando a cobertura e as "limitações de coleta" da UI e da IA. Nulo nas execuções Demo antigas.
    /// </summary>
    public string? CapabilitiesJson { get; set; }

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

    /// <summary>
    /// [AEGIS-ADM-01] A AQUISIÇÃO do ADM que efetivamente sustentou esta avaliação — a resposta para "qual
    /// coleta produziu este veredito?".
    ///
    /// ANULÁVEL por duas razões distintas, e a leitura precisa saber diferenciá-las: avaliações ANTERIORES a
    /// este pacote nunca tiveram aquisição registrada (e não são retropreenchidas com a coleta de hoje, o que
    /// seria apresentar o presente como prova do passado); e fontes que não passam pelo ADM nesta entrega
    /// (Demo, Google Workspace) seguem sem aquisição, por compatibilidade explícita.
    ///
    /// Deliberadamente SEM chave estrangeira: a evidência de uma origem desaparece quando o conector é
    /// removido (cascata), e uma FK obrigaria a escolher entre travar a remoção do conector ou destruir o
    /// histórico de avaliações. Nenhuma das duas é aceitável — a avaliação é congelada e sobrevive à origem.
    /// </summary>
    public Guid? IdentityAcquisitionId { get; set; }

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

    /// <summary>Fonte que produziu este resultado (Demo/MicrosoftEntraId/GoogleWorkspace).</summary>
    public KnightSourceType SourceType { get; set; } = KnightSourceType.Demo;

    /// <summary>Motivo do <c>NotEvaluated</c> (dado/permissão ausente), quando aplicável — nunca vira aprovação.</summary>
    public string? NotEvaluatedReason { get; set; }

    /// <summary>Instante da coleta do snapshot que originou este resultado.</summary>
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- [AEGIS-MVP-PRODUCT-02] Detalhe dos objetos afetados -------------------------------------
    // Três colunas ADITIVAS com default seguro (false/null): execuções anteriores permanecem exatamente como
    // foram gravadas e passam a se declarar "sem detalhe preservado" — nunca retropreenchidas com o presente.

    /// <summary>
    /// TRUE quando esta execução preservou os objetos que sustentam o veredito. FALSE distingue duas coisas
    /// que a UI não pode confundir: o indicador está fora do escopo de detalhe, ou a execução é anterior à
    /// preservação. Em ambos os casos a tela declara a ausência em vez de mostrar uma lista de outra coleta.
    /// </summary>
    public bool HasAffectedDetail { get; set; }

    /// <summary>
    /// TRUE quando a lista preservada cobre TODO o conjunto que produziu <see cref="AffectedObjectCount"/>.
    /// FALSE quando a coleta não conseguiu enumerar tudo — lista parcial declarada, nunca lista truncada
    /// apresentada como completa.
    /// </summary>
    public bool AffectedDetailComplete { get; set; }

    /// <summary>O que a coleta não conseguiu enumerar/ler no detalhe (sanitizado, sem segredo).</summary>
    public string? AffectedDetailLimitation { get; set; }

    /// <summary>Objetos que sustentam este veredito. Vazio quando não há detalhe preservado.</summary>
    public ICollection<KnightAffectedObject> AffectedObjects { get; set; } = new List<KnightAffectedObject>();
}

/// <summary>
/// [AEGIS-MVP-PRODUCT-02] UM objeto que sustenta o veredito de um indicador KNIGHT — tenant-owned e SEMPRE
/// vinculado à execução que o observou. Superfície DELIBERADAMENTE separada dos contratos agregados: o
/// resultado do indicador continua sem PII, e o detalhe nominal vive aqui, com leitura própria.
///
/// Invariantes que esta entidade existe para sustentar:
///   • a lista pertence a UMA execução — nunca se mostra a lista de hoje como prova de um veredito de ontem;
///   • execuções anteriores à preservação NÃO são retropreenchidas (ficam sem filhos, e a UI declara isso);
///   • o TIPO do objeto é explícito — membro de papel privilegiado pode ser aplicação ou grupo, não pessoa;
///   • nome/UPN são ANULÁVEIS: ausentes na fonte, permanecem ausentes aqui.
/// </summary>
public class KnightAffectedObject : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Execução que observou o objeto — o vínculo que impede apresentar o presente como passado.</summary>
    public Guid RunId { get; set; }

    /// <summary>Resultado do indicador ao qual o objeto pertence (o pai relacional).</summary>
    public Guid IndicatorResultId { get; set; }
    public KnightIndicatorResult? IndicatorResult { get; set; }

    /// <summary>ID do indicador denormalizado (ex.: "AK-ENTRA-002") — leitura por achado sem join extra.</summary>
    public string IndicatorId { get; set; } = "";

    /// <summary>Identificador do objeto NA FONTE (object id do diretório). Chave de deduplicação.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>Natureza do objeto — jamais presumida como pessoa.</summary>
    public KnightAffectedObjectKind Kind { get; set; } = KnightAffectedObjectKind.Unknown;

    /// <summary>Nome de exibição, SOMENTE quando a fonte o devolveu. <c>null</c> nunca vira texto inventado.</summary>
    public string? DisplayName { get; set; }

    /// <summary>UPN/e-mail principal, SOMENTE quando aplicável ao tipo e devolvido pela fonte.</summary>
    public string? UserPrincipalName { get; set; }

    /// <summary>Papéis associados ao objeto quando a coleta os conhece (jsonb).</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>O fato que sustenta a inclusão — constatação da coleta, nunca conclusão sobre a pessoa.</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] Afetado (entra na contagem) ou evidência de configuração (sustenta o
    /// veredito, não é contado). Default <see cref="KnightObjectRelation.Affected"/>: as linhas anteriores a este
    /// pacote eram todas afetadas.
    /// </summary>
    public KnightObjectRelation Relation { get; set; } = KnightObjectRelation.Affected;

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] Configuração OBSERVADA do objeto, resumida em texto (estado, alvos,
    /// aplicações, condições, controles). Nula para identidades e para as linhas anteriores.
    /// </summary>
    public string? ObservedConfiguration { get; set; }
}

/// <summary>
/// [AEGIS-MVP-PRODUCT-02] Natureza do objeto afetado. Deliberadamente NÃO colapsa tudo em "usuário": um
/// membro de papel privilegiado pode ser uma aplicação (service principal) ou um grupo, e tratar isso como
/// pessoa levaria a UI a sugerir ações impossíveis (ex.: "exigir MFA") sobre um objeto que não autentica com
/// MFA. Autoridade ÚNICA do tipo — a Application reusa este enum em vez de manter um espelho que sairia do ar.
/// </summary>
public enum KnightAffectedObjectKind
{
    /// <summary>Conta de usuário interna do diretório.</summary>
    User = 0,

    /// <summary>Conta de convidado/externa (userType = Guest ou equivalente na fonte).</summary>
    Guest = 1,

    /// <summary>Identidade de aplicação/serviço (service principal, managed identity…). Não é uma pessoa.</summary>
    ServicePrincipal = 2,

    /// <summary>Grupo cujo pertencimento concede o acesso (o acesso é herdado pelos membros).</summary>
    Group = 3,

    /// <summary>Dispositivo do diretório.</summary>
    Device = 4,

    /// <summary>A fonte devolveu o objeto sem tipo reconhecível — declarado desconhecido, nunca "usuário".</summary>
    Unknown = 5,

    /// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Política de configuração (ex.: acesso condicional). Não é identidade.</summary>
    Policy = 6,

    /// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Papel de diretório privilegiado — o objeto afetado quando nenhuma política o cobre.</summary>
    DirectoryRole = 7,

    /// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Configuração do tenant (ex.: security defaults).</summary>
    TenantSetting = 8,

    /// <summary>[AEGIS-KNIGHT-COVERAGE-01] Domínio do diretório (ex.: política de expiração de senha por domínio).</summary>
    Domain = 9,
}

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] O que um objeto preservado É para o veredito do indicador. Separa duas coisas
/// que a tela e o relatório não podem confundir:
///   • <see cref="Affected"/> — o objeto SUSTENTA a exposição e entra na contagem de afetados;
///   • <see cref="Evidence"/> — a configuração que SUSTENTOU o veredito ("onde foi encontrado"), inclusive
///     quando ele é aprovado. Não é contado como afetado.
/// </summary>
public enum KnightObjectRelation
{
    Affected = 0,
    Evidence = 1,
}

/// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Estágio de uma sincronização solicitada em Integrações.</summary>
public enum KnightSyncStatus
{
    /// <summary>Registrada, aguardando o processamento.</summary>
    Pending = 0,

    /// <summary>Coleta → ADM → avaliação em andamento (sob lease).</summary>
    Running = 1,

    /// <summary>Terminou e produziu uma avaliação concluída (ver <see cref="KnightSyncRequest.RunId"/>).</summary>
    Completed = 2,

    /// <summary>Não produziu avaliação: conector indisponível, falha da coleta ou interrupção. Dados anteriores intactos.</summary>
    Failed = 3,
}

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Sincronização SOLICITADA a partir de Configurações → Integrações — tenant-owned
/// e DURÁVEL. Segue o padrão da fila de políticas (<see cref="PolicySyncRequest"/>): o pedido é persistido antes
/// do 202, adquirido por lease atômico e processado num escopo próprio, que é dono do seu DbContext. É o que
/// dá à tela um IDENTIFICADOR desde o primeiro instante — a consulta posterior nunca precisa adivinhar qual
/// execução é a dela — e o que impede disparos duplicados (um pedido ATIVO por conector, invariante de banco).
///
/// O processamento reusa a autoridade ÚNICA de coleta → ADM → avaliação (<c>IAegisKnightAssessmentService</c>).
/// Uma falha não apaga nada: a última avaliação concluída e o último snapshot válido permanecem como estavam.
/// </summary>
public class KnightSyncRequest : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Conector de Integrações que originou o pedido.</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>Fonte KNIGHT correspondente ao conector (Entra, Google Workspace).</summary>
    public KnightSourceType SourceType { get; set; }

    public KnightSyncStatus Status { get; set; } = KnightSyncStatus.Pending;

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Membership (User.Id) de quem pediu — trilha, nunca autorização.</summary>
    public Guid? RequestedBy { get; set; }

    public DateTimeOffset AvailableAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Avaliação produzida — só quando <see cref="Status"/> é Completed.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Estado da coleta que a avaliação registrou (Completed, PartialCollection…).</summary>
    public KnightSourceState? ResultSourceState { get; set; }

    /// <summary>Categoria SANITIZADA da falha (ex.: "ConnectorNotConfigured", "Interrupted") — nunca a exceção crua.</summary>
    public string? FailureCategory { get; set; }

    /// <summary>Mensagem legível e sanitizada do desfecho.</summary>
    public string? Message { get; set; }
}
