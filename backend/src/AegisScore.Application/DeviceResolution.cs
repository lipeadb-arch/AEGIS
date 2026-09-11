using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-ENTITY-RESOLUTION-01] Resolução de dispositivos entre fontes — contrato PROVIDER-NEUTRAL ----
// O ADAPTADOR normaliza o que a fonte disse sobre o dispositivo (id na fonte, identificador de diretório, fatos já
// coletados); a AUTORIDADE ÚNICA de resolução (DeviceIdentityResolver, na infraestrutura) decide o ativo canônico.
// Nenhum reconciliador decide sozinho — é o que impede duas regras divergentes para o mesmo dispositivo.

/// <summary>
/// O que UMA observação disse sobre o identificador de dispositivo no diretório, já validado para o tipo. O valor
/// só existe quando <see cref="Status"/> é <see cref="DirectoryIdentifierStatus.Provided"/>.
/// </summary>
public sealed record DirectoryDeviceIdObservation(
    DirectoryIdentifierStatus Status,
    string? Value,
    /// <summary>
    /// Só em <see cref="DirectoryIdentifierStatus.Contradictory"/>: os identificadores VÁLIDOS e distintos que as
    /// repetições do registro trouxeram, ordenados (ordinal) e separados por vírgula — string, e não lista, para
    /// manter a igualdade por valor do record. Vazio quando a contradição foi entre um valor e ausência/invalidez.
    /// </summary>
    string? ContradictoryValues = null)
{
    /// <summary>A fonte não informou o campo (ausente, nulo ou vazio). Não é falha da coleta.</summary>
    public static readonly DirectoryDeviceIdObservation NotProvided = new(DirectoryIdentifierStatus.NotProvided, null);

    /// <summary>A fonte informou um valor que não é identificador válido (formato, GUID vazio, placeholder, tipo JSON).</summary>
    public static readonly DirectoryDeviceIdObservation Invalid = new(DirectoryIdentifierStatus.Invalid, null);

    public bool IsValid => Status == DirectoryIdentifierStatus.Provided && Value is not null;

    public bool IsContradictory => Status == DirectoryIdentifierStatus.Contradictory;

    /// <summary>Identificadores válidos envolvidos numa contradição (vazio fora dela).</summary>
    public IReadOnlyList<string> ContradictoryValueList =>
        string.IsNullOrEmpty(ContradictoryValues)
            ? Array.Empty<string>()
            : ContradictoryValues.Split(',', StringSplitOptions.RemoveEmptyEntries);

    internal static DirectoryDeviceIdObservation Contradictory(IEnumerable<string> validValues) =>
        new(DirectoryIdentifierStatus.Contradictory, null,
            string.Join(",", validValues.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)));
}

/// <summary>
/// Normalização ÚNICA dos identificadores usados na resolução. Os dois conectores (Defender e Intune) passam por
/// aqui — nenhum decide sozinho o que é um identificador válido.
///
/// Regras (documentadas porque recusam dados):
///   • só GUID no formato canônico com hífens ("D"), em qualquer caixa; normalizado para minúsculas;
///   • GUID vazio (<c>00000000-…</c>) é recusado — é o valor que o Intune devolve para dispositivo não registrado;
///   • GUID com um único dígito hexadecimal repetido (<c>ffffffff-…</c>, <c>11111111-…</c>) é recusado como
///     placeholder: não identifica dispositivo real;
///   • ausência não é invalidez: um campo ausente é <see cref="DirectoryIdentifierStatus.NotProvided"/>.
/// </summary>
public static class DeviceDirectoryIdentifiers
{
    /// <summary>
    /// Namespace do diretório a partir do identificador do tenant do Entra EFETIVAMENTE usado na credencial da
    /// integração. Devolve <c>null</c> (diretório NÃO confirmado) quando o valor configurado não é um GUID —
    /// por exemplo, um domínio. O namespace nunca é deduzido de hostname, domínio de e-mail ou do tenant AEGIS.
    /// </summary>
    public static string? NormalizeNamespace(string? configuredDirectoryTenantId) => NormalizeGuid(configuredDirectoryTenantId);

