using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Application.Identity.Adm;

/// <summary>
/// [AEGIS-ADM-01] Leitura do envelope de fatos agregados — AUTORIDADE ÚNICA, compartilhada pelo snapshot da
/// Evidence Fabric e pela aquisição do ADM.
///
/// Os dois guardam o MESMO envelope versionado, e por isso precisam lê-lo com as MESMAS opções: duas
/// desserializações independentes divergiriam no primeiro ajuste de nomenclatura, e a divergência apareceria
/// como "fato ausente" — que na avaliação vira <c>NotEvaluated</c>, ou seja, cobertura perdida sem causa real.
///
/// Compatibilidade preservada: raiz ARRAY é o schema v1 (só observações — e nenhum zero inventado para os
/// agregados que ele não tinha); raiz OBJETO é o v2 em diante. Um JSON ilegível degrada para "sem fatos",
/// nunca para números falsos.
/// </summary>
public static class IdentityEvidenceFactsJson
{
    /// <summary>Versão CORRENTE do envelope de fatos (compartilhada por snapshot e aquisição).</summary>
    public const string CurrentSchemaVersion = "aegis-identity-evidence-v2";

    /// <summary>Schema ANTERIOR — snapshots gravados assim continuam legíveis sem migration nem reescrita.</summary>
    public const string LegacySchemaVersion = "aegis-identity-evidence-v1";

    /// <summary>As MESMAS opções usadas na escrita — enums por nome, camelCase.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IdentityEvidenceFacts Empty =
        new(CurrentSchemaVersion, Array.Empty<KnightObservation>(), null, null);

    /// <summary>Serializa o envelope corrente com as observações em ordem estável (fingerprint reprodutível).</summary>
    public static string Serialize(
        IEnumerable<KnightObservation> observations,
        IdentityRiskPosture? identityRisk,
        IdentityAuthenticationPosture? authenticationPosture) =>
        JsonSerializer.Serialize(
            new IdentityEvidenceFacts(
                CurrentSchemaVersion,
                observations.OrderBy(o => (int)o.Key).ToList(),
                identityRisk,
                authenticationPosture),
            Options);

    /// <summary>Lê o envelope em QUALQUER das versões do schema. Nunca lança; nunca inventa valores.</summary>
    public static IdentityEvidenceFacts Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var legacy = JsonSerializer.Deserialize<List<KnightObservation>>(json, Options)
                    ?? new List<KnightObservation>();
                return new IdentityEvidenceFacts(LegacySchemaVersion, legacy, null, null);
            }

            return JsonSerializer.Deserialize<IdentityEvidenceFacts>(json, Options) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}

/// <summary>
/// [AEGIS-ADM-01] FRONTEIRA DE COMPATIBILIDADE entre o ADM e o avaliador do AEGIS KNIGHT.
///
/// Existe para manter uma direção de dependência que importa: o ADM não conhece o catálogo de indicadores,
/// não conhece IDs "AK-ENTRA" e não conhece a fórmula de score. O avaliador, por sua vez, continua recebendo
/// exatamente o contrato que já entendia — nenhuma regra dele muda.
///
/// A tradução é DETERMINÍSTICA e TOTAL nos dois sentidos: cada conjunto do ADM tem um sinal do KNIGHT e
/// vice-versa, e cada tipo de objeto tem correspondência exata. Um valor sem correspondência é tratado
/// explicitamente (<c>null</c>/<c>Unknown</c>) em vez de cair num "default" que inventaria semântica.
/// </summary>
public static class IdentityKnightBoundary
{
    /// <summary>
    /// Versão da NORMALIZAÇÃO aplicada: a tradução coleta → observações tipadas + conjuntos do ADM. Muda
    /// quando a semântica da tradução muda — é o que permite distinguir "o diretório mudou" de "nós passamos
    /// a interpretar o diretório de outro jeito".
    /// </summary>
    public const string NormalizationVersion = "aegis-adm-identity-normalization-v1";

    /// <summary>Versão do contrato canônico do ADM de identidade.</summary>
    public const string SchemaVersion = "aegis-adm-identity-v1";

    // ---- Conjunto do ADM ↔ sinal do KNIGHT -----------------------------------------------------------

