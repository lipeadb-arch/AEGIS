using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Queries;

// ---- [AEGIS-ENTITY-RESOLUTION-01] Leitura das FONTES de um ativo e do vínculo entre elas ------------------------
// Superfície SOMENTE LEITURA, tenant-scoped (Global Query Filter), na jornada já existente do inventário de ativos —
// não é painel de reconciliação. Uma única autoridade de narrativa (AssetCrossSourceNarrative) decide rótulo e
// explicação, para a lista e o detalhe dizerem a mesma coisa. Identificadores técnicos (id na fonte, identificador
// de diretório) só aparecem no DIAGNÓSTICO, quando pedido por quem pode vê-lo.

/// <summary>Estado do vínculo entre fontes de UM ativo (códigos estáveis; o texto vem da narrativa).</summary>
public static class AssetCrossSourceStates
{
    /// <summary>Registros de duas ou mais fontes associados pelo identificador de dispositivo do diretório.</summary>
    public const string Linked = "linked";

    /// <summary>Uma fonte, com identificador de diretório registrado; nenhuma outra fonte o informou ainda.</summary>
    public const string IdentifierOnly = "identifierOnly";

    /// <summary>Sem identificador válido (ou diretório não confirmado): ainda não vinculável — não é "outro dispositivo".</summary>
    public const string NotLinked = "notLinked";

    /// <summary>Contradição preservada para análise.</summary>
    public const string Conflict = "conflict";

    /// <summary>Registros anteriores à resolução, ainda não reavaliados.</summary>
    public const string NotEvaluated = "notEvaluated";

    /// <summary>Ativo sem registro de fonte integrada (manual/CMDB).</summary>
    public const string NoSource = "noSource";
}

/// <summary>Fatos mínimos de um binding para derivar o estado do ativo.</summary>
public sealed record AssetBindingFacts(Guid ConnectorConfigId, bool IsActive, AssetBindingResolutionState ResolutionState);

/// <summary>Resumo das fontes de um ativo, para a linha da listagem (sem identificadores técnicos).</summary>
public sealed record AssetSourceSummaryDto(
    /// <summary>Fontes (conectores) distintas com registro PRESENTE na última leitura.</summary>
    int ActiveSourceCount,
    IReadOnlyList<string> ActiveSources,
    /// <summary>Total de registros de fonte (presentes e não mais observados).</summary>
    int SourceRecords,
    string CrossSourceState,
    string CrossSourceLabel,
    DateTimeOffset? LastObservedAt);

/// <summary>Detalhe técnico de UM registro — só no diagnóstico. Dado interno de resolução.</summary>
public sealed record AssetSourceDiagnosticsDto(
    string ExternalId,
    string? DirectoryNamespace,
    string? DirectoryDeviceId,
    string? ConflictDirectoryDeviceId,
    DateTimeOffset? LinkedAt,
    DateTimeOffset? ResolutionEvaluatedAt);

/// <summary>UM registro de fonte do ativo, em linguagem executiva.</summary>
public sealed record AssetSourceRecordDto(
    string SourceLabel,
    bool IsActive,
    string PresenceLabel,
    DateTimeOffset FirstObservedAt,
    /// <summary>Última coleta do AEGIS que observou o registro nesta fonte.</summary>
    DateTimeOffset LastObservedAt,
    /// <summary>Última atividade informada PELA FONTE (lastSeen/lastSyncDateTime); nulo quando ausente.</summary>
    DateTimeOffset? SourceLastSeenAt,
    DateTimeOffset? NoLongerObservedSince,
    string? ObservedName,
    string? Platform,
    string ResolutionState,
    string ResolutionLabel,
    string ResolutionExplanation,
    string IdentifierStatus,
    string IdentifierStatusLabel,
    string? ConflictKind,
    /// <summary>Outro ativo envolvido numa contradição (referência de análise), quando houver.</summary>
    Guid? RelatedAssetId,
    string? RelatedAssetName,
    string? SourceCompliance,
    string? SourceComplianceLabel,
    string? SourceEncryption,
    string? SourceEncryptionLabel,
    AssetSourceDiagnosticsDto? Diagnostics);

/// <summary>Fontes de UM ativo e o estado do vínculo entre elas.</summary>
public sealed record AssetSourcesDto(
    Guid AssetId,
    string AssetName,
    bool NameIsPlaceholder,
    string CrossSourceState,
    string CrossSourceLabel,
    string Explanation,
    /// <summary>"Vínculo confirmado" confirma a associação dos registros — não a segurança do dispositivo.</summary>
    string LinkMeaning,
    /// <summary>O identificador do Entra vem das fontes; o cadastro do Entra NÃO foi consultado como fonte.</summary>
    string DirectoryNote,
    string CuratedNote,
    int ActiveSourceCount,
    int SourceRecords,
    bool Truncated,
    bool DiagnosticsIncluded,
    IReadOnlyList<AssetSourceRecordDto> Sources);