    /// <summary>Valida e normaliza o identificador de dispositivo no diretório informado pela fonte.</summary>
    public static DirectoryDeviceIdObservation ParseDeviceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DirectoryDeviceIdObservation.NotProvided;
        var normalized = NormalizeGuid(raw);
        return normalized is null
            ? DirectoryDeviceIdObservation.Invalid
            : new DirectoryDeviceIdObservation(DirectoryIdentifierStatus.Provided, normalized);
    }

    /// <summary>
    /// Combina o que DUAS repetições do MESMO registro da fonte, na MESMA coleta, disseram sobre o identificador.
    /// Repetição equivalente (mesmo estado e valor) permanece como está; qualquer divergência — X e Y, ou X e
    /// ausente/inválido — vira <see cref="DirectoryIdentifierStatus.Contradictory"/>, carregando a união dos valores
    /// válidos. Comutativa e associativa: o resultado NÃO depende da ordem das páginas nem de qual repetição chegou
    /// primeiro. Nenhum dos valores de uma contradição é usado para vincular.
    /// </summary>
    public static DirectoryDeviceIdObservation Merge(DirectoryDeviceIdObservation? a, DirectoryDeviceIdObservation? b)
    {
        a ??= DirectoryDeviceIdObservation.NotProvided;
        b ??= DirectoryDeviceIdObservation.NotProvided;
        if (a == b) return a;
        return DirectoryDeviceIdObservation.Contradictory(ValidValues(a).Concat(ValidValues(b)));
    }

    private static IEnumerable<string> ValidValues(DirectoryDeviceIdObservation o) =>
        o.IsValid ? new[] { o.Value! } : o.ContradictoryValueList;

    private static string? NormalizeGuid(string? raw)
    {
        var t = raw?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!Guid.TryParseExact(t, "D", out var g) || g == Guid.Empty) return null;
        var s = g.ToString("D");   // minúsculas, formato canônico
        return IsRepeatedDigitPlaceholder(s) ? null : s;
    }

    private static bool IsRepeatedDigitPlaceholder(string guidD)
    {
        char? first = null;
        foreach (var c in guidD)
        {
            if (c == '-') continue;
            first ??= c;
            if (c != first) return false;
        }
        return true;
    }
}

/// <summary>
/// UM dispositivo como a fonte o observou, no vocabulário provider-neutral. Carrega só o necessário à resolução e
/// à leitura mínima: id na fonte, identificador de diretório validado, nome/plataforma quando a fonte os coleta,
/// última atividade informada pela fonte e — para fontes de gestão — conformidade e criptografia COMO INFORMAÇÃO
/// DA FONTE. Nunca IP, usuário, e-mail, número de série ou payload.
/// </summary>
public sealed record DeviceSourceObservation(
    string ExternalId,
    DirectoryDeviceIdObservation DirectoryDeviceId,
    string? DisplayName,
    string? SubType,
    DateTimeOffset? SourceLastSeenAt,
    DeviceComplianceBucket? Compliance = null,
    DeviceEncryptionBucket? Encryption = null);

/// <summary>
/// Regras ÚNICAS sobre observações de dispositivo repetidas e sobre o id do registro na fonte — usadas pelos
/// conectores (fronteira) e pela autoridade de resolução (contrato), para que as duas camadas não divirjam.
/// </summary>
public static class DeviceSourceObservations
{
    /// <summary>Tamanho máximo do id na fonte (coluna <c>AssetSourceBindings.ExternalId</c>).</summary>
    public const int MaxExternalIdLength = 200;

    /// <summary>
    /// O id na fonte é a chave natural do registro: é aceito EXATAMENTE como veio ou recusado — nunca truncado nem
    /// aparado, porque dois valores distintos passariam a representar o mesmo registro. Recusa vazio, só espaços,
    /// espaços nas bordas e comprimento acima do contrato. Ausência de id e id fora do contrato tornam o
    /// REGISTRO inválido (a coleta deixa de ser completa); isso é diferente do estado do identificador de diretório.
    /// </summary>
    public static bool IsExternalIdWithinContract(string? externalId) =>
        !string.IsNullOrWhiteSpace(externalId)
        && externalId.Length <= MaxExternalIdLength
        && !char.IsWhiteSpace(externalId[0])
        && !char.IsWhiteSpace(externalId[^1]);

