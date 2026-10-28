using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

// ============================================================================
//  [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Evidência VERIFICÁVEL do acesso efetivo de uma aplicação
// ============================================================================
// Uma recusa (403, "InsufficientScopes", comando recusado) diz QUE a leitura não foi autorizada, nunca POR QUÊ. Antes
// deste pacote o AEGIS preenchia esse porquê com um palpite ("confira o papel Leitor do Teams") — e o palpite chegava ao
// relatório como se fosse diagnóstico. Aqui o porquê só entra quando há EVIDÊNCIA, de duas fontes:
//   • o PRÓPRIO TOKEN emitido para a leitura — é ele que o serviço avalia. Só claims de vocabulário fechado são lidas
//     (aud, tid, appid, roles, wids), validadas por conjunto de caracteres; o token nunca é guardado, registrado ou
//     repassado, e a assinatura não é verificada (não é preciso: o AEGIS só descreve o que pediu e recebeu);
//   • a ENTIDADE DE SERVIÇO da aplicação no Microsoft Graph, com as permissões de leitura que o conector já usa — cada
//     consulta termina em um de TRÊS estados: encontrado, ausência comprovada por leitura completa, ou não verificável.
// Nada aqui conclui um papel ausente a partir da recusa, e o papel consultado é sempre o da APLICAÇÃO, nunca o do operador.

/// <summary>Claims SEGURAS de um token de acesso (nunca o token).</summary>
public sealed record AccessTokenClaims(
    string? Audience,
    string? TenantId,
    string? AppId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> DirectoryRoleTemplateIds,
    bool HasDelegatedScopes)
{
    private const int MaxValues = 200;

    /// <summary>
    /// Lê o corpo do JWT. Token opaco, cifrado ou malformado devolve <c>null</c> (as verificações dependentes ficam
    /// "não verificáveis"); nenhum valor fora do vocabulário esperado é aceito.
    /// </summary>
    public static AccessTokenClaims? TryRead(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new AccessTokenClaims(
                ReadAudience(root),
                Guid(root, "tid"),
                Guid(root, "appid") ?? Guid(root, "azp"),
                Names(root, "roles"),
                Strings(root, "wids").Select(SafeGuid).Where(g => g is not null).Select(g => g!).Distinct().Take(MaxValues).ToList(),
                root.TryGetProperty("scp", out var scp) && scp.ValueKind == JsonValueKind.String);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }

    private static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : Enumerable.Empty<string>();

    /// <summary>Nomes de permissão (ex.: <c>Organization.Read.All</c>): só letras, dígitos, ponto, traço e sublinhado.</summary>
    private static IReadOnlyList<string> Names(JsonElement root, string name) =>
        Strings(root, name)
            .Where(s => s.Length is > 0 and <= 120 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            .Distinct(StringComparer.Ordinal).Take(MaxValues).ToList();

    private static string? Guid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? SafeGuid(v.GetString()) : null;

    private static string? SafeGuid(string? value) =>
        System.Guid.TryParse(value?.Trim(), out var g) ? g.ToString("D") : null;

    /// <summary>Identificador do recurso: GUID ou URI https curta, sem consulta nem credencial embutida.</summary>
    private static string? ReadAudience(JsonElement root)
    {
        if (!root.TryGetProperty("aud", out var v) || v.ValueKind != JsonValueKind.String) return null;
        var aud = v.GetString()!.Trim();
        if (SafeGuid(aud) is { } g) return g;
        return aud.Length <= 160
               && Uri.TryCreate(aud, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "api")
               && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
               && aud.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '/' or '.' or '-' or '_')
            ? aud.TrimEnd('/')
            : null;
    }
}

/// <summary>Um recurso da Microsoft que o AEGIS pede por token: os identificadores com que ele aparece no <c>aud</c>.</summary>
public sealed record MicrosoftTokenResource(string Name, IReadOnlyList<string> Audiences)
{
    public static readonly MicrosoftTokenResource Graph = new("Microsoft Graph",
        new[] { "https://graph.microsoft.com", "00000003-0000-0000-c000-000000000000" });

