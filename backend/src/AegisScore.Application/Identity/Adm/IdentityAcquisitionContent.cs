using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AegisScore.Application.Identity.Adm;

/// <summary>
/// [AEGIS-ADM-01] CANONICALIZAÇÃO do conteúdo de uma aquisição — a definição executável de "é a mesma
/// aquisição" e, por consequência, de o que nela é IMUTÁVEL.
///
/// Três coisas são deliberadamente separadas, porque colapsá-las corrompe a evidência:
///   • IDENTIDADE da aquisição — o <c>AcquisitionId</c>, e só ele. Duas coletas distintas nunca compartilham
///     identificador, mesmo observando exatamente o mesmo conteúdo.
///   • CONTEÚDO imutável — tudo o que a aquisição AFIRMA: origem, versões, horários, estado, detalhe, fatos
///     agregados, capacidades e os conjuntos observados COM os atributos preservados de cada objeto e as
///     limitações declaradas. Depois de gravado, não se reescreve: uma avaliação já pode tê-lo consumido.
///   • PROJEÇÃO atual — os atributos correntes da entidade canônica. Mudam com o tempo, por definição, e por
///     isso NÃO entram aqui.
///
/// Por que uma forma canônica e não a concatenação de campos: <c>"a|b" + "c"</c> e <c>"a" + "b|c"</c> produzem
/// a mesma cadeia, e uma diferença real passaria por igualdade. A forma canônica é um documento JSON com
/// delimitação inequívoca, no qual as coleções cuja ORDEM NÃO TEM SIGNIFICADO (conjuntos, objetos de um
/// conjunto, papéis de um objeto, propriedades de um objeto JSON) são ordenadas antes de entrar. Já as listas
/// cuja ordem é conteúdo — os elementos de um array dentro de <c>FactsJson</c>/<c>CapabilitiesJson</c>, que a
/// própria serialização do AEGIS já emite ordenada — são preservadas como vieram.
/// </summary>
public static class IdentityAcquisitionContent
{
    /// <summary>
    /// Impressão digital (SHA-256 hex) do que foi OBSERVADO — estado da coleta, diretório, fatos agregados,
    /// capacidades e, conjunto a conjunto, o desfecho, a contagem, a completude, a LIMITAÇÃO declarada e
    /// TODOS os atributos preservados de cada objeto.
    ///
    /// Deliberadamente NÃO inclui os metadados do ATO de coletar (identificador, conector, rótulo da fonte,
    /// versões, horários, detalhe da tentativa): duas coletas distintas que enxergaram exatamente a mesma
    /// coisa devem RECONHECER-SE como tal. Esse reconhecimento nunca deduplica — as duas continuam sendo dois
    /// fatos, e apagar uma destruiria a prova de que a coleta aconteceu. Os metadados imutáveis são
    /// comparados campo a campo na verificação de conflito, onde um nome de campo explica melhor que um hash.
    /// </summary>
    public static string ObservedFingerprint(IdentityAcquisitionRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(request)))).ToLowerInvariant();

    /// <summary>
    /// Instante normalizado para comparação e canonicalização: UTC, truncado a MICROSSEGUNDOS — a resolução
    /// real do <c>timestamptz</c> do PostgreSQL. Sem isso, um valor de 100 ns vindo do relógio do processo
    /// deixaria de ser igual a si mesmo depois de uma ida-e-volta ao banco, e um retry legítimo seria
    /// recusado como conflito.
    /// </summary>
    public static DateTimeOffset NormalizeInstant(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));
    }

    /// <summary>
    /// Forma canônica textual do conteúdo OBSERVADO. Exposta porque um conflito precisa poder ser EXPLICADO:
    /// comparar duas formas canônicas mostra o campo que divergiu, coisa que dois hashes nunca mostram.
    /// </summary>
    public static string Canonicalize(IdentityAcquisitionRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();

            // O DIRETÓRIO é conteúdo observado: o mesmo identificador em outro namespace é outro objeto.
            w.WriteString("directoryNamespace", (request.Origin.DirectoryNamespace ?? "").Trim());
            w.WriteNumber("state", (int)request.State);

            // Os agregados atravessam como DOCUMENTOS canonicalizados (propriedades ordenadas), não como
            // texto cru: dois envelopes equivalentes que diferissem só na ordem das chaves não podem ser
            // lidos como conteúdo diferente.
            w.WritePropertyName("facts");
            WriteCanonicalJson(w, request.FactsJson);
            w.WritePropertyName("capabilities");
            WriteCanonicalJson(w, request.CapabilitiesJson);

            w.WriteStartArray("sets");
            foreach (var set in (request.Sets ?? Array.Empty<IdentityObservedSet>()).OrderBy(s => (int)s.Set))
            {
                w.WriteStartObject();
                w.WriteNumber("set", (int)set.Set);
                w.WriteNumber("outcome", (int)set.Outcome);
                w.WriteNumber("observedCount", set.ObservedCount);
                w.WriteBoolean("isComplete", set.IsComplete);
                WriteNullableString(w, "limitation", set.Limitation);

                w.WriteStartArray("objects");
                foreach (var o in Canonical(set))
                {
                    w.WriteStartObject();
                    w.WriteString("externalId", o.ExternalId);
                    w.WriteNumber("kind", (int)o.Kind);
                    WriteNullableString(w, "displayName", o.DisplayName);
                    WriteNullableString(w, "userPrincipalName", o.UserPrincipalName);
                    w.WriteStartArray("roles");
                    // Papéis: pertencimento, não sequência. A ordem em que o diretório os devolveu não é
                    // conteúdo, e tratá-la como tal faria a mesma observação parecer duas.
                    foreach (var role in (o.Roles ?? Array.Empty<string>()).OrderBy(r => r, StringComparer.Ordinal))
                        w.WriteStringValue(role);
                    w.WriteEndArray();
                    WriteNullableString(w, "detail", o.Detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Os objetos de um conjunto na MESMA forma em que serão persistidos: identificador vazio descartado,
    /// PRIMEIRA ocorrência de cada identificador preservada, ordem ordinal. A canonicalização precisa
    /// enxergar exatamente o que a gravação vai guardar — se divergisse, o fingerprint deixaria de descrever
    /// o registro e passaria a descrever a intenção.
    /// </summary>
    public static IReadOnlyList<IdentityObservedObject> Canonical(IdentityObservedSet set)
    {
        var byExternalId = new Dictionary<string, IdentityObservedObject>(StringComparer.Ordinal);
        foreach (var o in set.Objects ?? Array.Empty<IdentityObservedObject>())
        {
            var externalId = (o.ExternalId ?? "").Trim();
            if (externalId.Length == 0 || byExternalId.ContainsKey(externalId)) continue;
            byExternalId[externalId] = o with { ExternalId = externalId };
        }

        return byExternalId.Values.OrderBy(o => o.ExternalId, StringComparer.Ordinal).ToList();
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    /// <summary>
    /// Reemite um JSON com as propriedades de cada objeto em ordem ordinal. Um texto ilegível entra como
    /// cadeia crua — degradar para "conteúdo diferente" é correto; fingir que dois JSONs quebrados são iguais
    /// não seria.
    /// </summary>
    private static void WriteCanonicalJson(Utf8JsonWriter w, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { w.WriteNullValue(); return; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            WriteCanonicalElement(w, doc.RootElement);
        }
        catch (JsonException)
        {
            w.WriteStringValue(json);
        }
    }

    private static void WriteCanonicalElement(Utf8JsonWriter w, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteCanonicalElement(w, p.Value);
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                // A ORDEM de um array é conteúdo aqui: as observações do envelope de fatos já saem ordenadas
                // pela chave do sinal, e reordená-las às cegas apagaria diferenças reais em arrays de valores.
                w.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonicalElement(w, item);
                w.WriteEndArray();
                break;

            default:
                element.WriteTo(w);
                break;
        }
    }
}

/// <summary>
/// [AEGIS-ADM-01] Recusa em reescrever uma aquisição JÁ GRAVADA com conteúdo diferente.
///
/// O identificador de uma aquisição é a referência que uma avaliação guarda. Aceitar conteúdo novo sob o
/// mesmo identificador significaria que a prova citada por uma avaliação publicada pode mudar depois —
/// exatamente o que o modelo existe para impedir. A recusa é lançada ANTES de qualquer escrita: nenhuma
/// alteração parcial é aplicada.
/// </summary>
public sealed class IdentityAcquisitionConflictException : InvalidOperationException
{
    public IdentityAcquisitionConflictException(Guid acquisitionId, IReadOnlyList<string> divergences)
        : base($"A aquisição de identidade {acquisitionId} já está gravada e o que foi reapresentado diverge "
               + $"dela em: {string.Join("; ", divergences)}. Uma aquisição é evidência: reprocessá-la é "
               + "idempotente, reescrevê-la não é permitido — uma avaliação já pode citá-la. Uma coleta com "
               + "conteúdo diferente é uma aquisição NOVA, com identificador próprio.")
    {
        AcquisitionId = acquisitionId;
        Divergences = divergences;
    }

    public Guid AcquisitionId { get; }

    /// <summary>Campos que divergiram, nomeados — um hash diria que algo mudou, não O QUE mudou.</summary>
    public IReadOnlyList<string> Divergences { get; }
}
