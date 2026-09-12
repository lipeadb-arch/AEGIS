using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Queries;

// ============================================================================
//  [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes (Defender × Intune) no MESMO dispositivo
// ============================================================================
// Primeira correlação funcional entre fontes, sobre os dispositivos já vinculados por chave forte
// (AEGIS-ENTITY-RESOLUTION-01). Três camadas separadas:
//   • obtenção dos fatos e da proveniência — CrossSourceCorrelationQuery (infraestrutura), numa leitura coerente;
//   • avaliação DETERMINÍSTICA das regras — CrossSourceCorrelationEvaluator (aqui, puro, sem banco nem relógio próprio);
//   • linguagem e contratos de leitura — CrossSourceNarrative e os DTOs abaixo.
//
// O que uma situação AFIRMA: que duas condições, cada uma informada por uma fonte, coexistem no mesmo dispositivo, nas
// datas de cada evidência. O que ela NÃO afirma: que uma condição permita explorar a outra, comprometimento, incidente,
// risco calculado, prioridade ou exposição à internet. Não há score, peso, probabilidade nem severidade composta — e
// os scores e filas existentes não são tocados. A IA não decide a existência de nenhuma situação.

/// <summary>
/// Política OPERACIONAL de elegibilidade temporal (configurável em <c>CrossSourceCorrelation</c>). Não é exigência
/// normativa nem garantia de segurança: define apenas a partir de quando uma evidência deixa de sustentar uma
/// situação combinada. Os valores são validados na subida da aplicação.
/// </summary>
public sealed class CrossSourceCorrelationOptions
{
    public const string SectionName = "CrossSourceCorrelation";

    /// <summary>Idade máxima da AQUISIÇÃO que observou o fato (instante da coleta do AEGIS), em dias.</summary>
    public int MaxEvidenceAgeDays { get; set; } = 7;

    /// <summary>
    /// Idade máxima da última ATIVIDADE informada pela fonte (lastSeen/lastSyncDateTime), em dias. Acima dela o
    /// registro não é tratado como atualmente observado.
    /// </summary>
    public int MaxSourceActivityAgeDays { get; set; } = 30;

    /// <summary>Defasagem entre as aquisições das duas fontes acima da qual a situação recebe ressalva, em horas.</summary>
    public int AcquisitionGapCaveatHours { get; set; } = 24;

    public bool IsValid =>
        MaxEvidenceAgeDays is >= 1 and <= 365
        && MaxSourceActivityAgeDays is >= 1 and <= 365
        && AcquisitionGapCaveatHours is >= 1 and <= 24 * 365;

    public CrossSourcePolicy ToPolicy() => IsValid
        ? new CrossSourcePolicy(MaxEvidenceAgeDays, MaxSourceActivityAgeDays, AcquisitionGapCaveatHours)
        : throw new InvalidOperationException(
            "CrossSourceCorrelation: MaxEvidenceAgeDays e MaxSourceActivityAgeDays devem estar entre 1 e 365 dias, e " +
            "AcquisitionGapCaveatHours entre 1 e 8760 horas.");
}

/// <summary>Política temporal efetiva (valores já validados).</summary>
public sealed record CrossSourcePolicy(int MaxEvidenceAgeDays, int MaxSourceActivityAgeDays, int AcquisitionGapCaveatHours)
{
    public static CrossSourcePolicy Default { get; } = new CrossSourceCorrelationOptions().ToPolicy();

    public TimeSpan MaxEvidenceAge => TimeSpan.FromDays(MaxEvidenceAgeDays);
    public TimeSpan MaxSourceActivityAge => TimeSpan.FromDays(MaxSourceActivityAgeDays);
    public TimeSpan AcquisitionGapCaveat => TimeSpan.FromHours(AcquisitionGapCaveatHours);
}

// ---- Regras ---------------------------------------------------------------------------------------------------------

/// <summary>Condição de dispositivo informada pela fonte de gestão que a regra combina com as vulnerabilidades.</summary>
public enum CrossSourceDeviceCondition
{
    /// <summary><c>complianceState = noncompliant</c> no Intune.</summary>
    Noncompliant = 1,

    /// <summary><c>isEncrypted = false</c> no Intune.</summary>
    NotEncrypted = 2,
}

/// <summary>Definição ESTÁVEL e versionada de uma regra. O código nunca muda de significado; mudança de critério = nova versão.</summary>
public sealed record CrossSourceRuleDefinition(
    string Code,
    int Version,
    CrossSourceDeviceCondition Condition,
    string Title,
    IReadOnlyList<string> Criteria,
    string WhatToVerify,
    string Limitation);

public static class CrossSourceRuleCodes
{
    public const string VulnerableAndNoncompliant = "XS-DEF-INT-NONCOMPLIANT";
    public const string VulnerableAndUnencrypted = "XS-DEF-INT-UNENCRYPTED";
}

/// <summary>Catálogo FECHADO das regras entregues neste pacote.</summary>
public static class CrossSourceRules
{
    private const string LinkCriterion =
        "O ativo tem registro do Microsoft Defender e registro do Microsoft Intune vinculados pela mesma chave forte: " +
        "mesmo tenant, mesmo ativo canônico e mesmo identificador de dispositivo do Microsoft Entra, no mesmo diretório " +
        "(um rótulo agregado de vínculo não basta).";

    private const string AttributionCriterion =
        "Nenhum registro ativo do ativo está em conflito de vínculo, e todos os registros ativos do conector do Defender " +
        "neste ativo são elegíveis — as vulnerabilidades são atribuídas ao dispositivo sem ambiguidade.";

    private const string VulnerabilityCriterion =
        "Há ao menos uma vulnerabilidade EM ABERTO observada pelo conector do Defender vinculado, numa aquisição dentro " +
        "da política temporal. Vulnerabilidade não mais reportada não conta.";

    private const string TimeCriterion =
        "Registros inativos na fonte, obtidos fora da política temporal ou sem atividade recente informada pela fonte " +
        "não participam. As fontes não precisam ter o mesmo horário: cada evidência mantém a própria data.";

    private const string CoexistenceOnly =
        "A regra afirma apenas que as duas condições coexistem no mesmo dispositivo, segundo as fontes, nas datas de " +
        "cada evidência. ";

    private const string NotAnIncident =
        " Não é incidente confirmado, risco calculado, prioridade nem exposição à internet.";

    public static readonly CrossSourceRuleDefinition VulnerableAndNoncompliant = new(
        CrossSourceRuleCodes.VulnerableAndNoncompliant,
        Version: 1,
        CrossSourceDeviceCondition.Noncompliant,
        "Vulnerabilidades reportadas pelo Defender em dispositivo não conforme segundo o Intune",
        new[]
        {
            LinkCriterion,
            AttributionCriterion,
            VulnerabilityCriterion,
            "O registro elegível do Intune informa complianceState = noncompliant, e nenhum outro registro elegível do " +
            "Intune para o mesmo dispositivo informa conforme ou em período de carência. Valor desconhecido, em conflito, " +
            "com erro ou gerenciado externamente não sustenta nem descarta a condição.",
            TimeCriterion,
        },
        "Revise as vulnerabilidades reportadas pelo Defender neste dispositivo e as políticas de conformidade que o " +
        "Intune indica como não atendidas. Trate cada item pelo processo de gestão de vulnerabilidades e de conformidade " +
        "do dispositivo; o AEGIS não aplica correção automaticamente.",
        CoexistenceOnly +
        "Não afirma que a não conformidade permita explorar essas vulnerabilidades, nem que o dispositivo esteja " +
        "comprometido." + NotAnIncident);

    public static readonly CrossSourceRuleDefinition VulnerableAndUnencrypted = new(
        CrossSourceRuleCodes.VulnerableAndUnencrypted,
        Version: 1,
        CrossSourceDeviceCondition.NotEncrypted,
        "Vulnerabilidades reportadas pelo Defender em dispositivo sem criptografia segundo o Intune",
        new[]
        {
            LinkCriterion,
            AttributionCriterion,
            VulnerabilityCriterion,
            "O registro elegível do Intune informa isEncrypted = false, e nenhum outro registro elegível do Intune para o " +
            "mesmo dispositivo informa criptografia. Criptografia não informada não sustenta nem descarta a condição.",
            TimeCriterion,
        },
        "Revise as vulnerabilidades reportadas pelo Defender neste dispositivo e a criptografia do armazenamento " +
        "informada pelo Intune. Trate cada item pelo processo de gestão de vulnerabilidades e de proteção do " +
        "dispositivo; o AEGIS não aplica correção automaticamente.",
        CoexistenceOnly +
        "Não afirma que a falta de criptografia permita explorar essas vulnerabilidades, nem que o dispositivo esteja " +
        "comprometido." + NotAnIncident);

    public static IReadOnlyList<CrossSourceRuleDefinition> All { get; } =
        new[] { VulnerableAndNoncompliant, VulnerableAndUnencrypted };

    public static CrossSourceRuleDefinition? Find(string? code) =>
        All.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Estado de UMA regra num ativo (códigos estáveis; o texto vem de <see cref="CrossSourceNarrative"/>).</summary>
public static class CrossSourceStates
{
    /// <summary>As duas condições foram identificadas nas evidências elegíveis (com ou sem ressalvas).</summary>
    public const string Identified = "identified";

    /// <summary>Evidência elegível suficiente, e ao menos uma das condições não está presente.</summary>
    public const string NotIdentified = "notIdentified";

    /// <summary>Não é possível verificar: falta evidência elegível, determinada ou dentro da política.</summary>
    public const string InsufficientEvidence = "insufficientEvidence";

    /// <summary>Vínculo em conflito: a combinação não é avaliada; as evidências individuais seguem visíveis.</summary>
    public const string LinkConflict = "linkConflict";

    /// <summary>Registros elegíveis da mesma fonte informam fatos contraditórios: nenhum é escolhido.</summary>
    public const string ContradictoryEvidence = "contradictoryEvidence";

    /// <summary>O ativo não tem registros das duas fontes: a regra não se aplica a ele.</summary>
    public const string NotEvaluated = "notEvaluated";

    public static IReadOnlyList<string> All { get; } =
        new[] { Identified, NotIdentified, InsufficientEvidence, LinkConflict, ContradictoryEvidence, NotEvaluated };

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);
}

