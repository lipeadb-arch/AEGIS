using AegisScore.Domain;

namespace AegisScore.Application.Assessment;

/// <summary>Natureza da prova que uma linha de <c>evidence_requirements</c> exige.</summary>
public enum EvidenceNature
{
    /// <summary>Sinal de ferramenta ("Entra ID: logs de sign-in…") — prova técnica, coletável por conector.</summary>
    Telemetry = 0,

    /// <summary>Auditoria manual / prova documental — nenhuma ferramenta do stack a coleta sozinha.</summary>
    Documentation = 1,
}

/// <summary>
/// Motor PURO de distinção entre lacuna de telemetria e lacuna de documentação — a "lógica de distinção"
/// do Aegis, em um lugar só. Estático e sem dependências (nem EF, nem LLM, nem HTTP): recebe o que a
/// regra EXIGE e o que o tenant TEM, devolve as lacunas compiladas. Todo motor que precise da distinção
/// (StubLlmClient, avaliador de telemetria, futuro resolver tenant-aware) chama AQUI — a regra de
/// classificação não pode ter duas implementações capazes de divergir.
///
/// ⚠️ A natureza da evidência hoje é um campo TIPADO e PERSISTIDO (<c>AegisAssessmentRule.EvidenceType</c>);
/// esta classificação por texto é a ÚNICA autoridade que a DERIVA no seed (e confere no guard), a partir do
/// vocabulário bimodal de <c>evidence_requirements</c>. Distribuição real das 99 regras:
///   • <c>MANUAL_AUDIT_REQUIRED</c> — 41 regras. Prova documental/manual.
///   • <c>"&lt;Ferramenta&gt;: &lt;o que coletar&gt;"</c> — 58 regras. Prova por telemetria.
///   • híbrida (as duas) — 0 regras hoje.
/// A produção lê o tipo PERSISTIDO; a inferência por string fica no seed/guard e nos shims de teste.
/// </summary>
/// <summary>
/// O que o tenant REALMENTE tem para provar um controle, no instante da avaliação.
///
/// Modela DATA e não booleano para a telemetria de propósito: "tem sinal" e "tem sinal RECENTE" são
/// perguntas diferentes, e um conector que morreu há uma semana continua deixando linha no banco. Sem a
/// data, um sensor parado passaria por cobertura ativa — o pior falso negativo possível num painel de
/// postura, porque some da tela justamente quando o problema começou.
/// </summary>
/// <param name="LastTelemetryAt">Instante do sinal técnico mais recente; nulo = nunca houve.</param>
/// <param name="HasVerifiedDocumentaryCoverage">
/// Cobertura documental ACEITA — documento processado pelo RAG e aprovado, não upload feito.
/// </param>
public readonly record struct EvidenceAvailability(
    DateTimeOffset? LastTelemetryAt,
    bool HasVerifiedDocumentaryCoverage);

public static class RuleEvaluator
{
    /// <summary>Token do catálogo que marca "telemetria não prova este controle sozinha".</summary>
    public const string ManualAuditToken = "MANUAL_AUDIT_REQUIRED";

    /// <summary>
    /// Classifica UMA linha de <c>evidence_requirements</c>. O token de auditoria manual é o único
    /// valor estruturado do catálogo; todo o resto é "Ferramenta: descrição" e portanto telemetria.
    /// </summary>
    public static EvidenceNature NatureOf(string requirement) =>
        requirement.Contains(ManualAuditToken, StringComparison.OrdinalIgnoreCase)
            ? EvidenceNature.Documentation
            : EvidenceNature.Telemetry;

    /// <summary>
    /// Extrai o identificador acionável da fonte — o que a tela mostra e o que o operador reconhece:
    /// o prefixo antes do ':' ("Entra ID: logs de sign-in…" → "Entra ID"). Sem ':', devolve a linha
    /// truncada. É o elo que permite ao futuro Auditor de Conectores agrupar lacunas por provedor.
    /// </summary>
    public static string SourceIdentifierOf(string requirement)
    {
        var text = requirement.Trim();
        if (text.Contains(ManualAuditToken, StringComparison.OrdinalIgnoreCase))
            return ManualAuditToken;

        var colon = text.IndexOf(':');
        var head = colon > 0 ? text[..colon].Trim() : text;
        return head.Length <= 60 ? head : head[..60].TrimEnd();
    }