    /// <summary>
    /// Combina duas repetições do MESMO registro numa coleta. O identificador de diretório passa por
    /// <see cref="DeviceDirectoryIdentifiers.Merge"/> (divergência = contradição, nunca "o primeiro"); os demais
    /// fatos vêm da repetição preferida por uma ordem TOTAL (atividade mais recente informada pela fonte e, no
    /// empate, comparação ordinal) — o resultado não depende da ordem das páginas.
    /// </summary>
    public static DeviceSourceObservation Merge(DeviceSourceObservation a, DeviceSourceObservation b)
    {
        if (!string.Equals(a.ExternalId, b.ExternalId, StringComparison.Ordinal))
            throw new ArgumentException("Só repetições do MESMO registro da fonte podem ser combinadas.");
        if (a == b) return a;
        var preferred = Compare(a, b) >= 0 ? a : b;
        return preferred with { DirectoryDeviceId = DeviceDirectoryIdentifiers.Merge(a.DirectoryDeviceId, b.DirectoryDeviceId) };
    }

    /// <summary>Uma observação por registro da fonte, combinando repetições (ordem de entrada irrelevante).</summary>
    public static List<DeviceSourceObservation> Consolidate(IEnumerable<DeviceSourceObservation> observations) =>
        observations
            .GroupBy(o => o.ExternalId, StringComparer.Ordinal)
            .Select(g => g.Aggregate(Merge))
            .ToList();

    private static int Compare(DeviceSourceObservation a, DeviceSourceObservation b)
    {
        var c = Nullable.Compare(a.SourceLastSeenAt, b.SourceLastSeenAt);
        if (c != 0) return c;
        c = -string.CompareOrdinal(a.DisplayName, b.DisplayName);
        if (c != 0) return c;
        c = -string.CompareOrdinal(a.SubType, b.SubType);
        if (c != 0) return c;
        c = -Nullable.Compare(a.Compliance, b.Compliance);
        return c != 0 ? c : -Nullable.Compare(a.Encryption, b.Encryption);
    }
}

/// <summary>
/// Marca de uma fotografia de dispositivos: instante em que a coleta COMEÇOU, em UTC e truncado a microssegundos
/// (a precisão do <c>timestamptz</c>), para que a marca em memória e a lida do banco sejam comparáveis por igualdade.
/// </summary>
public static class DeviceSnapshotMarker
{
    public static DateTimeOffset Normalize(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>
/// Contagens HONESTAS de uma passada de resolução de UMA fonte. Só números — nunca identificadores. Distingue
/// vínculo, falta de evidência e contradição.
/// </summary>
public sealed record DeviceResolutionSyncResult(
    int Observed,
    int AssetsCreated,
    int BindingsCreated,
    int Linked,
    int KeysEstablished,
    int WithoutIdentifier,
    int InvalidIdentifier,
    int DirectoryUnconfirmed,
    int Conflicts,
    int BindingsDeactivated,
    bool DeactivationApplied,
    /// <summary>Registros cujo id na fonte está fora do contrato: recusados (nunca truncados); a passada deixa de ser completa.</summary>
    int RejectedObservations = 0,
    /// <summary>Registros com identificadores contraditórios na mesma coleta (contados também em <see cref="Conflicts"/>).</summary>
    int ContradictoryIdentifiers = 0,
    /// <summary>
    /// A passada foi SUPERADA por uma fotografia mais recente do mesmo conector: não publicou (ou parou de publicar)
    /// presença, ausência e estado consolidado — nada do que a fotografia mais nova afirmou foi anulado.
    /// </summary>
    bool Superseded = false);

/// <summary>
/// Chave da trava consultiva de TRANSAÇÃO que serializa, no PostgreSQL, a resolução de dispositivos de um
/// <c>(tenant, namespace do diretório)</c> — o escopo em que vivem as chaves fortes e que Defender e Intune
/// disputam. Prefixo próprio: não serializa (nem é serializada por) a trava do ADM de identidade.
/// </summary>
public static class DeviceDirectoryLock
{
    public static long AdvisoryKey(Guid tenantId, string directoryNamespace)
    {
        var ns = (directoryNamespace ?? "").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"aegis-device-directory|{tenantId:D}|{ns}"));
        return BitConverter.ToInt64(bytes, 0);
    }
}

/// <summary>
/// Chave da trava consultiva de TRANSAÇÃO do CICLO DE VIDA de UMA fonte (conector) de dispositivos: presença,
/// ausência e a marca de precedência (<c>ConnectorConfig.DeviceSnapshotWatermark</c>) de um mesmo conector são
/// publicadas uma de cada vez, em qualquer instância. Ordem fixa de aquisição: fonte → diretório.
/// </summary>
public static class DeviceSourceLock
{
    public static long AdvisoryKey(Guid tenantId, Guid connectorId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"aegis-device-source|{tenantId:D}|{connectorId:D}"));
        return BitConverter.ToInt64(bytes, 0);
    }
}
