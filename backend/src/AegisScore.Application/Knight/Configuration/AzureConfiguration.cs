using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Contratos da configuração do Azure lida pelo Resource Manager
// ============================================================================
// Dois documentos, e nenhum guarda o recurso inteiro:
//   • ASSINATURA — está no escopo pedido? e, para cada FAMÍLIA de leitura (rede, armazenamento, …), qual foi o desfecho
//     NELA. É daqui que a regra sabe se a população que avalia está completa: um recurso reprovado continua reprovado,
//     mas aprovar exige que a família tenha sido lida em TODAS as assinaturas do escopo;
//   • RECURSO — identidade (id, tipo, nome, assinatura, grupo, região) e só os FATOS que alguma regra usa, por caminho
//     ("properties.minimumTlsVersion", "web:properties.ftpsState"). O coletor copia uma lista FECHADA de caminhos por
//     tipo: o resto da resposta — variáveis de ambiente de contêiner, cadeias de conexão, o que for — nunca é gravado.
// Um fato ausente é DESCONHECIDO: não aprova nem reprova. Onde a documentação da Microsoft fixa o valor padrão de uma
// propriedade omitida, a regra diz isso explicitamente — nunca o coletor por conta própria.

/// <summary>Desfecho de UMA família de leitura numa assinatura.</summary>
public sealed record AzureFamilyRead(KnightCapability Capability, KnightCapabilityOutcome Outcome, bool Truncated, string? Detail);

/// <summary>Uma assinatura vista pela aplicação e o desfecho de cada família de leitura nela.</summary>
/// <remarks>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] <paramref name="Visible"/> = <c>false</c>: assinatura PEDIDA no escopo que a descoberta não
/// encontrou. Fica no escopo, com as famílias não lidas — o controle não aprova "todas as assinaturas" sem ela. Inventários
/// anteriores não têm o campo e valem como visíveis (o padrão).
/// </remarks>
/// <remarks>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] <paramref name="Access"/> diz COMO a assinatura chegou ao inventário (ver
/// <see cref="AzureSubscriptionAccess"/>), <paramref name="AccessDetail"/> o resultado da consulta individual de uma assinatura
/// pedida e não listada (status, código, endpoint) e <paramref name="TenantId"/> o diretório que a Microsoft informou. Campos
/// aditivos com padrão nulo: inventários anteriores continuam legíveis na mesma versão do contrato.
/// </remarks>
public sealed record AzureSubscriptionInventory(
    string SubscriptionId,
    string? DisplayName,
    string? State,
    bool InScope,
    IReadOnlyList<AzureFamilyRead> Reads,
    bool Visible = true,
    string? Access = null,
    string? AccessDetail = null,
    string? TenantId = null)
{
    public const string SchemaVersion = "aegis-config-azure-subscription-v1";

    public AzureFamilyRead? ReadOf(KnightCapability family) => Reads.FirstOrDefault(r => r.Capability == family);
}

/// <summary>[AEGIS-KNIGHT-COLLECTION-GAPS-01] Como uma assinatura entrou no inventário da descoberta.</summary>
public static class AzureSubscriptionAccess
{
    /// <summary>Listada pelo Resource Manager (GET /subscriptions).</summary>
    public const string Listed = "Listed";

    /// <summary>Pedida, não listada, e confirmada pela consulta individual (GET /subscriptions/{id}): é lida.</summary>
    public const string DirectRead = "DirectRead";

    /// <summary>Pedida, não listada, e a consulta individual foi RECUSADA (403): selecionada, sem acesso.</summary>
    public const string Denied = "Denied";

    /// <summary>Pedida, não listada, e a consulta individual respondeu 404: inexistente ou não visível neste locatário.</summary>
    public const string NotFound = "NotFound";