    /// <summary>"Skype and Teams Tenant Admin API" — o recurso do segundo token da autenticação de aplicativo do Teams.</summary>
    public static readonly MicrosoftTokenResource TeamsAdmin = new("administração do Teams (Skype and Teams Tenant Admin API)",
        new[] { "48ac35b8-9aa8-4d74-927d-1f4a14a0b239" });

    /// <summary>API do Fabric — publicada pela aplicação do serviço Power BI.</summary>
    public static readonly MicrosoftTokenResource Fabric = new("API do Microsoft Fabric",
        new[] { "https://api.fabric.microsoft.com", "https://analysis.windows.net/powerbi/api", "00000009-0000-0000-c000-000000000000" });

    private static readonly MicrosoftTokenResource[] Known = { Graph, TeamsAdmin, Fabric };

    public bool Matches(string audience) => Audiences.Any(a => string.Equals(a, audience, StringComparison.OrdinalIgnoreCase));

    /// <summary>Outro recurso conhecido que o <c>aud</c> identifica (prova de token emitido para o destino errado).</summary>
    public MicrosoftTokenResource? OtherKnown(string audience) => Known.FirstOrDefault(k => k != this && k.Matches(audience));
}

/// <summary>Três estados, nunca confundidos: encontrado, ausência comprovada por leitura completa, não verificável.</summary>
public enum AccessLookupState { Found, ProvenAbsent, NotVerifiable }

/// <summary>Resultado de UMA consulta à entidade de serviço. <see cref="Failure"/> é status/código já sanitizados.</summary>
public sealed record AccessLookup<T>(AccessLookupState State, T? Value, string? Failure = null)
{
    public static AccessLookup<T> NotVerifiable(string failure) => new(AccessLookupState.NotVerifiable, default, failure);
}

/// <summary>Atribuição ATIVA de papel de diretório à entidade de serviço.</summary>
public sealed record DirectoryRoleAssignment(string RoleTemplateId, string? RoleName, bool TenantWide);

/// <summary>Verificações de identidade e recurso a partir das claims do token (sem consulta externa).</summary>
public static class TokenIdentityCheck
{
    /// <summary>Divergências COMPROVADAS entre o token e o pedido; lista vazia não prova nada além delas.</summary>
    public static IReadOnlyList<string> Mismatches(AccessTokenClaims claims, IMicrosoftGraphCredentials credentials, MicrosoftTokenResource resource)
    {
        var problems = new List<string>();
        if (claims.Audience is { } aud && resource.OtherKnown(aud) is { } other)
            problems.Add($"o token pedido para {resource.Name} foi emitido para outro recurso ({other.Name})");
        // O locatário pode ser configurado pelo domínio: só um GUID permite comparar.
        if (claims.TenantId is { } tid && System.Guid.TryParse(credentials.AzureTenantId, out var configured)
            && !string.Equals(tid, configured.ToString("D"), StringComparison.OrdinalIgnoreCase))
            problems.Add("o token foi emitido por outro locatário, diferente do configurado no conector");
        if (claims.AppId is { } app && System.Guid.TryParse(credentials.ClientId, out var client)
            && !string.Equals(app, client.ToString("D"), StringComparison.OrdinalIgnoreCase))
            problems.Add("o token identifica outra aplicação, diferente da configurada no conector");
        if (claims.HasDelegatedScopes)
            problems.Add("o token é delegado (de usuário), não de aplicativo");
        return problems;
    }

    /// <summary>Uma linha de fato: o que o token declara, sem julgamento.</summary>
    public static string Describe(AccessTokenClaims? claims, IMicrosoftGraphCredentials credentials, MicrosoftTokenResource resource)
    {
        if (claims is null) return $"Token de {resource.Name}: emitido, mas o conteúdo não pôde ser lido — as verificações de identidade e recurso ficam não verificáveis.";
        var mismatches = Mismatches(claims, credentials, resource);
        if (mismatches.Count > 0) return $"Token de {resource.Name}: incompatível — " + string.Join("; ", mismatches) + ".";
        var audOk = claims.Audience is { } a && resource.Matches(a);
        return $"Token de {resource.Name}: emitido para a aplicação e o locatário configurados"
            + (audOk ? ", para o recurso esperado." : $" (recurso declarado: {claims.Audience ?? "não informado"}).");
    }
}