    private static readonly IReadOnlyDictionary<IdentityObservationSet, KnightSignalKey> SignalBySet =
        new Dictionary<IdentityObservationSet, KnightSignalKey>
        {
            [IdentityObservationSet.PrivilegedRoleMember] = KnightSignalKey.PrivilegedAccountsTotal,
            [IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability] = KnightSignalKey.PrivilegedAccountsWithoutMfa,
            [IdentityObservationSet.InactiveGuest] = KnightSignalKey.InactiveGuestAccounts,
        };

    private static readonly IReadOnlyDictionary<KnightSignalKey, IdentityObservationSet> SetBySignal =
        SignalBySet.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Capacidade da coleta cujo desfecho determina o estado do conjunto (independente das demais).</summary>
    private static readonly IReadOnlyDictionary<IdentityObservationSet, KnightCapability> CapabilityBySet =
        new Dictionary<IdentityObservationSet, KnightCapability>
        {
            [IdentityObservationSet.PrivilegedRoleMember] = KnightCapability.PrivilegedRoleInventory,
            [IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability] = KnightCapability.MfaRegistration,
            [IdentityObservationSet.InactiveGuest] = KnightCapability.GuestAccounts,
        };

    public static KnightSignalKey SignalFor(IdentityObservationSet set) => SignalBySet[set];

    public static IdentityObservationSet? SetFor(KnightSignalKey signal) =>
        SetBySignal.TryGetValue(signal, out var set) ? set : null;

    public static KnightCapability CapabilityFor(IdentityObservationSet set) => CapabilityBySet[set];

    // ---- Tipo de objeto ------------------------------------------------------------------------------

    /// <summary>Tipo do KNIGHT → tipo do ADM. Total: um membro novo do enum de origem falha em compilação.</summary>
    public static IdentityEntityKind ToIdentityKind(KnightAffectedObjectKind kind) => kind switch
    {
        KnightAffectedObjectKind.User => IdentityEntityKind.User,
        KnightAffectedObjectKind.Guest => IdentityEntityKind.Guest,
        KnightAffectedObjectKind.ServicePrincipal => IdentityEntityKind.ServicePrincipal,
        KnightAffectedObjectKind.Group => IdentityEntityKind.Group,
        KnightAffectedObjectKind.Device => IdentityEntityKind.Device,
        KnightAffectedObjectKind.Unknown => IdentityEntityKind.Unknown,
        _ => IdentityEntityKind.Unknown,
    };

    /// <summary>Tipo do ADM → tipo da superfície histórica de afetados do KNIGHT.</summary>
    public static KnightAffectedObjectKind ToKnightKind(IdentityEntityKind kind) => kind switch
    {
        IdentityEntityKind.User => KnightAffectedObjectKind.User,
        IdentityEntityKind.Guest => KnightAffectedObjectKind.Guest,
        IdentityEntityKind.ServicePrincipal => KnightAffectedObjectKind.ServicePrincipal,
        IdentityEntityKind.Group => KnightAffectedObjectKind.Group,
        IdentityEntityKind.Device => KnightAffectedObjectKind.Device,
        IdentityEntityKind.Unknown => KnightAffectedObjectKind.Unknown,
        _ => KnightAffectedObjectKind.Unknown,
    };

    // ---- Desfecho da capacidade → desfecho do conjunto -----------------------------------------------

    /// <summary>
    /// Desfecho da capacidade traduzido para o conjunto. As falhas NÃO colapsam em "indisponível": permissão
    /// ausente, licença insuficiente e throttling contam histórias diferentes para quem vai agir, e o produto
    /// já as distingue em toda a superfície de cobertura.
    /// </summary>
    public static IdentityObservationSetOutcome ToSetOutcome(KnightCapabilityOutcome outcome) => outcome switch
    {
        KnightCapabilityOutcome.Collected => IdentityObservationSetOutcome.Collected,
        KnightCapabilityOutcome.InsufficientPermission => IdentityObservationSetOutcome.InsufficientPermission,
        KnightCapabilityOutcome.LimitedByLicense => IdentityObservationSetOutcome.InsufficientPermission,
        KnightCapabilityOutcome.Throttled => IdentityObservationSetOutcome.Unavailable,
        KnightCapabilityOutcome.AuthenticationFailure => IdentityObservationSetOutcome.Unavailable,
        KnightCapabilityOutcome.Unavailable => IdentityObservationSetOutcome.Unavailable,
        KnightCapabilityOutcome.NotAttempted => IdentityObservationSetOutcome.NotAttempted,
        KnightCapabilityOutcome.Error => IdentityObservationSetOutcome.Error,
        _ => IdentityObservationSetOutcome.Error,
    };