/// <summary>Papel de uma fonte na correlação.</summary>
public enum CrossSourceRole
{
    /// <summary>Vulnerabilidades por dispositivo (Microsoft Defender Vulnerability Management).</summary>
    Vulnerabilities = 1,

    /// <summary>Gestão de dispositivos: conformidade e criptografia informadas (Microsoft Intune).</summary>
    DeviceManagement = 2,
}

public static class CrossSourceSources
{
    /// <summary>Papel do conector nas regras deste pacote — só as duas fontes que elas nomeiam.</summary>
    public static CrossSourceRole? RoleOf(ConnectorProvider provider, ConnectorCapability capability) =>
        (provider, capability) switch
        {
            (ConnectorProvider.Microsoft, ConnectorCapability.VulnerabilityScanner) => CrossSourceRole.Vulnerabilities,
            (ConnectorProvider.Microsoft, ConnectorCapability.ConfigAnalyzer) => CrossSourceRole.DeviceManagement,
            _ => null,
        };
}

/// <summary>Relação entre um fato e a aquisição mais recente publicada da fonte dele (códigos estáveis).</summary>
public static class CrossSourceAcquisitionStates
{
    /// <summary>Aquisição mais recente da fonte, publicação concluída e completa.</summary>
    public const string Current = "current";

    /// <summary>Aquisição mais recente, concluída sem completude (só fatos positivos publicados).</summary>
    public const string CurrentPartial = "currentPartial";

    /// <summary>Aquisição mais recente cuja publicação não foi concluída (em andamento ou interrompida).</summary>
    public const string CurrentUnconcluded = "currentUnconcluded";

    /// <summary>Aquisição mais recente cujo desfecho não foi registrado (anterior a este registro).</summary>
    public const string CurrentNotRecorded = "currentNotRecorded";

    /// <summary>Observado numa aquisição ANTERIOR e ainda não reconfirmado pela mais recente — evidência preservada.</summary>
    public const string Previous = "previous";

    /// <summary>A fonte não tem marca de aquisição publicada: não há referência para comparar.</summary>
    public const string NotRecorded = "notRecorded";
}

/// <summary>
/// Conclusão da AVALIAÇÃO sobre a associação entre os registros das duas fontes no ativo (códigos estáveis). É distinta do
/// critério (o requisito) e do estado da regra: pode haver associação comprovada com fatos de postura insuficientes.
/// </summary>
public static class CrossSourceAssociationStates
{
    /// <summary>Registros elegíveis das duas fontes compartilham a única chave forte do ativo.</summary>
    public const string Proven = "proven";

    /// <summary>As duas fontes têm registro no ativo, mas a avaliação não comprovou a associação.</summary>
    public const string NotProven = "notProven";

    /// <summary>Há registro ativo em conflito de vínculo: a associação não está resolvida.</summary>
    public const string Conflict = "conflict";

    /// <summary>Falta registro de uma das fontes no ativo: não há associação entre fontes a avaliar.</summary>
    public const string SourceMissing = "sourceMissing";
}

/// <summary>O que representam as datas apresentadas para um resultado (códigos estáveis).</summary>
public static class CrossSourceEvidenceBasis
{
    /// <summary>Evidências que SUSTENTAM a conclusão (identificada ou não identificada).</summary>
    public const string Supporting = "supporting";

    /// <summary>Evidências elegíveis disponíveis, sem conclusão combinada.</summary>
    public const string Available = "available";

    /// <summary>Nenhuma evidência elegível participa (fonte ausente, conflito, associação não comprovada).</summary>
    public const string None = "none";
}

// ---- Fatos de entrada (obtidos pela infraestrutura) -----------------------------------------------------------------

/// <summary>Chave forte do ativo (namespace do diretório + identificador de dispositivo). Dado interno — nunca exposto.</summary>
public readonly record struct CrossSourceKey(string DirectoryNamespace, string DeviceId);

/// <summary>Estado da FONTE (conector) relevante para qualificar os fatos dela.</summary>
public sealed record CrossSourceConnectorFacts(
    Guid ConnectorId,
    CrossSourceRole Role,
    /// <summary>Produto da fonte (ex.: "Microsoft Intune").</summary>
    string Label,
    /// <summary>Nome da integração configurada — distingue duas instâncias do mesmo produto. Não é identificador técnico.</summary>
    string ConnectorName,
    /// <summary>Marca da aquisição mais recente publicada (instante em que a coleta começou).</summary>
    DateTimeOffset? Watermark,
    DeviceSnapshotOutcome Outcome,
    /// <summary>A tentativa mais recente da dimensão falhou (os fatos armazenados são de aquisição anterior).</summary>
    bool LatestAttemptFailed,
    /// <summary>Instante da tentativa que falhou, quando a fonte o registra.</summary>
    DateTimeOffset? LatestAttemptAt);

/// <summary>Fatos de UM registro de fonte (binding) do ativo. Identificadores são internos à avaliação.</summary>
public sealed record CrossSourceBindingFacts(
    Guid BindingId,
    Guid ConnectorId,
    string? SourceLabel,
    bool IsActive,
    AssetBindingResolutionState ResolutionState,
    AssetBindingConflictKind ConflictKind,
    string? DirectoryNamespace,
    string? DirectoryDeviceId,
    DateTimeOffset FirstObservedAt,
    /// <summary>Marca da aquisição do AEGIS que observou o registro por último.</summary>
    DateTimeOffset LastObservedAt,
    /// <summary>Última atividade informada PELA FONTE.</summary>
    DateTimeOffset? SourceLastSeenAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? LinkedAt,
    DeviceComplianceBucket? Compliance,
    DeviceEncryptionBucket? Encryption);

/// <summary>Observações de vulnerabilidade de UM conector no ativo, agregadas por (ciclo de vida, marca da aquisição).</summary>
public sealed record CrossSourceObservationGroup(Guid ConnectorId, ObservationLifecycle Lifecycle, DateTimeOffset LastSeenAt, int Count);

/// <summary>Tudo o que a avaliação de UM ativo precisa — lido de uma só vez, numa leitura coerente.</summary>
public sealed record CrossSourceAssetFacts(
    Guid AssetId,
    string AssetName,
    bool NameIsPlaceholder,
    IReadOnlyCollection<CrossSourceKey> StrongKeys,
    IReadOnlyList<CrossSourceBindingFacts> Bindings,
    IReadOnlyDictionary<Guid, CrossSourceConnectorFacts> Connectors,
    IReadOnlyList<CrossSourceObservationGroup> Observations);

// ---- Resultado da avaliação (interno; a infraestrutura projeta nos DTOs) -------------------------------------------

public sealed record CrossSourceNoteDto(string Code, string Text);

/// <summary>Elegibilidade de UM registro de fonte (códigos estáveis).</summary>
public static class CrossSourceEligibility
{
    public const string Eligible = "eligible";
    public const string NotObserved = "notObserved";
    public const string Conflict = "conflict";
    public const string NotLinked = "notLinked";
    public const string KeyMismatch = "keyMismatch";
    public const string OutOfPolicy = "outOfPolicy";
    public const string InactiveInSource = "inactiveInSource";
    public const string AmbiguousAttribution = "ambiguousAttribution";
}

public sealed record CrossSourceRecordAssessment(
    CrossSourceBindingFacts Binding,
    CrossSourceConnectorFacts Connector,
    string Eligibility,
    string? ExclusionReason,
    string AcquisitionState)
{
    public bool Eligible => Eligibility == CrossSourceEligibility.Eligible;
}

/// <summary>Evidência de vulnerabilidades de UM conector usável (atribuição inequívoca), já qualificada no tempo.</summary>
public sealed record CrossSourceVulnerabilityEvidence(
    CrossSourceConnectorFacts Connector,
    /// <summary>Marcas das observações ABERTAS dentro da política — é por elas que a infraestrutura lista as CVEs.</summary>
    IReadOnlyList<DateTimeOffset> EligibleMarkers,
    int OpenCurrent,
    int OpenPrevious,
    int OpenOutOfPolicy,
    int NoLongerReported)
{
    public int OpenEligible => OpenCurrent + OpenPrevious;
}

public sealed record CrossSourceRuleAssessment(
    CrossSourceRuleDefinition Rule,
    string State,
    IReadOnlyList<CrossSourceNoteDto> Caveats,
    IReadOnlyList<string> Reasons,
    /// <summary>
    /// Registros da gestão de dispositivos que sustentam a CONCLUSÃO: a condição presente ("identificada") ou não
    /// correspondente ("não identificada" pelo lado do dispositivo). Vazio sem conclusão.
    /// </summary>
    IReadOnlyList<CrossSourceRecordAssessment> SupportingDeviceRecords,
    /// <summary>O que representam as datas abaixo (<see cref="CrossSourceEvidenceBasis"/>).</summary>
    string EvidenceBasis,
    /// <summary>Aquisições do Defender que participam — marcas distintas, em ordem crescente (nunca só a mais recente).</summary>
    IReadOnlyList<DateTimeOffset> VulnerabilityAcquisitions,
    /// <summary>Aquisições do Intune que participam — marcas distintas, em ordem crescente.</summary>
    IReadOnlyList<DateTimeOffset> DeviceAcquisitions,
    /// <summary>A conclusão negativa se apoia na ausência de vulnerabilidade em aberto numa aquisição completa do Defender.</summary>
    bool VulnerabilityAbsenceSupports = false);

/// <summary>Conclusão sobre a associação (<see cref="CrossSourceAssociationStates"/>) e o texto derivado da avaliação.</summary>
public sealed record CrossSourceAssociation(string State, string Text);

public sealed record CrossSourceAssetAssessment(
    CrossSourceAssetFacts Facts,
    CrossSourceAssociation Association,
    IReadOnlyList<CrossSourceRecordAssessment> Records,
    IReadOnlyList<CrossSourceVulnerabilityEvidence> Vulnerabilities,
    IReadOnlyList<CrossSourceRuleAssessment> Rules);

/// <summary>[AEGIS-RISK-PRIORITIZATION-01] Estado da atribuição das vulnerabilidades de UMA fonte ao ativo (códigos estáveis).</summary>
public static class CrossSourceVulnerabilitySourceStates
{
    /// <summary>Há registro atual e inequívoco da fonte: as observações nas marcas elegíveis são deste ativo.</summary>
    public const string Attributable = "attributable";

    /// <summary>O ativo não tem registro da fonte de vulnerabilidades.</summary>
    public const string NoSourceRecord = "noSourceRecord";