    /// <summary>Pedida, não listada, e a consulta individual falhou por outro motivo (serviço, tempo, contrato).</summary>
    public const string Unverified = "Unverified";
}

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Resumo VERIFICÁVEL da descoberta das assinaturas de UMA coleta: o que foi listado,
/// selecionado, desabilitado e pedido sem acesso — contados SEPARADAMENTE — e a identidade do token do Resource Manager
/// (recurso e locatário, lidos das declarações do próprio token; nunca o token). É daqui que Integrações e o motivo dos
/// controles dizem por que nenhuma assinatura entrou no escopo, sem que alguém precise ler logs.
/// </summary>
/// <param name="Listed">Assinaturas devolvidas pela listagem (lista válida, possivelmente vazia).</param>
/// <param name="Selected">Assinaturas efetivamente lidas (no escopo e acessíveis).</param>
/// <param name="Disabled">Listadas em estado Disabled ou Deleted (fora da seleção automática).</param>
/// <param name="OutsideRequestedScope">Listadas e fora da lista explícita pedida.</param>
/// <param name="Requested">Tamanho da lista explícita pedida (0 = todas as habilitadas).</param>
/// <param name="RequestedNotListed">Pedidas e ausentes da listagem.</param>
/// <param name="RequestedDirectRead">Pedidas, não listadas, confirmadas pela consulta individual (lidas).</param>
/// <param name="RequestedDenied">Pedidas, não listadas, com a consulta individual recusada (selecionadas sem acesso).</param>
/// <param name="RequestedNotFound">Pedidas, não listadas, com 404 na consulta individual.</param>
/// <param name="RequestedUnverified">Pedidas, não listadas, cuja consulta individual falhou por outro motivo.</param>
/// <param name="OtherDirectory">Listadas cujo diretório informado difere do locatário configurado.</param>
/// <param name="TokenAudience">Declaração <c>aud</c> do token do Resource Manager (nula quando o token não é legível).</param>
/// <param name="TokenTenantMatches">A declaração <c>tid</c> confere com o locatário configurado (nulo = não verificável).</param>
public sealed record AzureDiscoverySummary(
    int Listed,
    int Selected,
    int Disabled,
    int OutsideRequestedScope,
    int Requested,
    int RequestedNotListed,
    int RequestedDirectRead,
    int RequestedDenied,
    int RequestedNotFound,
    int RequestedUnverified,
    int OtherDirectory,
    string? TokenAudience,
    bool? TokenTenantMatches)
{
    public const string SchemaVersion = "aegis-config-azure-discovery-v1";
    public const string ExternalId = "azureDiscovery";

    /// <summary>Por que NENHUMA assinatura foi lida — em palavras, com o que isso prova e o que não prova.</summary>
    public string NoScopeReason()
    {
        string why;
        if (Listed == 0 && Requested == 0)
            why = "a listagem do Resource Manager respondeu com lista válida e VAZIA, e nenhuma assinatura foi pedida explicitamente";
        else if (Listed == 0)
            why = $"a listagem respondeu com lista válida e vazia, e {Requested} assinatura(s) pedida(s) não foi(ram) confirmada(s) pela consulta individual ("
                  + Parts() + ")";
        else
            why = $"{Listed} assinatura(s) listada(s) e nenhuma selecionada ("
                  + string.Join(", ", new[]
                  {
                      Disabled > 0 ? $"{Disabled} desabilitada(s)" : null,
                      OutsideRequestedScope > 0 ? $"{OutsideRequestedScope} fora do escopo pedido" : null,
                      RequestedNotListed > 0 ? Parts() : null,
                  }.Where(x => x is not null)) + ")";
        return $"nenhuma assinatura no escopo desta coleta: {why}. Isso não prova, sozinho, falta do papel Leitor, ausência de recursos nem conformidade.";
    }

    private string Parts() => string.Join(", ", new[]
    {
        RequestedDenied > 0 ? $"{RequestedDenied} com autorização recusada" : null,
        RequestedNotFound > 0 ? $"{RequestedNotFound} não encontrada(s) neste locatário" : null,
        RequestedUnverified > 0 ? $"{RequestedUnverified} sem resposta conclusiva" : null,
    }.Where(x => x is not null));
}