    /// <summary>
    /// Compila as lacunas de um controle não-conforme cruzando o que a regra EXIGE com o que o tenant TEM.
    ///
    /// Emite NO MÁXIMO UM item, e isso é deliberado: as fontes de telemetria de uma regra são
    /// ALTERNATIVAS, não cumulativas (Sentinel <b>ou</b> SecOps <b>ou</b> CrowdStrike provam o mesmo
    /// controle). Emitir uma lacuna por ferramenta citada inflaria a lista em 5× e diria ao operador que
    /// ele precisa dos cinco produtos. A lacuna é uma: "nenhuma fonte aceita está integrada".
    /// </summary>
    /// <param name="evidenceRequirements">O <c>evidence_requirements</c> da regra, como está no catálogo.</param>
    /// <param name="hasTelemetrySignal">O tenant já tem sinal técnico registrado para este controle?</param>
    /// <param name="hasProcessedDocument">O tenant já tem documento processado cobrindo este controle?</param>
    /// <remarks>
    /// Sobrecarga SEM janela de frescor: trata o sinal como recém-chegado. É o caso do motor de ingestão,
    /// onde a telemetria sendo avaliada acabou de chegar por construção — cobrar TTL ali reprovaria o
    /// próprio payload que está sob análise. O TTL pertence à LEITURA (ver a sobrecarga abaixo).
    /// </remarks>
    /// <summary>
    /// [AEGIS-MVP-POSTURE-01] Classificação TIPADA da natureza da evidência de uma regra a partir do
    /// vocabulário bimodal de <c>evidence_requirements</c> — a ÚNICA autoridade de classificação
    /// (telemetria × documental × híbrida). O seed a chama para PERSISTIR <see cref="RuleEvidenceType"/> na
    /// regra; a partir daí a produção lê o tipo persistido em vez de reparsear a string a cada leitura.
    /// </summary>
    public static RuleEvidenceType DeriveEvidenceType(IReadOnlyList<string> evidenceRequirements)
    {
        if (evidenceRequirements is null || evidenceRequirements.Count == 0)
            return RuleEvidenceType.Telemetry;

        var hasManual = evidenceRequirements.Any(e => NatureOf(e) == EvidenceNature.Documentation);
        var hasTool = evidenceRequirements.Any(e => NatureOf(e) == EvidenceNature.Telemetry);
        return (hasManual, hasTool) switch
        {
            (true, true) => RuleEvidenceType.Both,
            (true, false) => RuleEvidenceType.Documentation,
            _ => RuleEvidenceType.Telemetry,
        };
    }

    // ---- Sobrecargas COMPATÍVEIS (transição/teste) — derivam o tipo da string pela ÚNICA autoridade -----
    // A produção deve chamar as sobrecargas TIPADAS (que recebem RuleEvidenceType). Estas existem apenas para
    // testes e para caminhos legados que ainda não têm o tipo persistido à mão, e delegam ao mesmo motor.

    public static IReadOnlyList<MissingRequirement> Compile(
        IReadOnlyList<string> evidenceRequirements,
        bool hasTelemetrySignal,
        bool hasProcessedDocument) =>
        Compile(DeriveEvidenceType(evidenceRequirements), evidenceRequirements, hasTelemetrySignal, hasProcessedDocument);

    public static IReadOnlyList<MissingRequirement> Compile(
        IReadOnlyList<string> evidenceRequirements,
        EvidenceAvailability availability,
        DateTimeOffset now,
        TimeSpan freshnessWindow) =>
        Compile(DeriveEvidenceType(evidenceRequirements), evidenceRequirements, availability, now, freshnessWindow);