    // ---- Coleta → aquisição canônica -----------------------------------------------------------------

    /// <summary>
    /// Traduz o resultado de UMA coleta na aquisição canônica a persistir. Os fatos agregados atravessam
    /// INTEIROS (inclusive os agregados de risco e de métodos de autenticação): mudar o caminho de leitura não
    /// pode reduzir a cobertura que já existia.
    ///
    /// O horário OBSERVADO só é preenchido quando a coleta soube dizê-lo. Aqui a fonte informa apenas o
    /// instante da coleta, então <c>ObservedAt</c> fica <c>null</c> — inventá-lo a partir de
    /// <c>CollectedAt</c> seria atribuir ao fornecedor uma informação que ele não deu.
    /// </summary>
    public static IdentityAcquisitionRequest ToAcquisition(
        Guid acquisitionId,
        IdentityAcquisitionOrigin origin,
        KnightCollectionResult result,
        DateTimeOffset acquiredAt)
    {
        var factsJson = IdentityEvidenceFactsJson.Serialize(
            result.Facts.All, result.IdentityRisk, result.AuthenticationPosture);
        var capsJson = JsonSerializer.Serialize(result.Capabilities, IdentityEvidenceFactsJson.Options);

        var evidenceBySignal = result.AffectedObjectSets
            .Where(e => SetBySignal.ContainsKey(e.Signal))
            .GroupBy(e => e.Signal)
            .ToDictionary(g => g.Key, g => g.Last());

        var outcomeByCapability = result.Capabilities
            .GroupBy(c => c.Capability)
            .ToDictionary(g => g.Key, g => g.Last());

        var sets = new List<IdentityObservedSet>();
        foreach (var set in IdentityObservationSetCatalog.All)
        {
            var signal = SignalBySet[set];
            var capability = CapabilityBySet[set];

            outcomeByCapability.TryGetValue(capability, out var capStatus);
            var outcome = capStatus is null
                ? IdentityObservationSetOutcome.NotAttempted
                : ToSetOutcome(capStatus.Outcome);

            if (outcome != IdentityObservationSetOutcome.Collected)
            {
                // Capacidade que falhou (ou nem foi tentada): o conjunto fica DESCONHECIDO. Nenhum objeto é
                // registrado e nenhuma ausência é afirmada — é a diferença entre "não há convidados
                // sinalizados" e "não conseguimos ler os convidados".
                sets.Add(IdentityObservedSet.NotCollected(set, outcome, capStatus?.Detail));
                continue;
            }

            var observation = result.Facts.Get(signal);
            evidenceBySignal.TryGetValue(signal, out var evidence);

            if (!observation.IsCollected && evidence is null)
            {
                // A capacidade respondeu, mas ESTE sinal não foi apurado (ex.: privilegiados ausentes do
                // relatório de registro de MFA). O conjunto permanece indeterminado, com o motivo declarado.
                sets.Add(IdentityObservedSet.NotCollected(
                    set, IdentityObservationSetOutcome.Partial, observation.MissingReason));
                continue;
            }

            var objects = (evidence?.Objects ?? Array.Empty<KnightAffectedObjectFact>())
                .Where(o => !string.IsNullOrWhiteSpace(o.ExternalId))
                .Select(o => new IdentityObservedObject(
                    o.ExternalId, ToIdentityKind(o.Kind), o.DisplayName, o.UserPrincipalName, o.Roles, o.Detail))
                .ToList();

            // A contagem APURADA é a da observação tipada — a MESMA que o avaliador lê. Sem ela, a lista
            // preservada é o único número disponível, e o conjunto se declara incompleto por isso.
            var observedCount = observation.IsCollected && observation.Count is { } c && c >= 0
                ? (int)Math.Min(c, int.MaxValue)
                : objects.Count;

            var limitation = evidence?.Limitation
                ?? (evidence is null
                    ? "A coleta apurou a contagem, mas não preservou os objetos deste conjunto."
                    : null);

            // Completude nunca é DEDUZIDA do tamanho da lista: ela exige que a coleta a tenha declarado E que
            // a lista tenha exatamente o tamanho da contagem. Divergência vira "Partial" visível, não silêncio.
            var isComplete = (evidence?.IsComplete ?? false) && objects.Count == observedCount;

            sets.Add(new IdentityObservedSet(
                set,
                isComplete ? IdentityObservationSetOutcome.Collected : IdentityObservationSetOutcome.Partial,
                observedCount,
                objects,
                isComplete,
                limitation));
        }

        return new IdentityAcquisitionRequest(
            acquisitionId,
            origin,
            SchemaVersion,
            NormalizationVersion,
            acquiredAt,
            ObservedAt: null,
            result.State,
            result.Detail,
            factsJson,
            capsJson,
            sets);
    }