/// <summary>
/// Consultas de leitura à entidade de serviço da PRÓPRIA aplicação no Microsoft Graph. Cada uma devolve um
/// <see cref="AccessLookup{T}"/>; falha de consulta nunca vira ausência. Cancelamento do chamador propaga.
/// </summary>
public sealed class ServicePrincipalAccessReader
{
    private readonly IEntraGraphClient _graph;

    public ServicePrincipalAccessReader(IEntraGraphClient graph) => _graph = graph;

    /// <summary>Identificador do objeto da entidade de serviço da aplicação configurada.</summary>
    public async Task<AccessLookup<string>> FindAsync(string graphToken, IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        if (!System.Guid.TryParse(cfg.ClientId, out var appId))
            return AccessLookup<string>.NotVerifiable("identificador da aplicação não é um GUID");
        try
        {
            var sp = await _graph.GetJsonAsync(graphToken, cfg, $"servicePrincipals(appId='{appId:D}')?$select=id", ct);
            return sp.ValueKind == JsonValueKind.Object && sp.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                   && System.Guid.TryParse(id.GetString(), out var g)
                ? new AccessLookup<string>(AccessLookupState.Found, g.ToString("D"))
                : AccessLookup<string>.NotVerifiable("resposta sem o identificador da entidade de serviço");
        }
        catch (EntraGraphException ex) when (ex.HttpStatusCode == 404)
        {
            return new AccessLookup<string>(AccessLookupState.ProvenAbsent, null);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            return AccessLookup<string>.NotVerifiable(Failure(ex));
        }
    }

    /// <summary>Atribuições ATIVAS de papel de diretório da entidade de serviço (leitura completa, todas as páginas).</summary>
    public async Task<AccessLookup<IReadOnlyList<DirectoryRoleAssignment>>> DirectoryRolesAsync(
        string graphToken, IMicrosoftGraphCredentials cfg, string servicePrincipalId, CancellationToken ct)
    {
        try
        {
            var list = new List<DirectoryRoleAssignment>();
            var url = $"roleManagement/directory/roleAssignments?$filter=principalId eq '{servicePrincipalId}'&$select=roleDefinitionId,directoryScopeId";
            await foreach (var a in _graph.GetPagedAsync(graphToken, cfg, url, ct))
            {
                var role = KnightJson.Str(a, "roleDefinitionId");
                if (!System.Guid.TryParse(role, out var template)) continue;
                var id = template.ToString("D");
                list.Add(new DirectoryRoleAssignment(id, DirectoryRoleNames.Name(id), KnightJson.Str(a, "directoryScopeId") == "/"));
            }
            return list.Count == 0
                ? new AccessLookup<IReadOnlyList<DirectoryRoleAssignment>>(AccessLookupState.ProvenAbsent, list)
                : new AccessLookup<IReadOnlyList<DirectoryRoleAssignment>>(AccessLookupState.Found, list);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            return AccessLookup<IReadOnlyList<DirectoryRoleAssignment>>.NotVerifiable(Failure(ex));
        }
    }

    /// <summary>Quantos grupos de SEGURANÇA contêm a entidade de serviço, direta ou transitivamente (leitura completa).</summary>
    public async Task<AccessLookup<int>> SecurityGroupCountAsync(
        string graphToken, IMicrosoftGraphCredentials cfg, string servicePrincipalId, CancellationToken ct)
    {
        try
        {
            var n = 0;
            var url = $"servicePrincipals/{servicePrincipalId}/transitiveMemberOf/microsoft.graph.group?$select=id,securityEnabled";
            await foreach (var g in _graph.GetPagedAsync(graphToken, cfg, url, ct))
                if (g.TryGetProperty("securityEnabled", out var s) && s.ValueKind == JsonValueKind.True) n++;
            return new AccessLookup<int>(n == 0 ? AccessLookupState.ProvenAbsent : AccessLookupState.Found, n);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            return AccessLookup<int>.NotVerifiable(Failure(ex));
        }
    }