    /// <summary>
    /// Compila as lacunas usando o tipo de evidência PERSISTIDO como autoridade de classificação (sem
    /// re-inferir da string). Sem janela de frescor: trata o sinal como recém-chegado (caso da ingestão).
    /// </summary>
    public static IReadOnlyList<MissingRequirement> Compile(
        RuleEvidenceType evidenceType,
        IReadOnlyList<string> evidenceRequirements,
        bool hasTelemetrySignal,
        bool hasProcessedDocument)
    {
        var now = DateTimeOffset.UtcNow;
        return Compile(
            evidenceType,
            evidenceRequirements,
            new EvidenceAvailability(hasTelemetrySignal ? now : null, hasProcessedDocument),
            now,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Compila as lacunas com o tipo PERSISTIDO como autoridade e aplicando a JANELA DE FRESCOR: um sinal
    /// mais velho que <paramref name="freshnessWindow"/> é tratado como ausente — o conector parou de
    /// reportar e o controle voltou a ser um ponto cego, ainda que exista histórico no banco.
    /// </summary>
    /// <param name="evidenceType">Natureza TIPADA da evidência (autoridade única de classificação).</param>
    /// <param name="now">Relógio injetado (testabilidade — nunca <c>DateTimeOffset.UtcNow</c> aqui dentro).</param>
    /// <param name="freshnessWindow">
    /// Idade máxima aceita do sinal. <see cref="Timeout.InfiniteTimeSpan"/> desliga a checagem.
    /// </param>
    public static IReadOnlyList<MissingRequirement> Compile(
        RuleEvidenceType evidenceType,
        IReadOnlyList<string> evidenceRequirements,
        EvidenceAvailability availability,
        DateTimeOffset now,
        TimeSpan freshnessWindow)
    {
        evidenceRequirements ??= Array.Empty<string>();
        if (evidenceRequirements.Count == 0)
            return Array.Empty<MissingRequirement>();

        // Split das linhas por natureza é APENAS para descrição/identificador de fonte — a CLASSIFICAÇÃO
        // (qual lacuna emitir) vem do tipo persistido, não desta partição.
        var telemetrySources = evidenceRequirements
            .Where(r => NatureOf(r) == EvidenceNature.Telemetry).ToList();
        var documentarySources = evidenceRequirements
            .Where(r => NatureOf(r) == EvidenceNature.Documentation).ToList();

        var expectsTelemetry = evidenceType is RuleEvidenceType.Telemetry or RuleEvidenceType.Both;
        var expectsDocumentation = evidenceType is RuleEvidenceType.Documentation or RuleEvidenceType.Both;

        var staleness = StalenessOf(availability.LastTelemetryAt, now, freshnessWindow);
        var telemetryGap = expectsTelemetry && staleness is not TelemetryFreshness.Fresh;
        var documentaryGap = expectsDocumentation && !availability.HasVerifiedDocumentaryCoverage;

        // Identificador de fonte para a descrição — defensivo quando o tipo persistido espera uma natureza
        // cujas linhas não estão presentes (ex.: regra editada no banco): usa um rótulo genérico honesto.
        var telemetryLabel = telemetrySources.Count > 0 ? SourceIdentifierOf(telemetrySources[0]) : "telemetria";
        var documentaryLabel = documentarySources.Count > 0 ? SourceIdentifierOf(documentarySources[0]) : ManualAuditToken;

        // Ambas as naturezas em falta → UMA lacuna de dupla evidência (ver ComplianceRequirementType.Both):
        // fechar metade não é progresso, e marcá-la como dois itens sugeriria que é.
        if (telemetryGap && documentaryGap)
            return new[]
            {
                new MissingRequirement(
                    ComplianceRequirementType.Both,
                    telemetryLabel,
                    $"Faltam as duas provas: telemetria de {telemetryLabel} " +
                    $"e a evidência documental/auditoria manual exigida pelo controle."),
            };

        if (telemetryGap)
            return new[]
            {
                new MissingRequirement(
                    ComplianceRequirementType.Telemetry,
                    telemetryLabel,
                    telemetrySources.Count > 0
                        ? DescribeTelemetryGap(telemetrySources, staleness, availability.LastTelemetryAt, now)
                        : "Sem sinal técnico registrado para este controle — conector não integrado ou sem cobertura."),
            };

        if (documentaryGap)
            return new[]
            {
                new MissingRequirement(
                    ComplianceRequirementType.Documentation,
                    documentaryLabel,
                    "Controle exige auditoria manual ou prova documental: nenhum documento processado no " +
                    "Document Hub cobre esta subcategoria."),
            };

        // A evidência existe — a não-conformidade é de MÉRITO (o controle falhou), não de lacuna de prova.
        return Array.Empty<MissingRequirement>();
    }

    /// <summary>Estado do sinal técnico perante a janela de frescor.</summary>
    private enum TelemetryFreshness { Fresh, Stale, Never }

    private static TelemetryFreshness StalenessOf(DateTimeOffset? lastAt, DateTimeOffset now, TimeSpan window)
    {
        if (lastAt is null) return TelemetryFreshness.Never;
        if (window == Timeout.InfiniteTimeSpan) return TelemetryFreshness.Fresh;
        return now - lastAt.Value > window ? TelemetryFreshness.Stale : TelemetryFreshness.Fresh;
    }

    /// <summary>
    /// Redige a lacuna distinguindo "nunca houve sinal" de "o sinal envelheceu". A diferença é operacional,
    /// não estética: nunca integrado é trabalho de configuração; parou de reportar é INCIDENTE — alguém
    /// revogou a credencial, o agente caiu, a cota estourou. Uma frase só para os dois casos mandaria o
    /// operador reinstalar um conector que precisa apenas ser reautenticado.
    /// </summary>
    private static string DescribeTelemetryGap(
        IReadOnlyList<string> telemetrySources, TelemetryFreshness staleness,
        DateTimeOffset? lastAt, DateTimeOffset now)
    {
        var primary = SourceIdentifierOf(telemetrySources[0]);

        if (staleness == TelemetryFreshness.Stale && lastAt is not null)
        {
            var age = now - lastAt.Value;
            var howLong = age.TotalDays >= 1
                ? $"{(int)age.TotalDays} dia(s)"
                : $"{(int)age.TotalHours} hora(s)";
            return $"Sinal de {primary} OBSOLETO: último dado há {howLong}, fora da janela de frescor. " +
                   $"O conector está integrado mas parou de reportar — verifique credencial, cota e saúde do agente.";
        }

        if (telemetrySources.Count == 1)
            return $"Sem sinal registrado de {primary} para este controle — conector não integrado ou sem cobertura.";

        var alternatives = string.Join(", ", telemetrySources.Skip(1).Take(4).Select(SourceIdentifierOf));
        return $"Sem sinal técnico registrado para este controle. Fonte primária: {primary}. " +
               $"Alternativas aceitas: {alternatives}.";
    }
}