    // ---- Aquisição persistida → contrato do avaliador -------------------------------------------------

    /// <summary>
    /// Reconstrói o contrato que o avaliador do AEGIS KNIGHT já entende A PARTIR DA AQUISIÇÃO PERSISTIDA. É
    /// isto que faz a avaliação consumir a evidência gravada em vez do resultado transitório da coleta —
    /// sem alterar uma única regra do avaliador.
    ///
    /// Os objetos vêm com os atributos OBSERVADOS naquela aquisição, não com o cadastro atual: reprojetar uma
    /// aquisição antiga não pode apresentar o presente como prova do passado.
    ///
    /// [AEGIS-ADM-02] E quando o DETALHE expirou por retenção, a lista vazia é declarada como tal: o conjunto
    /// deixa de se apresentar completo e a limitação diz o que aconteceu. Sem isso, "as observações foram
    /// removidas por prazo" e "a coleta não encontrou ninguém" chegariam ao avaliador com a mesma cara — e a
    /// segunda é uma afirmação sobre o ambiente do cliente que ninguém fez. As CONTAGENS não mudam: elas vivem
    /// no envelope de fatos, que a retenção preserva, e por isso o veredito reprojetado continua o mesmo.
    /// </summary>
    public static KnightCollectionResult ToCollectionResult(IdentityAcquisitionRecord record)
    {
        var facts = IdentityEvidenceFactsJson.Deserialize(record.FactsJson);
        var capabilities = KnightCapabilitiesJson.Deserialize(record.CapabilitiesJson);

        var affected = record.Sets
            .Where(s => s.Outcome is IdentityObservationSetOutcome.Collected or IdentityObservationSetOutcome.Partial)
            .Select(s =>
            {
                // Só perde detalhe quem TINHA detalhe. Um conjunto que preservou zero objetos continua
                // devolvendo zero objetos pelo mesmo motivo de sempre — e marcá-lo incompleto por causa de uma
                // retenção que não lhe tirou nada seria inventar uma limitação.
                var perdeuDetalhe = record.DetailRetiredAt is not null && s.PreservedCount > 0;

                return new KnightAffectedObjectEvidence(
                    SignalBySet[s.Set],
                    s.Objects
                        .Select(o => new KnightAffectedObjectFact(
                            o.ExternalId, ToKnightKind(o.Kind), o.DisplayNameObserved, o.UserPrincipalNameObserved,
                            o.RolesObserved, o.Detail))
                        .ToList(),
                    s.IsComplete && !perdeuDetalhe,
                    perdeuDetalhe ? DetalheExpirado(record.DetailRetiredAt!.Value, s) : s.Limitation);
            })
            .ToList();

        return new KnightCollectionResult(
            record.Origin.Provider,
            record.State,
            record.Origin.SourceLabel,
            new KnightFactSet(facts.Observations),
            capabilities,
            record.AcquiredAt,
            record.Detail,
            facts.IdentityRisk,
            facts.AuthenticationPosture,
            affected);
    }

    /// <summary>
    /// [AEGIS-ADM-02] O texto que impede uma lista vazia de passar por ausência. Diz as TRÊS coisas que
    /// precisam continuar distinguíveis: quanto a coleta apurou, quanto ela preservou e que o detalhe de hoje
    /// já não é o de então. A limitação original é preservada à frente — ela continua verdadeira.
    /// </summary>
    private static string DetalheExpirado(DateTimeOffset retiredAt, IdentityObservedSetRecord set)
    {
        var expirado =
            $"Detalhe expirado por retenção operacional em {retiredAt.ToUniversalTime():yyyy-MM-dd}: a coleta "
            + $"apurou {set.ObservedCount} objeto(s) e preservou {set.PreservedCount}, que não estão mais "
            + "disponíveis. A contagem e a completude originais permanecem registradas; a ausência de lista "
            + "aqui NÃO significa que o conjunto estivesse vazio.";

        return string.IsNullOrWhiteSpace(set.Limitation) ? expirado : $"{set.Limitation} {expirado}";
    }
}