/// <summary>Um recurso do Azure com os fatos que as regras usam (lista fechada por tipo).</summary>
public sealed record AzureResource(
    string Id,
    string Type,
    string Name,
    string SubscriptionId,
    string? ResourceGroup,
    string? Location,
    string? Kind,
    KnightCapability Family,
    IReadOnlyDictionary<string, JsonElement> Facts,
    string? ParentId = null)
{
    public const string SchemaVersion = "aegis-config-azure-resource-v1";

    public bool Is(string type) => string.Equals(Type, type, StringComparison.OrdinalIgnoreCase);

    public bool Has(string path) => Get(path) is not null;

    /// <summary>
    /// O fato pelo caminho exato; senão, navega a partir do fato gravado que é PREFIXO do caminho (o coletor grava objetos
    /// inteiros da lista fechada — "properties.sslPolicy" — e a regra lê "properties.sslPolicy.policyName").
    /// </summary>
    public JsonElement? Get(string path)
    {
        if (Facts.TryGetValue(path, out var v))
            return v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : v;
        for (var i = path.LastIndexOf('.'); i > 0; i = path.LastIndexOf('.', i - 1))
            if (Facts.TryGetValue(path[..i], out var parent))
                return AzureJson.Prop(parent, path[(i + 1)..]);
        return null;
    }

    public string? Str(string path) => Get(path) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null,
        }
        : null;

    /// <summary>Booleano; aceita também "true"/"false" e "Enabled"/"Disabled" em texto, como a ARM devolve em parte dos recursos.</summary>
    public bool? Bool(string path) => Get(path) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "enabled" or "on" or "1" => true,
                "false" or "disabled" or "off" or "0" => false,
                _ => null,
            },
            _ => null,
        }
        : null;

    public long? Int(string path) => Get(path) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
            _ => null,
        }
        : null;

    /// <summary>Itens de um fato lista (ausente = vazio; use <see cref="Has"/> para distinguir).</summary>
    public IReadOnlyList<JsonElement> Items(string path) =>
        Get(path) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : Array.Empty<JsonElement>();

    public IReadOnlyList<string> Strings(string path) =>
        Items(path).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();

    /// <summary>
    /// Identificador compacto para a coluna do ADM (200): o id da ARM quando cabe; senão, um resumo SHA-256 do id com o
    /// final legível. O id completo continua no documento.
    /// </summary>
    public static string CompactId(string id)
    {
        if (id.Length <= 200) return id;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant())))[..24].ToLowerInvariant();
        var tail = id[^Math.Min(160, id.Length)..];
        return $"sha256:{hash}:…{tail}";
    }
}

/// <summary>Leitura de uma propriedade de objeto JSON (item de um fato lista) sem distinção de caixa.</summary>
public static class AzureJson
{
    public static JsonElement? Prop(JsonElement e, string path)
    {
        var cur = e;
        foreach (var part in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            JsonElement? next = null;
            foreach (var p in cur.EnumerateObject())
                if (string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase)) { next = p.Value; break; }
            if (next is null) return null;
            cur = next.Value;
        }
        return cur.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : cur;
    }

    public static string? Str(JsonElement e, string path) => Prop(e, path) is { } v
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : null
        : null;

    public static bool? Bool(JsonElement e, string path) => Prop(e, path) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "enabled" or "on" => true,
                "false" or "disabled" or "off" => false,
                _ => null,
            },
            _ => null,
        }
        : null;

    public static long? Int(JsonElement e, string path) => Prop(e, path) is { } v
        ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : long.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : null, out var s) ? s : null
        : null;

    public static IReadOnlyList<JsonElement> Items(JsonElement e, string path) =>
        Prop(e, path) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : Array.Empty<JsonElement>();
}