    /// <summary>Os registros da fonte não estão atualmente observados (inativos, fora da política, sem atividade recente).</summary>
    public const string NotCurrentlyObserved = "notCurrentlyObserved";

    /// <summary>Outro registro ativo do mesmo conector não é atual: a atribuição é ambígua e nada é escolhido.</summary>
    public const string AmbiguousAttribution = "ambiguousAttribution";
}

/// <summary>
/// [AEGIS-RISK-PRIORITIZATION-01] Vulnerabilidades da fonte atribuídas ao ativo SEM exigir a segunda fonte — produzido pela
/// mesma autoridade (<see cref="CrossSourceCorrelationEvaluator.AssessVulnerabilitySource"/>), com as mesmas regras de
/// ciclo de vida, política temporal e atribuição inequívoca.
/// </summary>
public sealed record CrossSourceVulnerabilitySource(
    string State,
    IReadOnlyList<CrossSourceRecordAssessment> Records,
    /// <summary>Evidência por conector usável — marcas elegíveis, abertas atuais/anteriores, fora da política, não mais reportadas.</summary>
    IReadOnlyList<CrossSourceVulnerabilityEvidence> Evidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<CrossSourceNoteDto> Caveats,
    /// <summary>
    /// O que a fonte NÃO reporta pode ser dado como ausente? Só com o registro reconfirmado pela aquisição mais recente e
    /// ela completa — a mesma regra do lado das vulnerabilidades na combinação. Atribuível não é prova de ausência.
    /// </summary>
    bool AbsenceConclusive,
    /// <summary>Por que a ausência não pode ser concluída (vazio quando conclusiva ou sem atribuição).</summary>
    IReadOnlyList<string> AbsenceReasons);

/// <summary>
/// Autoridade ÚNICA e DETERMINÍSTICA das regras: mesma entrada ⇒ mesma saída, independente da ordem dos registros.
/// Pura — não lê banco nem relógio (o instante da avaliação é recebido). Usada pelo detalhe do ativo e pela Central.
/// </summary>
public static class CrossSourceCorrelationEvaluator
{
    private enum Side { Present, Absent, Undetermined, Contradictory }

