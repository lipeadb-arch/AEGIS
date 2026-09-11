using System;
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
public sealed record DirectoryDeviceIdObservation(DirectoryIdentifierStatus Status, string? Value)
{
    /// <summary>A fonte não informou o campo (ausente, nulo ou vazio). Não é falha da coleta.</summary>
    public static readonly DirectoryDeviceIdObservation NotProvided = new(DirectoryIdentifierStatus.NotProvided, null);

    /// <summary>A fonte informou um valor que não é identificador válido (formato, GUID vazio, placeholder, tipo JSON).</summary>
    public static readonly DirectoryDeviceIdObservation Invalid = new(DirectoryIdentifierStatus.Invalid, null);

    public bool IsValid => Status == DirectoryIdentifierStatus.Provided && Value is not null;
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
    bool DeactivationApplied);

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