public interface IAssetSourceQuery
{
    /// <summary>Resumo por ativo para uma página da listagem (todos os ids pedidos recebem uma entrada).</summary>
    Task<IReadOnlyDictionary<Guid, AssetSourceSummaryDto>> GetSummariesAsync(
        IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default);

    /// <summary>Fontes de UM ativo do tenant ambiente; <c>null</c> quando o ativo não existe (ou é de outro tenant).</summary>
    Task<AssetSourcesDto?> GetSourcesAsync(Guid assetId, bool includeDiagnostics, CancellationToken ct = default);
}

/// <summary>
/// Autoridade ÚNICA do estado e da linguagem do vínculo entre fontes. Distingue falta de evidência, contradição e
/// registro não avaliado — nunca chama toda ausência de "conflito", nunca apresenta vínculo como segurança.
/// </summary>
public static class AssetCrossSourceNarrative
{
    public const string LinkMeaning =
        "“Vínculo confirmado” significa que os registros das fontes se referem ao mesmo dispositivo. Não confirma " +
        "a segurança, a conformidade nem a proteção do dispositivo.";

    public const string DirectoryNote =
        "A associação usa o identificador de dispositivo do Microsoft Entra informado pelas próprias fontes, no " +
        "mesmo diretório. O cadastro do dispositivo no Entra não foi consultado como fonte.";

    public const string CuratedNote =
        "A vinculação não altera nome curado, responsável, criticidade nem relações do ativo. A criticidade exibida " +
        "é a registrada no AEGIS (1 é o valor padrão quando nunca foi declarada) — não é inferida pela ferramenta.";

    /// <summary>Registros considerados: os PRESENTES; sem nenhum presente, os últimos conhecidos.</summary>
    private static List<AssetBindingFacts> Considered(IReadOnlyCollection<AssetBindingFacts> bindings)
    {
        var active = bindings.Where(b => b.IsActive).ToList();
        return active.Count > 0 ? active : bindings.ToList();
    }

    /// <summary>Fontes (conectores) distintas vinculadas pelo identificador de diretório.</summary>
    public static int LinkedSourceCount(IReadOnlyCollection<AssetBindingFacts> bindings) =>
        Considered(bindings)
            .Where(b => b.ResolutionState == AssetBindingResolutionState.Linked)
            .Select(b => b.ConnectorConfigId)
            .Distinct()
            .Count();

    public static string Derive(IReadOnlyCollection<AssetBindingFacts> bindings)
    {
        if (bindings.Count == 0) return AssetCrossSourceStates.NoSource;
        var considered = Considered(bindings);
        if (considered.Any(b => b.ResolutionState == AssetBindingResolutionState.Conflict))
            return AssetCrossSourceStates.Conflict;

        var linkedSources = LinkedSourceCount(bindings);
        if (linkedSources >= 2) return AssetCrossSourceStates.Linked;
        if (considered.All(b => b.ResolutionState == AssetBindingResolutionState.NotEvaluated))
            return AssetCrossSourceStates.NotEvaluated;

        var sources = considered.Select(b => b.ConnectorConfigId).Distinct().Count();
        if (linkedSources == 1 && sources == 1) return AssetCrossSourceStates.IdentifierOnly;
        return AssetCrossSourceStates.NotLinked;
    }

    public static string Label(string state) => state switch
    {
        AssetCrossSourceStates.Linked => "Vínculo confirmado entre fontes",
        AssetCrossSourceStates.IdentifierOnly => "Uma fonte · identificador de diretório registrado",
        AssetCrossSourceStates.NotLinked => "Ainda sem vínculo entre fontes",
        AssetCrossSourceStates.Conflict => "Conflito de vínculo preservado",
        AssetCrossSourceStates.NotEvaluated => "Vínculo ainda não avaliado",
        _ => "Sem fonte integrada",
    };

    public static string Explanation(string state, int linkedSources) => state switch
    {
        AssetCrossSourceStates.Linked =>
            $"Registros de {linkedSources} fontes estão associados a este ativo porque informaram o mesmo " +
            "identificador de dispositivo do Microsoft Entra, no mesmo diretório. Nome, endereço ou semelhança " +
            "não participam da associação.",
        AssetCrossSourceStates.IdentifierOnly =>
            "Uma fonte observou este dispositivo e informou o identificador de dispositivo do diretório. Quando outra " +
            "fonte informar o mesmo identificador, no mesmo diretório, os registros serão associados a este ativo.",
        AssetCrossSourceStates.NotLinked =>
            "Ainda não foi possível vincular este dispositivo entre fontes: falta um identificador de dispositivo de " +
            "diretório válido, ou o diretório de origem não foi confirmado. Os registros são preservados — isso não " +
            "significa que seja um dispositivo diferente dos observados por outras fontes.",
        AssetCrossSourceStates.Conflict =>
            "Há uma contradição entre identificadores em pelo menos um registro. Nenhum ativo foi escolhido " +
            "arbitrariamente e nada foi movido, fundido ou apagado; o motivo e as referências estão no registro da fonte.",
        AssetCrossSourceStates.NotEvaluated =>
            "Os registros deste ativo são anteriores à resolução por identificador de diretório e ainda não foram " +
            "reavaliados por uma nova coleta da fonte.",
        _ => "Ativo sem registro de fonte integrada (cadastro manual ou importado): não há registros a vincular.",
    };