    /// <summary>Leitura efetiva de <c>/organization</c> com o token do Graph (o que a permissão concede de fato).</summary>
    public async Task<AccessLookup<bool>> OrganizationReadAsync(string graphToken, IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        try
        {
            await _graph.GetJsonAsync(graphToken, cfg, "organization?$select=id", ct);
            return new AccessLookup<bool>(AccessLookupState.Found, true);
        }
        catch (EntraGraphException ex) when (ex.Kind == EntraGraphErrorKind.InsufficientPermission)
        {
            return new AccessLookup<bool>(AccessLookupState.ProvenAbsent, false, Failure(ex));
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            return AccessLookup<bool>.NotVerifiable(Failure(ex));
        }
    }

    /// <summary>Status e código sanitizados — nunca a mensagem.</summary>
    internal static string Failure(Exception ex) => ex is EntraGraphException g
        ? string.Join(" · ", new[] { g.HttpStatusCode is { } s ? $"HTTP {s}" : null, g.GraphErrorCode }.Where(x => x is not null))
          is { Length: > 0 } text ? text : g.Kind.ToString()
        : KnightFailureClassifier.SafeTypeName(ex);
}

/// <summary>Nomes (em português) dos papéis de diretório que importam a estas verificações, pelo modelo oficial.</summary>
public static class DirectoryRoleNames
{
    public const string TeamsAdministrator = "69091246-20e8-4a56-aa4d-066075b2a7a8";
    public const string TeamsCommunicationsAdministrator = "baf37b3a-610e-45da-9e62-d9d1e5e8914b";
    public const string TeamsTelephonyAdministrator = "aa38014f-0993-46e9-9b45-30501a20909d";
    public const string TeamsReader = "1076ac91-f3d9-41a7-a339-dcdf5f480acc";
    public const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [TeamsAdministrator] = "Administrador do Teams",
        [TeamsCommunicationsAdministrator] = "Administrador de Comunicações do Teams",
        ["f70938a0-fc10-4177-9e90-2178f8765737"] = "Engenheiro de Suporte de Comunicações do Teams",
        ["fcf91098-03e3-41a9-b5ba-6f0ec8188a12"] = "Especialista de Suporte de Comunicações do Teams",
        ["3d762c5a-1b6c-493f-843e-55a3b42923d4"] = "Administrador de Dispositivos do Teams",
        [TeamsTelephonyAdministrator] = "Administrador de Telefonia do Teams",
        [TeamsReader] = "Leitor do Teams",
        [GlobalReader] = "Leitor Global",
        [GlobalAdministrator] = "Administrador Global",
        ["a9ea8996-122f-4c74-9520-8edcd192826c"] = "Administrador do Fabric",
    };

    /// <summary>Papéis que a tabela oficial de papéis do Teams associa EXPLICITAMENTE ao módulo PowerShell.</summary>
    public static readonly IReadOnlySet<string> TeamsPowerShellDocumented = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TeamsAdministrator, TeamsCommunicationsAdministrator, TeamsTelephonyAdministrator,
    };

    /// <summary>Papéis de leitura que a tabela cita só para o centro de administração (sem a nota do módulo PowerShell).</summary>
    public static readonly IReadOnlySet<string> TeamsReadOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TeamsReader, GlobalReader };

    public static string? Name(string templateId) => Names.TryGetValue(templateId, out var n) ? n : null;

    public static bool IsTeamsRelevant(string templateId) => Names.ContainsKey(templateId) && templateId != "a9ea8996-122f-4c74-9520-8edcd192826c";
}