    public static CrossSourceAssetAssessment Evaluate(CrossSourceAssetFacts f, CrossSourcePolicy policy, DateTimeOffset now)
    {
        var keys = f.StrongKeys.ToHashSet();
        var roleBindings = f.Bindings
            .Where(b => f.Connectors.ContainsKey(b.ConnectorId))
            .OrderBy(b => (int)f.Connectors[b.ConnectorId].Role)
            .ThenByDescending(b => b.IsActive)
            .ThenByDescending(b => b.LastObservedAt)
            .ThenBy(b => b.BindingId)
            .ToList();
        var records = roleBindings
            .Select(b => AssessRecord(b, f.Connectors[b.ConnectorId], keys, policy, now))
            .ToList();

        IReadOnlyList<CrossSourceRuleAssessment> All(string state, IReadOnlyList<string> reasons) =>
            CrossSourceRules.All
                .Select(r => new CrossSourceRuleAssessment(r, state, Array.Empty<CrossSourceNoteDto>(), reasons,
                    Array.Empty<CrossSourceRecordAssessment>(), CrossSourceEvidenceBasis.None,
                    Array.Empty<DateTimeOffset>(), Array.Empty<DateTimeOffset>()))
                .ToList();

        var hasVuln = records.Any(r => r.Connector.Role == CrossSourceRole.Vulnerabilities);
        var hasMgmt = records.Any(r => r.Connector.Role == CrossSourceRole.DeviceManagement);
        if (!hasVuln || !hasMgmt)
        {
            var missing = new List<string>();
            if (!hasVuln) missing.Add("o ativo não tem registro do Microsoft Defender (vulnerabilidades)");
            if (!hasMgmt) missing.Add("o ativo não tem registro do Microsoft Intune (gestão de dispositivos)");
            var association = new CrossSourceAssociation(CrossSourceAssociationStates.SourceMissing,
                "Associação entre fontes não avaliada: " + string.Join("; ", missing) + "." + IdentityNotes(records));
            return new CrossSourceAssetAssessment(f, association, records, Array.Empty<CrossSourceVulnerabilityEvidence>(),
                All(CrossSourceStates.NotEvaluated, missing));
        }

        // (1) Conflito em QUALQUER registro ativo do ativo: nenhuma combinação é avaliada — o vínculo não está resolvido.
        var conflicts = f.Bindings.Where(b => b.IsActive && b.ResolutionState == AssetBindingResolutionState.Conflict)
            .OrderBy(b => b.BindingId).ToList();
        if (conflicts.Count > 0)
        {
            var reasons = conflicts
                .Select(b =>
                {
                    var (label, _) = AssetCrossSourceNarrative.ForBinding(
                        b.ResolutionState, b.ConflictKind, DirectoryIdentifierStatus.NotEvaluated);
                    return $"{LabelOf(b, f.Connectors)}: {label}.";
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var association = new CrossSourceAssociation(CrossSourceAssociationStates.Conflict,
                "Associação não comprovada: há registro deste ativo em conflito de vínculo, e a associação só é avaliada " +
                "quando ele estiver resolvido. " + string.Join(" ", reasons));
            return new CrossSourceAssetAssessment(f, association, records, Array.Empty<CrossSourceVulnerabilityEvidence>(),
                All(CrossSourceStates.LinkConflict, reasons));
        }

        // (2) Chave do vínculo: a ÚNICA chave forte do ativo compartilhada pelos registros elegíveis das duas fontes.
        var vulnKeys = EligibleKeys(records, CrossSourceRole.Vulnerabilities);
        var mgmtKeys = EligibleKeys(records, CrossSourceRole.DeviceManagement);
        var common = vulnKeys.Intersect(mgmtKeys).ToList();
        if (vulnKeys.Count == 0 || mgmtKeys.Count == 0 || common.Count != 1)
        {
            var reasons = new List<string>();
            if (vulnKeys.Count == 0) reasons.Add(NoEligibleRecord(records, CrossSourceRole.Vulnerabilities));
            if (mgmtKeys.Count == 0) reasons.Add(NoEligibleRecord(records, CrossSourceRole.DeviceManagement));
            if (vulnKeys.Count > 0 && mgmtKeys.Count > 0)
                reasons.Add("Os registros elegíveis das duas fontes não compartilham uma única chave forte deste ativo; " +
                    "a associação entre eles não está comprovada.");
            var association = new CrossSourceAssociation(CrossSourceAssociationStates.NotProven,
                "Associação não comprovada nesta avaliação. " + string.Join(" ", reasons));
            return new CrossSourceAssetAssessment(f, association, records, Array.Empty<CrossSourceVulnerabilityEvidence>(),
                All(CrossSourceStates.InsufficientEvidence, reasons));
        }

        var linkKey = common[0];
        records = records
            .Select(r => r.Eligible && KeyOf(r.Binding) != linkKey
                ? r with
                {
                    Eligibility = CrossSourceEligibility.KeyMismatch,
                    ExclusionReason = "Registro de outro dispositivo de diretório vinculado a este ativo; não participa desta combinação.",
                }
                : r)
            .ToList();

        // (3) Atribuição das vulnerabilidades: a observação é por (conector, ativo×CVE), não por registro. Se o mesmo
        // conector tem outro registro ATIVO neste ativo que não é elegível, as CVEs não podem ser atribuídas só aos
        // registros elegíveis — o conector inteiro sai da combinação (nunca se escolhe o registro mais conveniente).
        var ambiguous = records
            .Where(r => r.Connector.Role == CrossSourceRole.Vulnerabilities)
            .GroupBy(r => r.Connector.ConnectorId)
            .Where(g => g.Any(r => r.Eligible) && g.Any(r => r.Binding.IsActive && !r.Eligible))
            .Select(g => g.Key)
            .ToHashSet();
        if (ambiguous.Count > 0)
            records = records
                .Select(r => r.Eligible && ambiguous.Contains(r.Connector.ConnectorId)
                    ? r with
                    {
                        Eligibility = CrossSourceEligibility.AmbiguousAttribution,
                        ExclusionReason = "Outro registro ativo do mesmo conector neste ativo não é elegível; as vulnerabilidades " +
                            "não podem ser atribuídas apenas aos registros elegíveis.",
                    }
                    : r)
                .ToList();

        var usableVulnConnectors = records
            .Where(r => r.Eligible && r.Connector.Role == CrossSourceRole.Vulnerabilities)
            .Select(r => r.Connector)
            .DistinctBy(c => c.ConnectorId)
            .OrderBy(c => c.ConnectorId)
            .ToList();
        var vulnerabilities = usableVulnConnectors
            .Select(c => VulnerabilityEvidence(c, f.Observations, policy, now))
            .ToList();

        var mgmtRecords = records.Where(r => r.Eligible && r.Connector.Role == CrossSourceRole.DeviceManagement).ToList();
        var vulnRecords = records.Where(r => r.Eligible && r.Connector.Role == CrossSourceRole.Vulnerabilities).ToList();

        var (vulnSide, vulnReasons) = VulnerabilitySide(vulnerabilities, vulnRecords, records, ambiguous.Count > 0);
        var rules = CrossSourceRules.All
            .Select(rule => EvaluateRule(rule, vulnSide, vulnReasons, vulnerabilities, vulnRecords, mgmtRecords, records,
                ambiguous, policy))
            .ToList();

        // A chave compartilhada comprova a associação dos registros; a atribuição das CVEs é outra questão e é dita à parte.
        var proven = new CrossSourceAssociation(CrossSourceAssociationStates.Proven,
            CrossSourceNarrative.AssociationProven + (ambiguous.Count > 0
                ? " As vulnerabilidades, porém, não podem ser atribuídas sem ambiguidade a este dispositivo (ver o motivo " +
                  "de cada regra)."
                : ""));
        return new CrossSourceAssetAssessment(f, proven, records, vulnerabilities, rules);
    }

    /// <summary>Motivos de IDENTIDADE dos registros presentes (sem identificador, inválido, outra chave), para a associação.</summary>
    private static string IdentityNotes(IEnumerable<CrossSourceRecordAssessment> records)
    {
        var notes = records
            .Where(r => r.Eligibility is CrossSourceEligibility.NotLinked or CrossSourceEligibility.KeyMismatch && r.ExclusionReason is not null)
            .Select(r => $"{r.Connector.Label}: {r.ExclusionReason}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return notes.Count == 0 ? "" : " " + string.Join(" ", notes);
    }

    private static IReadOnlyList<DateTimeOffset> Dates(IEnumerable<DateTimeOffset> dates) =>
        dates.Distinct().OrderBy(d => d).ToList();

    // ---- Registro ---------------------------------------------------------------------------------------------------

    private static CrossSourceRecordAssessment AssessRecord(
        CrossSourceBindingFacts b, CrossSourceConnectorFacts c, HashSet<CrossSourceKey> keys,
        CrossSourcePolicy policy, DateTimeOffset now)
    {
        var acquisition = AcquisitionState(b.LastObservedAt, c);
        CrossSourceRecordAssessment Out(string code, string reason) => new(b, c, code, reason, acquisition);

        if (!b.IsActive)
            return Out(CrossSourceEligibility.NotObserved,
                "Não mais observado pela fonte" + (b.ResolvedAt is { } r ? $" desde {Utc(r)}" : "") +
                " — não é tratado como atualmente observado nem sustenta condição aberta.");
        if (b.ResolutionState == AssetBindingResolutionState.Conflict)
            return Out(CrossSourceEligibility.Conflict, "Registro em conflito de vínculo; suas informações seguem visíveis, fora da combinação.");
        if (b.ResolutionState != AssetBindingResolutionState.Linked)
            return Out(CrossSourceEligibility.NotLinked, b.ResolutionState switch
            {
                AssetBindingResolutionState.NotEvaluated => "Vínculo ainda não avaliado por uma coleta com identificador de diretório.",
                AssetBindingResolutionState.NoIdentifier => "A fonte não informou o identificador de dispositivo do diretório; o vínculo não está comprovado.",
                AssetBindingResolutionState.InvalidIdentifier => "A fonte informou identificador de diretório inválido; o vínculo não está comprovado.",
                AssetBindingResolutionState.DirectoryUnconfirmed => "Diretório de origem não confirmado; o vínculo não está comprovado.",
                _ => "Vínculo sem comprovação suficiente.",
            });
        if (KeyOf(b) is not { } key || !keys.Contains(key))
            return Out(CrossSourceEligibility.KeyMismatch, "O vínculo registrado não corresponde à chave forte deste ativo.");
        if (TemporalExclusion(b, policy, now) is { } t)
            return Out(t.Code, t.Reason);
        return new CrossSourceRecordAssessment(b, c, CrossSourceEligibility.Eligible, null, acquisition);
    }

    /// <summary>Política temporal de UM registro: aquisição fora da janela ou sem atividade recente informada pela fonte.</summary>
    private static (string Code, string Reason)? TemporalExclusion(CrossSourceBindingFacts b, CrossSourcePolicy policy, DateTimeOffset now)
    {
        if (now - b.LastObservedAt > policy.MaxEvidenceAge)
            return (CrossSourceEligibility.OutOfPolicy,
                $"Obtido pelo AEGIS em {Utc(b.LastObservedAt)}, fora da política temporal (até {policy.MaxEvidenceAgeDays} dia(s)).");
        if (b.SourceLastSeenAt is { } seen && now - seen > policy.MaxSourceActivityAge)
            return (CrossSourceEligibility.InactiveInSource,
                $"Última atividade informada pela fonte em {Utc(seen)}, além de {policy.MaxSourceActivityAgeDays} dia(s) — " +
                "o dispositivo não é tratado como atualmente observado.");
        return null;
    }

    // ---- [AEGIS-RISK-PRIORITIZATION-01] Vulnerabilidades de UMA fonte, sem exigir a segunda --------------------------

    /// <summary>
    /// Atribuição das vulnerabilidades da fonte de vulnerabilidades (Defender) a ESTE ativo, sem exigir a fonte de gestão
    /// (Intune) nem o vínculo por chave forte — que só importam para COMBINAR as duas. As regras são as mesmas da
    /// combinação, e o código é o mesmo: registro não mais observado não sustenta nada; aquisição fora da política
    /// temporal ou sem atividade recente na fonte não é tratada como atual; outro registro ATIVO do mesmo conector que não
    /// seja atribuível torna a atribuição ambígua (nunca se escolhe o registro mais conveniente); e as observações só
    /// contam nas marcas de aquisição dentro da política. Uma vulnerabilidade grave num dispositivo sem Intune continua
    /// visível e atribuída.
    /// </summary>
    public static CrossSourceVulnerabilitySource AssessVulnerabilitySource(
        CrossSourceAssetFacts f, CrossSourcePolicy policy, DateTimeOffset now)
    {
        var records = f.Bindings
            .Where(b => f.Connectors.TryGetValue(b.ConnectorId, out var c) && c.Role == CrossSourceRole.Vulnerabilities)
            .OrderByDescending(b => b.IsActive)
            .ThenByDescending(b => b.LastObservedAt)
            .ThenBy(b => b.BindingId)
            .Select(b =>
            {
                var c = f.Connectors[b.ConnectorId];
                var acquisition = AcquisitionState(b.LastObservedAt, c);
                if (!b.IsActive)
                    return new CrossSourceRecordAssessment(b, c, CrossSourceEligibility.NotObserved,
                        "Não mais observado pela fonte" + (b.ResolvedAt is { } r ? $" desde {Utc(r)}" : "") +
                        " — não é tratado como atualmente observado nem sustenta condição aberta.", acquisition);
                return TemporalExclusion(b, policy, now) is { } t
                    ? new CrossSourceRecordAssessment(b, c, t.Code, t.Reason, acquisition)
                    : new CrossSourceRecordAssessment(b, c, CrossSourceEligibility.Eligible, null, acquisition);
            })
            .ToList();

        if (records.Count == 0)
            return new CrossSourceVulnerabilitySource(CrossSourceVulnerabilitySourceStates.NoSourceRecord, records,
                Array.Empty<CrossSourceVulnerabilityEvidence>(),
                new[] { "O ativo não tem registro do Microsoft Defender (vulnerabilidades)." }, Array.Empty<CrossSourceNoteDto>(),
                AbsenceConclusive: false, AbsenceReasons: Array.Empty<string>());

        var ambiguous = records
            .GroupBy(r => r.Connector.ConnectorId)
            .Where(g => g.Any(r => r.Eligible) && g.Any(r => r.Binding.IsActive && !r.Eligible))
            .Select(g => g.Key)
            .ToHashSet();
        if (ambiguous.Count > 0)
            records = records
                .Select(r => r.Eligible && ambiguous.Contains(r.Connector.ConnectorId)
                    ? r with
                    {
                        Eligibility = CrossSourceEligibility.AmbiguousAttribution,
                        ExclusionReason = "Outro registro ativo do mesmo conector neste ativo não está atualmente observado; as " +
                            "vulnerabilidades não podem ser atribuídas apenas ao registro atual.",
                    }
                    : r)
                .ToList();

        var usable = records.Where(r => r.Eligible).ToList();
        var evidence = usable
            .Select(r => r.Connector)
            .DistinctBy(c => c.ConnectorId)
            .OrderBy(c => c.ConnectorId)
            .Select(c => VulnerabilityEvidence(c, f.Observations, policy, now))
            .ToList();

        if (evidence.Count == 0)
        {
            var why = records.Where(r => r.ExclusionReason is not null)
                .Select(r => $"{r.Connector.Label}: {r.ExclusionReason}")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return new CrossSourceVulnerabilitySource(
                ambiguous.Count > 0
                    ? CrossSourceVulnerabilitySourceStates.AmbiguousAttribution
                    : CrossSourceVulnerabilitySourceStates.NotCurrentlyObserved,
                records, evidence, why, Array.Empty<CrossSourceNoteDto>(),
                AbsenceConclusive: false, AbsenceReasons: Array.Empty<string>());
        }

        // As MESMAS ressalvas que qualificam o lado das vulnerabilidades numa combinação (parcial, publicação não concluída,
        // desfecho não registrado, aquisição anterior preservada, fora da política, tentativa recente falha).
        var caveats = Caveats(new EvidenceInUse(evidence, VulnerabilityAbsence: false, usable, Array.Empty<CrossSourceRecordAssessment>()),
            rule: null, records, ambiguous, policy, vulnDates: null, deviceDates: null);
        var (conclusive, absenceReasons) = VulnerabilityAbsence(evidence, usable);
        return new CrossSourceVulnerabilitySource(CrossSourceVulnerabilitySourceStates.Attributable, records, evidence,
            Array.Empty<string>(), caveats, conclusive, absenceReasons);
    }

    /// <summary>
    /// Completude para concluir AUSÊNCIA de vulnerabilidade em aberto — regra única, usada pela combinação e pela
    /// prioridade: o registro do dispositivo foi reconfirmado pela aquisição mais recente de cada fonte usável, e ela foi
    /// publicada COMPLETA. Parcial, publicação não concluída, desfecho não registrado ou registro não reconfirmado: não
    /// conclui (fatos positivos continuam valendo).
    /// </summary>
    public static (bool Conclusive, IReadOnlyList<string> Reasons) VulnerabilityAbsence(
        IReadOnlyList<CrossSourceVulnerabilityEvidence> evidence, IReadOnlyList<CrossSourceRecordAssessment> vulnRecords)
    {
        var reasons = new List<string>();
        foreach (var e in evidence)
        {
            var recs = vulnRecords.Where(r => r.Connector.ConnectorId == e.Connector.ConnectorId).ToList();
            if (recs.Any(r => r.AcquisitionState == CrossSourceAcquisitionStates.Previous))
            {
                reasons.Add($"{e.Connector.Label}: o registro do dispositivo não foi reconfirmado pela aquisição mais recente; a " +
                    "ausência de vulnerabilidades não pode ser concluída.");
                continue;
            }
            if (AcquisitionIncompleteness(e.Connector) is { } why) reasons.Add(why);
        }
        return (evidence.Count > 0 && reasons.Count == 0, reasons);
    }

    /// <summary>Por que a aquisição mais recente da fonte não sustenta ausência; <c>null</c> quando foi publicada completa.</summary>
    public static string? AcquisitionIncompleteness(CrossSourceConnectorFacts c) => c.Outcome switch
    {
        DeviceSnapshotOutcome.Complete => null,
        DeviceSnapshotOutcome.Partial =>
            $"{c.Label}: a aquisição mais recente foi parcial — nenhuma vulnerabilidade em aberto nela não prova ausência.",
        DeviceSnapshotOutcome.Publishing =>
            $"{c.Label}: a publicação da aquisição mais recente não foi concluída (em andamento ou interrompida).",
        _ => $"{c.Label}: o desfecho da aquisição mais recente não foi registrado; a completude não é afirmada.",
    };

    /// <summary>Relação do fato (pela marca da aquisição que o observou) com a aquisição mais recente publicada da fonte.</summary>
    public static string AcquisitionState(DateTimeOffset observedAt, CrossSourceConnectorFacts c)
    {
        if (c.Watermark is not { } watermark) return CrossSourceAcquisitionStates.NotRecorded;
        if (observedAt < watermark) return CrossSourceAcquisitionStates.Previous;
        if (observedAt > watermark) return CrossSourceAcquisitionStates.NotRecorded;
        return c.Outcome switch
        {
            DeviceSnapshotOutcome.Complete => CrossSourceAcquisitionStates.Current,
            DeviceSnapshotOutcome.Partial => CrossSourceAcquisitionStates.CurrentPartial,
            DeviceSnapshotOutcome.Publishing => CrossSourceAcquisitionStates.CurrentUnconcluded,
            _ => CrossSourceAcquisitionStates.CurrentNotRecorded,
        };
    }

    private static CrossSourceKey? KeyOf(CrossSourceBindingFacts b) =>
        b.DirectoryNamespace is { } ns && b.DirectoryDeviceId is { } id ? new CrossSourceKey(ns, id) : null;

    private static HashSet<CrossSourceKey> EligibleKeys(IEnumerable<CrossSourceRecordAssessment> records, CrossSourceRole role) =>
        records.Where(r => r.Eligible && r.Connector.Role == role).Select(r => KeyOf(r.Binding)!.Value).ToHashSet();

    private static string NoEligibleRecord(IReadOnlyList<CrossSourceRecordAssessment> records, CrossSourceRole role)
    {
        var why = records.Where(r => r.Connector.Role == role && r.ExclusionReason is not null)
            .Select(r => r.ExclusionReason!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var source = role == CrossSourceRole.Vulnerabilities ? "do Microsoft Defender" : "do Microsoft Intune";
        return $"Nenhum registro {source} é elegível" + (why.Count > 0 ? ": " + string.Join(" ", why) : ".");
    }

    // ---- Vulnerabilidades -------------------------------------------------------------------------------------------

    private static CrossSourceVulnerabilityEvidence VulnerabilityEvidence(
        CrossSourceConnectorFacts c, IReadOnlyList<CrossSourceObservationGroup> groups, CrossSourcePolicy policy, DateTimeOffset now)
    {
        var mine = groups.Where(g => g.ConnectorId == c.ConnectorId).ToList();
        var open = mine.Where(g => g.Lifecycle == ObservationLifecycle.Open).ToList();
        var inPolicy = open.Where(g => now - g.LastSeenAt <= policy.MaxEvidenceAge).ToList();
        var current = inPolicy.Where(g => c.Watermark is { } w && g.LastSeenAt >= w).Sum(g => g.Count);
        return new CrossSourceVulnerabilityEvidence(
            c,
            inPolicy.Select(g => g.LastSeenAt).Distinct().OrderBy(d => d).ToList(),
            OpenCurrent: current,
            OpenPrevious: inPolicy.Sum(g => g.Count) - current,
            OpenOutOfPolicy: open.Sum(g => g.Count) - inPolicy.Sum(g => g.Count),
            NoLongerReported: mine.Where(g => g.Lifecycle == ObservationLifecycle.Resolved).Sum(g => g.Count));
    }

    private static (Side Side, List<string> Reasons) VulnerabilitySide(
        IReadOnlyList<CrossSourceVulnerabilityEvidence> evidence, IReadOnlyList<CrossSourceRecordAssessment> vulnRecords,
        IReadOnlyList<CrossSourceRecordAssessment> allRecords, bool anyAmbiguous)
    {
        var reasons = new List<string>();
        if (evidence.Count == 0)
        {
            reasons.Add(anyAmbiguous
                ? "Há outro registro ativo do mesmo conector do Defender neste ativo que não é elegível (vínculo, atividade " +
                  "ou política temporal): as vulnerabilidades não podem ser atribuídas apenas ao registro elegível."
                : NoEligibleRecord(allRecords, CrossSourceRole.Vulnerabilities));
            return (Side.Undetermined, reasons);
        }
        if (evidence.Any(e => e.OpenEligible > 0)) return (Side.Present, reasons);

        // Zero só é conclusivo quando o dispositivo foi observado na aquisição mais recente, e ela foi COMPLETA.
        var (conclusive, why) = VulnerabilityAbsence(evidence, vulnRecords);
        reasons.AddRange(why);
        if (evidence.Any(e => e.OpenOutOfPolicy > 0))
            reasons.Add("Há vulnerabilidades em aberto observadas antes da política temporal; elas não são consideradas.");
        return (conclusive ? Side.Absent : Side.Undetermined, reasons);
    }

    // ---- Regra ------------------------------------------------------------------------------------------------------

    private enum DeviceFact { Present, NotMatching, Undetermined }

    public static bool? ConditionPresent(CrossSourceDeviceCondition condition, CrossSourceBindingFacts b) =>
        Classify(condition, b) switch
        {
            DeviceFact.Present => true,
            DeviceFact.NotMatching => false,
            _ => null,
        };

    private static DeviceFact Classify(CrossSourceDeviceCondition condition, CrossSourceBindingFacts b) => condition switch
    {
        CrossSourceDeviceCondition.Noncompliant => b.Compliance switch
        {
            DeviceComplianceBucket.Noncompliant => DeviceFact.Present,
            DeviceComplianceBucket.Compliant or DeviceComplianceBucket.InGracePeriod => DeviceFact.NotMatching,
            _ => DeviceFact.Undetermined,
        },
        _ => b.Encryption switch
        {
            DeviceEncryptionBucket.NotEncrypted => DeviceFact.Present,
            DeviceEncryptionBucket.Encrypted => DeviceFact.NotMatching,
            _ => DeviceFact.Undetermined,
        },
    };

    private static string ConditionFactLabel(CrossSourceDeviceCondition condition, CrossSourceBindingFacts b) =>
        (condition == CrossSourceDeviceCondition.Noncompliant
            ? AssetCrossSourceNarrative.ComplianceLabel(b.Compliance)
            : AssetCrossSourceNarrative.EncryptionLabel(b.Encryption))
        ?? (condition == CrossSourceDeviceCondition.Noncompliant ? "Conformidade não informada" : "Criptografia não informada");

    private static CrossSourceRuleAssessment EvaluateRule(
        CrossSourceRuleDefinition rule, Side vulnSide, IReadOnlyList<string> vulnReasons,
        IReadOnlyList<CrossSourceVulnerabilityEvidence> vulnerabilities, IReadOnlyList<CrossSourceRecordAssessment> vulnRecords,
        IReadOnlyList<CrossSourceRecordAssessment> mgmtRecords, IReadOnlyList<CrossSourceRecordAssessment> allRecords,
        IReadOnlySet<Guid> ambiguous, CrossSourcePolicy policy)
    {
        var present = mgmtRecords.Where(r => Classify(rule.Condition, r.Binding) == DeviceFact.Present).ToList();
        var notMatching = mgmtRecords.Where(r => Classify(rule.Condition, r.Binding) == DeviceFact.NotMatching).ToList();
        var what = rule.Condition == CrossSourceDeviceCondition.Noncompliant ? "conformidade" : "criptografia";

        // Sem conclusão combinada, as datas apresentadas são as das evidências elegíveis DISPONÍVEIS — rotuladas como tal.
        var availableVuln = Dates(vulnRecords.Select(r => r.Binding.LastObservedAt)
            .Concat(vulnerabilities.SelectMany(v => v.EligibleMarkers)));
        var availableDevice = Dates(mgmtRecords.Select(r => r.Binding.LastObservedAt));

        CrossSourceRuleAssessment Unconcluded(string state, IReadOnlyList<string> reasons) =>
            new(rule, state, Array.Empty<CrossSourceNoteDto>(), reasons, Array.Empty<CrossSourceRecordAssessment>(),
                CrossSourceEvidenceBasis.Available, availableVuln, availableDevice);

        string DeviceUndetermined()
        {
            var labels = mgmtRecords.Select(r => ConditionFactLabel(rule.Condition, r.Binding)).Distinct(StringComparer.Ordinal).ToList();
            return mgmtRecords.Count == 0
                ? NoEligibleRecord(allRecords, CrossSourceRole.DeviceManagement)
                : $"O Microsoft Intune não informou {what} determinada para este dispositivo ({string.Join("; ", labels)}).";
        }

        // Registros ELEGÍVEIS da mesma fonte discordando: política explícita — nenhum é escolhido (nem o mais
        // favorável, nem o mais grave, nem o primeiro). Os indeterminados não contradizem ninguém.
        if (present.Count > 0 && notMatching.Count > 0)
        {
            var facts = present.Concat(notMatching)
                .Select(r => ConditionFactLabel(rule.Condition, r.Binding))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return Unconcluded(CrossSourceStates.ContradictoryEvidence, new[]
            {
                $"{present.Count + notMatching.Count} registros elegíveis do Microsoft Intune para o mesmo dispositivo informam " +
                $"{what} divergente ({string.Join("; ", facts)}). Nenhum foi escolhido; a divergência é preservada.",
            });
        }

        var deviceSide = present.Count > 0 ? Side.Present : notMatching.Count > 0 ? Side.Absent : Side.Undetermined;

        if (deviceSide == Side.Present && vulnSide == Side.Present)
        {
            // Participam TODAS as aquisições com CVE em aberto elegível e todos os registros que informam a condição.
            var positive = vulnerabilities.Where(v => v.OpenEligible > 0).ToList();
            var vulnDates = Dates(positive.SelectMany(v => v.EligibleMarkers));
            var deviceDates = Dates(present.Select(r => r.Binding.LastObservedAt));
            var caveats = Caveats(new EvidenceInUse(positive, VulnerabilityAbsence: false, vulnRecords, present),
                rule, allRecords, ambiguous, policy, vulnDates, deviceDates);
            return new CrossSourceRuleAssessment(rule, CrossSourceStates.Identified, caveats, Array.Empty<string>(), present,
                CrossSourceEvidenceBasis.Supporting, vulnDates, deviceDates);
        }

        var reasons = new List<string>();
        var deviceAbsent = deviceSide == Side.Absent;
        var vulnAbsent = vulnSide == Side.Absent;
        if (deviceAbsent)
            reasons.Add($"O Microsoft Intune informa, para este dispositivo: “{ConditionFactLabel(rule.Condition, notMatching[0].Binding)}”. " +
                "A condição de dispositivo desta regra não está presente.");
        if (vulnAbsent)
            reasons.Add("A aquisição completa mais recente do Defender não reporta vulnerabilidade em aberto neste dispositivo. " +
                "Ausência de vulnerabilidades reportadas não comprova que o dispositivo esteja seguro.");
        if (deviceAbsent || vulnAbsent)
        {
            // A regra é uma CONJUNÇÃO: basta uma condição ausente para a combinação não se formar. A outra condição, quando
            // presente, continua sendo um fato informado pela fonte — o resultado não a nega.
            if (vulnSide == Side.Present)
                reasons.Add("O Microsoft Defender reporta vulnerabilidade(s) em aberto neste dispositivo; essa condição, " +
                    "isolada, continua informada pela fonte.");
            else if (vulnSide == Side.Undetermined)
                reasons.AddRange(vulnReasons);
            if (deviceSide == Side.Present)
                reasons.Add($"O Microsoft Intune informa “{ConditionFactLabel(rule.Condition, present[0].Binding)}”; essa " +
                    "condição, isolada, continua informada pela fonte.");
            else if (deviceSide == Side.Undetermined)
                reasons.Add(DeviceUndetermined());

            // Só as evidências que sustentam a conclusão negativa são qualificadas (ressalvas) e datadas.
            var deviceUsed = deviceAbsent ? notMatching : new List<CrossSourceRecordAssessment>();
            var vulnUsed = vulnAbsent ? vulnerabilities : Array.Empty<CrossSourceVulnerabilityEvidence>();
            var vulnRecordsUsed = vulnAbsent ? vulnRecords : Array.Empty<CrossSourceRecordAssessment>();
            var caveats = Caveats(new EvidenceInUse(vulnUsed, VulnerabilityAbsence: vulnAbsent, vulnRecordsUsed, deviceUsed),
                rule, allRecords, ambiguous, policy, vulnDates: null, deviceDates: null);
            return new CrossSourceRuleAssessment(rule, CrossSourceStates.NotIdentified, caveats, reasons, deviceUsed,
                CrossSourceEvidenceBasis.Supporting,
                Dates(vulnRecordsUsed.Select(r => r.Binding.LastObservedAt)),
                Dates(deviceUsed.Select(r => r.Binding.LastObservedAt)),
                VulnerabilityAbsenceSupports: vulnAbsent);
        }

        if (deviceSide == Side.Undetermined) reasons.Add(DeviceUndetermined());
        if (vulnSide == Side.Undetermined) reasons.AddRange(vulnReasons);
        return Unconcluded(CrossSourceStates.InsufficientEvidence, reasons);
    }

    /// <summary>Evidências que participam de UMA conclusão — só elas são qualificadas por ressalvas.</summary>
    private sealed record EvidenceInUse(
        IReadOnlyList<CrossSourceVulnerabilityEvidence> Vulnerabilities,
        bool VulnerabilityAbsence,
        IReadOnlyList<CrossSourceRecordAssessment> VulnerabilityRecords,
        IReadOnlyList<CrossSourceRecordAssessment> DeviceRecords);

    /// <summary>
    /// Ressalvas das evidências que PARTICIPAM de uma conclusão (identificada ou não identificada) — qualificação das
    /// evidências, separada da identificação positiva. Fontes que não participam da conclusão não geram ressalva.
    /// </summary>
    private static IReadOnlyList<CrossSourceNoteDto> Caveats(
        EvidenceInUse used, CrossSourceRuleDefinition? rule, IReadOnlyList<CrossSourceRecordAssessment> allRecords,
        IReadOnlySet<Guid> ambiguous, CrossSourcePolicy policy,
        IReadOnlyList<DateTimeOffset>? vulnDates, IReadOnlyList<DateTimeOffset>? deviceDates)
    {
        var notes = new List<CrossSourceNoteDto>();
        void Add(string code, string text)
        {
            if (!notes.Any(n => n.Code == code && n.Text == text)) notes.Add(new CrossSourceNoteDto(code, text));
        }

        foreach (var e in used.Vulnerabilities)
        {
            var label = e.Connector.Label;
            // A ausência só é conclusiva numa aquisição atual e completa; as ressalvas de completude valem para o fato positivo.
            if (!used.VulnerabilityAbsence)
            {
                switch (e.Connector.Outcome)
                {
                    case DeviceSnapshotOutcome.Partial when e.OpenCurrent > 0:
                        Add("partialAcquisition", $"{label}: a aquisição mais recente foi parcial. As vulnerabilidades reportadas " +
                            "valem como fato positivo; a ausência de outras não pode ser concluída.");
                        break;
                    case DeviceSnapshotOutcome.Publishing:
                        Add("publicationNotConcluded", $"{label}: a publicação da aquisição mais recente não foi concluída (em " +
                            "andamento ou interrompida). Cada CVE mostra de qual aquisição veio.");
                        break;
                    case DeviceSnapshotOutcome.NotRecorded:
                        Add("outcomeNotRecorded", $"{label}: o desfecho da aquisição que observou estes fatos não foi registrado " +
                            "(anterior a este registro); a completude dela não é afirmada.");
                        break;
                }
                if (e.OpenPrevious > 0)
                    Add("previousAcquisition", $"{label}: {e.OpenPrevious} CVE(s) foram observadas numa aquisição anterior e " +
                        "ainda não foram reconfirmadas pela mais recente (evidência preservada).");
            }
            if (e.OpenOutOfPolicy > 0)
                Add("excludedOutOfPolicy", $"{label}: {e.OpenOutOfPolicy} CVE(s) em aberto observadas antes da política " +
                    $"temporal ({policy.MaxEvidenceAgeDays} dia(s)) não foram consideradas.");
            if (e.Connector.LatestAttemptFailed)
                Add("latestAttemptFailed", $"{label}: a tentativa mais recente de coleta falhou; os fatos usados são da " +
                    "última aquisição publicada.");
        }

        foreach (var r in used.DeviceRecords.Concat(used.VulnerabilityRecords))
        {
            var label = r.Connector.Label;
            var management = r.Connector.Role == CrossSourceRole.DeviceManagement;
            switch (r.AcquisitionState)
            {
                case CrossSourceAcquisitionStates.Previous:
                    Add("recordNotReconfirmed", $"{label}: o registro do dispositivo foi observado na aquisição de " +
                        $"{Utc(r.Binding.LastObservedAt)} e ainda não foi reconfirmado pela mais recente (evidência preservada).");
                    // O fato é o da aquisição anterior: o desfecho da mais recente não se estende a ele.
                    if (management && r.Connector.Outcome == DeviceSnapshotOutcome.Publishing)
                        Add("publicationNotConcluded", $"{label}: a publicação da aquisição mais recente não foi concluída (em " +
                            "andamento ou interrompida); o fato usado é o da aquisição anterior preservada.");
                    else if (management && r.Connector.Outcome == DeviceSnapshotOutcome.Partial)
                        Add("partialAcquisition", $"{label}: a aquisição mais recente foi parcial e não reconfirmou este " +
                            "registro; a completude dela não se estende a este fato.");
                    break;
                case CrossSourceAcquisitionStates.CurrentPartial when r.Connector.Role == CrossSourceRole.DeviceManagement:
                    Add("partialAcquisition", $"{label}: a aquisição mais recente foi parcial; o fato deste dispositivo foi " +
                        "observado nela e vale como informado.");
                    break;
                case CrossSourceAcquisitionStates.CurrentUnconcluded when r.Connector.Role == CrossSourceRole.DeviceManagement:
                    Add("publicationNotConcluded", $"{label}: a publicação da aquisição mais recente não foi concluída.");
                    break;
                case CrossSourceAcquisitionStates.CurrentNotRecorded or CrossSourceAcquisitionStates.NotRecorded
                    when r.Connector.Role == CrossSourceRole.DeviceManagement:
                    Add("outcomeNotRecorded", $"{label}: o desfecho da aquisição que observou este fato não foi registrado; a " +
                        "completude dela não é afirmada.");
                    break;
            }
            if (r.Connector.Role == CrossSourceRole.DeviceManagement && r.Connector.LatestAttemptFailed)
                Add("latestAttemptFailed", $"{label}: a tentativa mais recente de leitura dos dispositivos falhou" +
                    (r.Connector.LatestAttemptAt is { } at ? $" ({Utc(at)})" : "") +
                    "; o fato exibido é da última aquisição publicada.");
            if (r.Binding.SourceLastSeenAt is null)
                Add("sourceActivityUnknown", $"{label}: a fonte não informou a última atividade do dispositivo.");
        }

        // Defasagem entre as fontes, sobre TODAS as evidências participantes (não só a mais recente de cada lado): a maior
        // distância entre uma aquisição do Defender e uma do Intune — pelos extremos, sem produto cartesiano. Não é
        // requisito que coincidam: é ressalva, com o intervalo de cada fonte.
        if (rule is not null && vulnDates is { Count: > 0 } && deviceDates is { Count: > 0 })
        {
            var widest = new[] { vulnDates[^1] - deviceDates[0], deviceDates[^1] - vulnDates[0] }.Max();
            if (widest > policy.AcquisitionGapCaveat)
                Add("acquisitionGap", "As evidências que sustentam a combinação foram obtidas em momentos diferentes: " +
                    $"vulnerabilidades {DateSpan(vulnDates)} e " +
                    $"{(rule.Condition == CrossSourceDeviceCondition.Noncompliant ? "conformidade" : "criptografia")} " +
                    $"{DateSpan(deviceDates)} — maior defasagem entre as fontes: " +
                    $"{Math.Round(widest.TotalHours).ToString(CultureInfo.InvariantCulture)} h. As fontes são independentes; " +
                    "a coexistência vale para as datas de cada evidência.");
        }

        var usesVuln = used.Vulnerabilities.Count > 0 || used.VulnerabilityRecords.Count > 0;
        var usesDevice = used.DeviceRecords.Count > 0;
        if (usesVuln && ambiguous.Count > 0)
            Add("excludedSource", "Um conector do Defender com registro não elegível neste ativo foi excluído da combinação " +
                "(atribuição ambígua); as evidências dele seguem visíveis.");

        var excluded = allRecords.Count(r => !r.Eligible && r.Binding.IsActive
            && (r.Connector.Role == CrossSourceRole.Vulnerabilities ? usesVuln : usesDevice));
        if (excluded > 0)
            Add("excludedRecords", $"{excluded} registro(s) ativo(s) de fonte deste ativo ficaram fora da combinação; o motivo " +
                "de cada um está nas evidências.");
        return notes;
    }

    private static string LabelOf(CrossSourceBindingFacts b, IReadOnlyDictionary<Guid, CrossSourceConnectorFacts> connectors) =>
        !string.IsNullOrWhiteSpace(b.SourceLabel) ? b.SourceLabel!
        : connectors.TryGetValue(b.ConnectorId, out var c) ? c.Label
        : "Fonte integrada";

    /// <summary>Data em UTC para textos (os campos de data dos contratos seguem ISO 8601).</summary>
    public static string Utc(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>"em 12/09/2026 10:00 UTC" ou, com mais de uma aquisição, "em 2 aquisições, de … a …" (datas em ordem).</summary>
    public static string DateSpan(IReadOnlyList<DateTimeOffset> ordered) => ordered.Count == 1
        ? $"em {Utc(ordered[0])}"
        : $"em {ordered.Count} aquisições, de {Utc(ordered[0])} a {Utc(ordered[^1])}";
}

/// <summary>
/// Autoridade ÚNICA da linguagem das situações entre fontes — o detalhe do ativo e a Central dizem a mesma coisa.
/// Determinística: nenhum texto depende de IA.
/// </summary>
public static class CrossSourceNarrative
{
    public const string Heading = "Situações identificadas entre fontes";

    public const string Scope =
        "Combinam fatos de fontes diferentes sobre o MESMO dispositivo, vinculados por chave forte, segundo regras " +
        "explícitas e versionadas. Afirmam a coexistência de condições informadas pelas fontes — não são incidentes " +
        "confirmados, risco calculado, prioridade de risco nem exposição à internet, e não alteram scores nem filas.";

    /// <summary>O REQUISITO da associação — explicado como critério, nunca como afirmação sobre um ativo.</summary>
    public const string AssociationCriterion =
        "Critério de associação: os registros do Microsoft Defender e do Microsoft Intune precisam informar o mesmo " +
        "identificador de dispositivo do Microsoft Entra, no mesmo diretório e no mesmo tenant, e estar no mesmo ativo " +
        "canônico. Nome, endereço ou semelhança não participam da associação.";

    /// <summary>Afirmação usada SÓ quando a avaliação comprovou a associação (<see cref="CrossSourceAssociationStates.Proven"/>).</summary>
    public const string AssociationProven =
        "Associação comprovada: os registros elegíveis do Microsoft Defender e do Microsoft Intune informaram o mesmo " +
        "identificador de dispositivo do Microsoft Entra, no mesmo diretório e no mesmo tenant, e estão no mesmo ativo " +
        "canônico.";

    public static string AssociationLabel(string state) => state switch
    {
        CrossSourceAssociationStates.Proven => "Associação comprovada",
        CrossSourceAssociationStates.Conflict => "Vínculo em conflito — associação não resolvida",
        CrossSourceAssociationStates.SourceMissing => "Associação não avaliada — falta registro de uma das fontes",
        _ => "Associação não comprovada",
    };

    public static string PolicyDescription(CrossSourcePolicy p) =>
        $"Política operacional do AEGIS (configurável), não exigência normativa nem garantia de segurança: evidência obtida " +
        $"há até {p.MaxEvidenceAgeDays} dia(s); registro cuja última atividade informada pela fonte ultrapassa " +
        $"{p.MaxSourceActivityAgeDays} dia(s) não é tratado como atualmente observado; defasagem acima de " +
        $"{p.AcquisitionGapCaveatHours} h entre as fontes gera ressalva.";

    public static string StateLabel(string state, bool hasCaveats = false) => state switch
    {
        CrossSourceStates.Identified => hasCaveats ? "Situação identificada, com ressalvas" : "Situação identificada",
        CrossSourceStates.NotIdentified => hasCaveats ? "Combinação não identificada, com ressalvas" : "Combinação não identificada",
        CrossSourceStates.InsufficientEvidence => "Evidência insuficiente para verificar",
        CrossSourceStates.LinkConflict => "Vínculo em conflito — combinação indisponível",
        CrossSourceStates.ContradictoryEvidence => "Registros contraditórios — combinação indisponível",
        _ => "Não avaliado",
    };

    public static string RoleLabel(CrossSourceRole role) => role == CrossSourceRole.Vulnerabilities
        ? "Vulnerabilidades reportadas"
        : "Conformidade e criptografia informadas";

    public static string EligibilityLabel(string eligibility) => eligibility switch
    {
        CrossSourceEligibility.Eligible => "Elegível",
        CrossSourceEligibility.NotObserved => "Fora: não mais observado",
        CrossSourceEligibility.Conflict => "Fora: vínculo em conflito",
        CrossSourceEligibility.NotLinked => "Fora: vínculo não comprovado",
        CrossSourceEligibility.KeyMismatch => "Fora: outra chave forte",
        CrossSourceEligibility.OutOfPolicy => "Fora: política temporal",
        CrossSourceEligibility.InactiveInSource => "Fora: sem atividade recente na fonte",
        CrossSourceEligibility.AmbiguousAttribution => "Fora: atribuição ambígua",
        _ => "Fora da combinação",
    };

    public static string AcquisitionLabel(string state) => state switch
    {
        CrossSourceAcquisitionStates.Current => "Aquisição mais recente (completa)",
        CrossSourceAcquisitionStates.CurrentPartial => "Aquisição mais recente (parcial)",
        CrossSourceAcquisitionStates.CurrentUnconcluded => "Aquisição mais recente (publicação não concluída)",
        CrossSourceAcquisitionStates.CurrentNotRecorded => "Aquisição mais recente (desfecho não registrado)",
        CrossSourceAcquisitionStates.Previous => "Aquisição anterior, não reconfirmada pela mais recente",
        _ => "Aquisição sem marca registrada",
    };

    /// <summary>"O que foi identificado?" — ou, fora de "identificada", o que se pode afirmar e por quê.</summary>
    public static string Summary(CrossSourceRuleAssessment r, int? openCves)
    {
        var device = r.Rule.Condition == CrossSourceDeviceCondition.Noncompliant
            ? "informa o dispositivo como não conforme"
            : "informa o dispositivo como sem criptografia";
        return r.State switch
        {
            CrossSourceStates.Identified =>
                $"No mesmo dispositivo, o Microsoft Defender reporta {openCves?.ToString(CultureInfo.InvariantCulture) ?? "ao menos uma"} " +
                $"vulnerabilidade(s) em aberto e o Microsoft Intune {device}.",
            CrossSourceStates.NotIdentified =>
                "A combinação das duas condições não foi identificada nas evidências elegíveis. " + string.Join(" ", r.Reasons),
            CrossSourceStates.InsufficientEvidence => "Evidência insuficiente para verificar a regra. " + string.Join(" ", r.Reasons),
            CrossSourceStates.LinkConflict =>
                "Vínculo em conflito: a combinação não foi avaliada, porque a associação dos registros não está resolvida. " +
                string.Join(" ", r.Reasons),
            CrossSourceStates.ContradictoryEvidence => "Combinação indisponível: " + string.Join(" ", r.Reasons),
            _ => "Não avaliado: " + string.Join("; ", r.Reasons) + ".",
        };
    }

    /// <summary>"Quais dados sustentam isso?" — com a data PRÓPRIA de cada evidência.</summary>
    public static string SupportingData(CrossSourceRuleAssessment r, CrossSourceAssetAssessment a, int? openCves)
    {
        string DeviceFact(CrossSourceRecordAssessment d) =>
            $"{d.Connector.Label}: “{(r.Rule.Condition == CrossSourceDeviceCondition.Noncompliant
                ? AssetCrossSourceNarrative.ComplianceLabel(d.Binding.Compliance)
                : AssetCrossSourceNarrative.EncryptionLabel(d.Binding.Encryption))}”, na aquisição de " +
            $"{CrossSourceCorrelationEvaluator.Utc(d.Binding.LastObservedAt)} ({AcquisitionLabel(d.AcquisitionState).ToLowerInvariant()})" +
            (d.Binding.SourceLastSeenAt is { } s ? $", última atividade informada pela fonte: {CrossSourceCorrelationEvaluator.Utc(s)}." : ".");

        if (r.State == CrossSourceStates.NotIdentified)
        {
            var negative = new List<string> { a.Association.Text };
            negative.AddRange(r.SupportingDeviceRecords.Select(DeviceFact));
            if (r.VulnerabilityAbsenceSupports)
                foreach (var v in a.Vulnerabilities)
                    negative.Add($"{v.Connector.Label}: nenhuma vulnerabilidade em aberto para este dispositivo na aquisição " +
                        $"completa {CrossSourceCorrelationEvaluator.DateSpan(r.VulnerabilityAcquisitions)}.");
            negative.Add("Estas evidências sustentam apenas que a combinação não se formou; cada condição isolada continua " +
                "nas telas de cada fonte.");
            return string.Join(" ", negative);
        }
        if (r.State != CrossSourceStates.Identified)
            return "As evidências de cada fonte e o motivo de cada exclusão estão listados abaixo; nenhuma conclusão combinada " +
                "foi tirada delas.";
        var parts = new List<string> { a.Association.Text };
        foreach (var v in a.Vulnerabilities.Where(v => v.OpenEligible > 0))
            parts.Add($"{v.Connector.Label}: {v.OpenEligible} observação(ões) de CVE em aberto, das aquisições de " +
                string.Join(", ", v.EligibleMarkers.Select(CrossSourceCorrelationEvaluator.Utc)) + ".");
        parts.AddRange(r.SupportingDeviceRecords.Select(DeviceFact));
        if (openCves is { } n) parts.Add($"CVEs distintas em aberto que sustentam a situação: {n}.");
        return string.Join(" ", parts);
    }

    /// <summary>"O que deve ser verificado ou tratado?"</summary>
    public static string WhatToVerify(CrossSourceRuleAssessment r) => r.State switch
    {
        CrossSourceStates.Identified => r.Rule.WhatToVerify,
        CrossSourceStates.LinkConflict =>
            "Analise o conflito de vínculo no registro da fonte (inventário de ativos). A combinação só volta a ser avaliada " +
            "quando a associação estiver resolvida por uma nova coleta.",
        CrossSourceStates.ContradictoryEvidence =>
            "Verifique no Microsoft Intune por que há registros divergentes para o mesmo dispositivo (ex.: registro " +
            "duplicado). O AEGIS não escolhe um deles.",
        CrossSourceStates.InsufficientEvidence =>
            "Verifique a coleta das fontes em Integrações e o vínculo do dispositivo; a regra é reavaliada a cada leitura.",
        CrossSourceStates.NotIdentified =>
            "Nada a tratar por esta regra agora: a combinação não se formou. Cada condição isolada (vulnerabilidades ou " +
            "condição do dispositivo) continua nas telas de cada fonte" + (r.Caveats.Count > 0 ? "; considere as ressalvas acima." : "."),
        _ => "Nada a avaliar por esta regra neste ativo.",
    };
}

// ---- Contratos de leitura --------------------------------------------------------------------------------------------

public sealed record CrossSourcePolicyDto(
    int MaxEvidenceAgeDays, int MaxSourceActivityAgeDays, int AcquisitionGapCaveatHours, string Description)
{
    public static CrossSourcePolicyDto From(CrossSourcePolicy p) =>
        new(p.MaxEvidenceAgeDays, p.MaxSourceActivityAgeDays, p.AcquisitionGapCaveatHours, CrossSourceNarrative.PolicyDescription(p));
}

/// <summary>UM registro de fonte do ativo como evidência (sem identificadores técnicos).</summary>
public sealed record CrossSourceEvidenceRecordDto(
    string Source,
    string ConnectorName,
    string Role,
    string RoleLabel,
    bool Eligible,
    string Eligibility,
    string EligibilityLabel,
    string? ExclusionReason,
    bool IsActive,
    DateTimeOffset FirstObservedAt,
    /// <summary>Marca da aquisição do AEGIS que observou o registro por último — a data PRÓPRIA desta evidência.</summary>
    DateTimeOffset AcquiredAt,
    /// <summary>Última atividade informada pela fonte (conceito distinto da aquisição e da última tentativa do conector).</summary>
    DateTimeOffset? SourceActivityAt,
    DateTimeOffset? NoLongerObservedSince,
    DateTimeOffset? LinkedAt,
    string AcquisitionState,
    string AcquisitionLabel,
    string ResolutionLabel,
    string? ComplianceLabel,
    string? EncryptionLabel,
    bool LatestAttemptFailed);

public sealed record CrossSourceRuleResultDto(
    string RuleCode,
    int RuleVersion,
    string Title,
    string State,
    string StateLabel,
    bool HasCaveats,
    /// <summary>O que foi identificado?</summary>
    string Summary,
    /// <summary>Quais dados sustentam isso?</summary>
    string SupportingData,
    /// <summary>O que deve ser verificado ou tratado?</summary>
    string WhatToVerify,
    string Limitation,
    IReadOnlyList<string> Criteria,
    IReadOnlyList<CrossSourceNoteDto> Caveats,
    IReadOnlyList<string> Reasons,
    /// <summary>CVEs DISTINTAS em aberto e elegíveis (unidade: CVEs); nulo quando não há evidência de vulnerabilidade usável.</summary>
    int? OpenCveCount);

public sealed record CrossSourceCveSourceDto(
    string Source, string ConnectorName, DateTimeOffset FirstSeenAt, DateTimeOffset AcquiredAt, string AcquisitionState, string AcquisitionLabel);

/// <summary>UMA CVE (deduplicada por ativo/CVE), com a proveniência de cada fonte que a reporta em aberto.</summary>
public sealed record CrossSourceCveDto(
    string CveId, string? Title, string? Severity, double? CvssScore, IReadOnlyList<CrossSourceCveSourceDto> Sources);

public sealed record CrossSourceCvePageDto(
    /// <summary>Total de CVEs distintas elegíveis (unidade: CVEs), não o tamanho da página.</summary>
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<CrossSourceCveDto> Items,
    /// <summary>Observações em aberto fora da política temporal (não consideradas).</summary>
    int ExcludedOutOfPolicy,
    /// <summary>Observações não mais reportadas pela fonte (não sustentam condição aberta).</summary>
    int NoLongerReported);

/// <summary>Situações entre fontes de UM ativo: regras, evidências de cada fonte, CVEs paginadas e limitações.</summary>
public sealed record AssetCrossSourceDto(
    Guid AssetId,
    string AssetName,
    bool NameIsPlaceholder,
    /// <summary>Instante em que a correlação foi CALCULADA (relógio injetável) — distinto das datas das evidências.</summary>
    DateTimeOffset EvaluatedAt,
    string Heading,
    string Scope,
    /// <summary>O requisito da associação (critério), igual para todo ativo.</summary>
    string AssociationCriterion,
    /// <summary>A conclusão da avaliação sobre a associação NESTE ativo — nunca presumida.</summary>
    CrossSourceAssociationDto Association,
    CrossSourcePolicyDto Policy,
    IReadOnlyList<CrossSourceRuleResultDto> Rules,
    IReadOnlyList<CrossSourceEvidenceRecordDto> Evidence,
    CrossSourceCvePageDto? Cves);

public sealed record CrossSourceAssociationDto(string State, string Label, string Text)
{
    public static CrossSourceAssociationDto From(CrossSourceAssociation a) =>
        new(a.State, CrossSourceNarrative.AssociationLabel(a.State), a.Text);
}

/// <summary>Contagem de ATIVOS por estado, para UMA regra (unidade: ativos).</summary>
public sealed record CrossSourceRuleTallyDto(
    string RuleCode, int RuleVersion, string Title,
    int Identified, int IdentifiedWithCaveats, int NotIdentified, int NotIdentifiedWithCaveats, int InsufficientEvidence,
    int LinkConflict, int ContradictoryEvidence);

/// <summary>
/// Aquisições de UMA fonte que participam de um resultado: intervalo (primeira e última marca) e quantas aquisições
/// distintas — nunca reduzido a uma única data.
/// </summary>
public sealed record CrossSourceAcquisitionSpanDto(DateTimeOffset First, DateTimeOffset Last, int Acquisitions)
{
    public static CrossSourceAcquisitionSpanDto? From(IReadOnlyList<DateTimeOffset> ordered) =>
        ordered.Count == 0 ? null : new(ordered[0], ordered[^1], ordered.Count);
}

public static class CrossSourceReadingStates
{
    /// <summary>Falta a fonte de vulnerabilidades (Defender) ou a de gestão de dispositivos (Intune).</summary>
    public const string NoSource = "NoSource";

    /// <summary>As duas fontes existem, mas ao menos uma ainda não publicou aquisição por dispositivo.</summary>
    public const string NeverCollected = "NeverCollected";

    public const string Available = "Available";
}

public sealed record CrossSourceSituationSummaryDto(
    string ReadingState,
    string? ReadingNote,
    /// <summary>Ativos com registros das duas fontes (unidade: ativos) — a população avaliável.</summary>
    int AssetsWithBothSources,
    /// <summary>Ativos efetivamente avaliados nesta leitura (≤ teto).</summary>
    int AssetsEvaluated,
    /// <summary>A população passou do teto: os totais abaixo são PARCIAIS e dizem isso.</summary>
    bool EvaluationTruncated,
    /// <summary>Situações identificadas (unidade: ativo × regra).</summary>
    int SituationsIdentified,
    /// <summary>Ativos com ao menos uma situação identificada (unidade: ativos).</summary>
    int AssetsWithSituations,
    IReadOnlyList<CrossSourceRuleTallyDto> ByRule);

public sealed record CrossSourceSituationItemDto(
    Guid AssetId,
    string AssetName,
    bool NameIsPlaceholder,
    string RuleCode,
    int RuleVersion,
    string Title,
    string State,
    string StateLabel,
    bool HasCaveats,
    string Summary,
    /// <summary>CVEs distintas em aberto e elegíveis (unidade: CVEs).</summary>
    int? OpenCveCount,
    /// <summary>Prévia LIMITADA de identificadores de CVE (ordem por identificador) — nunca o total.</summary>
    IReadOnlyList<string> CvePreview,
    bool CvePreviewTruncated,
    IReadOnlyList<CrossSourceNoteDto> Caveats,
    /// <summary>O que representam as aquisições abaixo (<see cref="CrossSourceEvidenceBasis"/>).</summary>
    string EvidenceBasis,
    /// <summary>Aquisições do Defender que participam (intervalo e quantidade); nulo quando nenhuma participa.</summary>
    CrossSourceAcquisitionSpanDto? VulnerabilityAcquisitions,
    /// <summary>Aquisições do Intune que participam (intervalo e quantidade); nulo quando nenhuma participa.</summary>
    CrossSourceAcquisitionSpanDto? DeviceManagementAcquisitions);

public sealed record CrossSourceSituationListDto(
    DateTimeOffset EvaluatedAt,
    string Heading,
    string Scope,
    CrossSourcePolicyDto Policy,
    CrossSourceSituationSummaryDto Summary,
    string StateFilter,
    string? RuleFilter,
    IReadOnlyList<CrossSourceSituationItemDto> Items,
    /// <summary>Total FILTRADO (unidade: situações ativo × regra), para paginação.</summary>
    int Total,
    int Page,
    int PageSize);

public sealed record CrossSourceSituationFilter(string? State = null, string? RuleCode = null, int Page = 1, int PageSize = 10);

/// <summary>
/// Leitura tenant-scoped (Global Query Filter fail-closed) das situações entre fontes. Somente leitura: nunca coleta,
/// nunca escreve, nunca aciona IA, nunca altera score ou fila. Os fatos são lidos numa leitura coerente.
/// </summary>
public interface ICrossSourceCorrelationQuery
{
    /// <summary>Situações de UM ativo; <c>null</c> quando o ativo não existe no tenant ambiente.</summary>
    Task<AssetCrossSourceDto?> GetForAssetAsync(Guid assetId, int cvePage, int cvePageSize, CancellationToken ct = default);

    /// <summary>Resumo por regra × estado e a lista agrupada por ativo × regra (paginada).</summary>
    Task<CrossSourceSituationListDto> ListAsync(CrossSourceSituationFilter filter, CancellationToken ct = default);
}