    /// <summary>Rótulo e explicação de UM registro de fonte.</summary>
    public static (string Label, string Explanation) ForBinding(
        AssetBindingResolutionState state, AssetBindingConflictKind kind, DirectoryIdentifierStatus lastStatus) =>
        state switch
        {
            AssetBindingResolutionState.Linked => ("Vinculado pelo identificador de diretório",
                "Esta fonte informou o identificador de dispositivo do Microsoft Entra, no mesmo diretório, que " +
                "associa o registro a este ativo." +
                (lastStatus is DirectoryIdentifierStatus.Provided or DirectoryIdentifierStatus.NotEvaluated
                    ? ""
                    : " A última coleta não trouxe um identificador válido; o vínculo estabelecido antes foi mantido.")),
            AssetBindingResolutionState.NoIdentifier => ("Sem identificador de diretório",
                "A fonte não informou o identificador de dispositivo do diretório. O registro foi preservado, mas ainda " +
                "não pode ser vinculado a outras fontes — isso não significa que seja outro dispositivo."),
            AssetBindingResolutionState.InvalidIdentifier => ("Identificador de diretório inválido",
                "A fonte informou um valor que não é identificador de dispositivo válido (formato inválido, GUID vazio " +
                "ou valor provisório). Ele foi recusado e nenhuma união foi feita."),
            AssetBindingResolutionState.DirectoryUnconfirmed => ("Diretório de origem não confirmado",
                "O identificador foi informado, mas o diretório de origem não foi confirmado pela configuração da " +
                "integração. Sem o diretório, a união não é feita."),
            AssetBindingResolutionState.Conflict => kind switch
            {
                AssetBindingConflictKind.IdentifierChanged => ("Conflito: identificador mudou",
                    "A fonte passou a informar outro identificador de dispositivo para o mesmo registro. O registro " +
                    "continua neste ativo e nada foi movido até a análise."),
                AssetBindingConflictKind.IdentifierHeldByOtherAsset => ("Conflito: identificador já pertence a outro ativo",
                    "O identificador informado já está vinculado a outro ativo existente. Os dois ativos foram " +
                    "preservados — nenhum foi escolhido, fundido ou apagado (duplicidade entre ativos já existentes)."),
                AssetBindingConflictKind.AssetHeldByOtherIdentifier => ("Conflito: ativo já vinculado a outro dispositivo",
                    "Este ativo já está vinculado a outro dispositivo do mesmo diretório; o identificador informado " +
                    "não foi associado a ele."),
                _ => ("Conflito de vínculo", "Contradição entre identificadores preservada para análise."),
            },
            _ => ("Vínculo ainda não avaliado",
                "Registro anterior à resolução por identificador de diretório; será avaliado na próxima coleta desta fonte."),
        };

    public static string IdentifierStatusLabel(DirectoryIdentifierStatus s) => s switch
    {
        DirectoryIdentifierStatus.Provided => "Informado pela fonte",
        DirectoryIdentifierStatus.NotProvided => "Não informado pela fonte",
        DirectoryIdentifierStatus.Invalid => "Inválido (recusado)",
        _ => "Não avaliado",
    };

    public static string? ComplianceLabel(DeviceComplianceBucket? c) => c switch
    {
        null => null,
        DeviceComplianceBucket.Compliant => "Conforme, segundo a fonte",
        DeviceComplianceBucket.Noncompliant => "Não conforme, segundo a fonte",
        DeviceComplianceBucket.InGracePeriod => "Em período de carência, segundo a fonte",
        DeviceComplianceBucket.Conflict => "Políticas em conflito, segundo a fonte",
        DeviceComplianceBucket.Error => "Erro de avaliação na fonte",
        DeviceComplianceBucket.ManagedExternally => "Avaliado por gerenciador externo",
        _ => "Conformidade não informada",
    };

    public static string? EncryptionLabel(DeviceEncryptionBucket? e) => e switch
    {
        null => null,
        DeviceEncryptionBucket.Encrypted => "Criptografado, segundo a fonte",
        DeviceEncryptionBucket.NotEncrypted => "Sem criptografia, segundo a fonte",
        _ => "Criptografia não informada",
    };
}
