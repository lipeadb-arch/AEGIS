using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Reference;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] Catálogo de REFERÊNCIA e cobertura de implementação
// ============================================================================
// Três números que o produto NÃO pode misturar:
//   1. cobertura de IMPLEMENTAÇÃO do catálogo de referência — quantos controles de referência o AEGIS consegue
//      avaliar (é uma propriedade do produto, não do cliente);
//   2. cobertura da AVALIAÇÃO no ambiente do cliente — quantos controles aplicáveis a coleta conseguiu avaliar
//      (a "cobertura" que a execução já registra);
//   3. APROVAÇÃO dos controles avaliados.
//
// Este arquivo cuida só do primeiro. O catálogo de referência é a lista de controles de configuração dos
// benchmarks (Microsoft 365 e Azure), fixada numa versão rastreável. Cada controle recebe uma DISPOSIÇÃO:
//   • IMPLEMENTADO — um controle do KNIGHT avalia o critério;
//   • PARCIAL — um controle do KNIGHT avalia um critério equivalente, mas não idêntico (a nota diz a diferença);
//   • PENDENTE — ainda não implementado;
//   • LIMITAÇÃO DE API — sem leitura na versão estável da API oficial (só beta ou só portal);
//   • EXIGE OUTRO ACESSO — há método oficial, mas com autenticação/permissão que o conector atual não tem;
//   • VERIFICAÇÃO MANUAL — o critério depende de contexto organizacional que nenhuma configuração expressa.
// Nenhuma dessas três vira avaliação executada nem aprovação.
// A disposição IMPLEMENTADO/PARCIAL não é declarada à mão: ela vem das referências que os próprios controles
// do catálogo KNIGHT declaram — E só vale quando o controle está no fluxo ativo (catálogo corrente, aplicável a
// uma fonte real) e a coleta que ele consome é produzida pelo coletor dessa fonte. Declarar uma referência numa
// regra sem coleta não a torna implementada: o controle de referência continua PENDENTE, com o motivo.
// Os testes exigem, para cada controle que sustenta uma referência implementada, cenários de aprovação e de
// reprovação pelo caminho do coletor (resposta da API → ADM → releitura → avaliação).

/// <summary>Um controle do catálogo de referência (seção do benchmark, serviço, severidade de referência, título autoral).</summary>
public sealed record KnightReferenceControl(
    string Key,
    string Framework,
    string Version,
    string? Section,
    string? Variant,
    KnightService Service,
    SeverityLevel Severity,
    string Title);

/// <summary>Quão fielmente um controle do KNIGHT avalia o critério de referência que ele cita.</summary>
public enum KnightReferenceMatch
{
    /// <summary>O critério avaliado é o da referência.</summary>
    Exact = 0,

    /// <summary>Critério equivalente, mas não idêntico — a nota do vínculo explica a diferença.</summary>
    Partial = 1,
}

/// <summary>Vínculo de um controle do KNIGHT com um controle de referência.</summary>
public sealed record KnightReferenceLink(string Key, KnightReferenceMatch Match = KnightReferenceMatch.Exact, string? Note = null);

/// <summary>Situação de um controle de referência no produto.</summary>
public enum KnightReferenceDisposition
{
    /// <summary>Avaliado integralmente por controle ativo, com a coleta que ele consome.</summary>
    Implemented = 0,

    /// <summary>Avaliado por controle ativo com critério equivalente, mas não idêntico (a nota diz a diferença).</summary>
    Partial = 1,

    /// <summary>Ainda não implementado.</summary>
    Pending = 2,

    /// <summary>O critério depende de contexto organizacional: verificação manual.</summary>
    ManualOnly = 3,

    /// <summary>Há método oficial de leitura, mas com acesso que o conector atual não tem.</summary>
    RequiresAccess = 4,

    /// <summary>Sem leitura na versão estável da API oficial (só beta ou só portal).</summary>
    ApiLimitation = 5,
}

/// <summary>Um controle de referência e a sua situação no produto, com os controles KNIGHT que o avaliam.</summary>
public sealed record KnightReferenceStatus(
    KnightReferenceControl Control,
    KnightReferenceDisposition Disposition,
    IReadOnlyList<string> IndicatorIds,
    string? Note);

/// <summary>
/// Contagem de um recorte (total, serviço ou plataforma). A cobertura INTEGRAL conta só o que é avaliado com o
/// critério da referência; a PARCIAL é informada à parte; a soma das duas é "com alguma avaliação automatizada" e
/// nunca é apresentada como cobertura completa.
/// </summary>
public sealed record KnightReferenceCoverageGroup(
    string Key,
    string Label,
    int Total,
    int Implemented,
    int Partial,
    int Pending,
    int ManualOnly,
    int RequiresAccess,
    int ApiLimitation)
{
    /// <summary>Percentual avaliado INTEGRALMENTE (critério da referência).</summary>
    public double FullPercent => Percent(Implemented);

    /// <summary>Percentual avaliado com critério equivalente, mas não idêntico.</summary>
    public double PartialPercent => Percent(Partial);

    /// <summary>Percentual com ALGUMA avaliação automatizada (integral + parcial) — rótulo próprio, nunca "cobertura completa".</summary>
    public double AnyAutomatedPercent => Percent(Implemented + Partial);

    private double Percent(int n) =>
        Total == 0 ? 0 : Math.Round(100.0 * n / Total, 1, MidpointRounding.AwayFromZero);
}

/// <summary>Um benchmark do catálogo de referência e a versão fixada.</summary>
public sealed record KnightReferenceFramework(string Name, string Version, int Controls);

/// <summary>Cobertura de IMPLEMENTAÇÃO do catálogo de referência numa versão do catálogo KNIGHT.</summary>
public sealed record KnightReferenceCoverage(
    string CatalogVersion,
    string ReferenceCommit,
    IReadOnlyList<KnightReferenceFramework> Frameworks,
    KnightReferenceCoverageGroup Total,
    IReadOnlyList<KnightReferenceCoverageGroup> ByPlatform,
    IReadOnlyList<KnightReferenceCoverageGroup> ByService,
    IReadOnlyList<KnightReferenceStatus> Controls);

public static class KnightReferenceCatalog
{
    private const string ResourceName = "AegisScore.Knight.ReferenceCatalog.json";

    private static readonly Lazy<Loaded> Data = new(Load);

    private sealed record Loaded(string Commit, IReadOnlyList<KnightReferenceControl> Controls);

    /// <summary>Commit do repositório de referência inspecionado para montar a lista (rastreabilidade).</summary>
    public static string ReferenceCommit => Data.Value.Commit;

    /// <summary>Todos os controles de referência, na ordem estável do arquivo.</summary>
    public static IReadOnlyList<KnightReferenceControl> Controls => Data.Value.Controls;

    public static KnightReferenceControl? Find(string key) =>
        Controls.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Cobertura de implementação para o catálogo KNIGHT ATUAL. As referências citadas pelos controles decidem o
    /// que está implementado; as disposições declaradas explicam o que não está (manual, outro acesso). Vínculo
    /// a uma chave inexistente é defeito de catálogo e lança — não pode inflar nem esconder a cobertura.
    /// </summary>
    public static KnightReferenceCoverage Coverage() => Coverage(KnightCatalog.Indicators);

    public static KnightReferenceCoverage Coverage(IEnumerable<KnightIndicatorDefinition> indicators)
    {
        var links = new Dictionary<string, List<(string IndicatorId, KnightReferenceLink Link)>>(StringComparer.Ordinal);
        var inactive = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var def in indicators)
        {
            var active = KnightCollectorCapabilities.IsActive(def);
            foreach (var link in def.References)
            {
                if (Find(link.Key) is null)
                    throw new InvalidOperationException(
                        $"O controle {def.Id} cita a referência {link.Key}, que não existe no catálogo de referência.");
                if (!active)
                {
                    if (!inactive.TryGetValue(link.Key, out var ids)) inactive[link.Key] = ids = new();
                    ids.Add(def.Id);
                    continue;
                }
                if (!links.TryGetValue(link.Key, out var list)) links[link.Key] = list = new();
                list.Add((def.Id, link));
            }
        }

        var statuses = new List<KnightReferenceStatus>(Controls.Count);
        foreach (var c in Controls)
        {
            if (links.TryGetValue(c.Key, out var byIndicator))
            {
                var ids = byIndicator.Select(x => x.IndicatorId).Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                var exact = byIndicator.Any(x => x.Link.Match == KnightReferenceMatch.Exact);
                var note = exact
                    ? byIndicator.Select(x => x.Link.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    : string.Join(" ", byIndicator.Select(x => x.Link.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                statuses.Add(new KnightReferenceStatus(c,
                    exact ? KnightReferenceDisposition.Implemented : KnightReferenceDisposition.Partial,
                    ids, string.IsNullOrWhiteSpace(note) ? null : note));
                continue;
            }

            if (inactive.TryGetValue(c.Key, out var declaredBy))
            {
                statuses.Add(new KnightReferenceStatus(c, KnightReferenceDisposition.Pending, Array.Empty<string>(),
                    $"Regra declarada em {string.Join(", ", declaredBy)}, mas fora do fluxo ativo: a coleta que ela consome não é produzida por nenhum conector real."));
                continue;
            }

            var declared = KnightReferenceDispositions.For(c.Key);
            statuses.Add(new KnightReferenceStatus(c,
                declared?.Disposition ?? KnightReferenceDisposition.Pending,
                Array.Empty<string>(),
                declared?.Note ?? KnightReferenceDispositions.PendingNote(c.Service)));
        }

        KnightReferenceCoverageGroup Group(string key, string label, IEnumerable<KnightReferenceStatus> items)
        {
            var list = items.ToList();
            int N(KnightReferenceDisposition d) => list.Count(s => s.Disposition == d);
            return new KnightReferenceCoverageGroup(key, label, list.Count,
                N(KnightReferenceDisposition.Implemented), N(KnightReferenceDisposition.Partial),
                N(KnightReferenceDisposition.Pending), N(KnightReferenceDisposition.ManualOnly),
                N(KnightReferenceDisposition.RequiresAccess), N(KnightReferenceDisposition.ApiLimitation));
        }

        var byService = statuses
            .GroupBy(s => s.Control.Service)
            .OrderBy(g => (int)g.Key)
            .Select(g => Group(g.Key.ToString(), KnightServices.Describe(g.Key)?.Label ?? g.Key.ToString(), g))
            .ToList();

        var byPlatform = statuses
            .GroupBy(s => KnightServices.Describe(s.Control.Service)?.Platform ?? KnightPlatform.Demo)
            .OrderBy(g => (int)g.Key)
            .Select(g => Group(g.Key.ToString(), KnightServices.PlatformLabel(g.Key), g))
            .ToList();

        var frameworks = Controls
            .GroupBy(c => (c.Framework, c.Version))
            .Select(g => new KnightReferenceFramework(g.Key.Framework, g.Key.Version, g.Count()))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        return new KnightReferenceCoverage(
            KnightCatalog.Version, ReferenceCommit, frameworks,
            Group("Total", "Catálogo de referência", statuses), byPlatform, byService, statuses);
    }

    /// <summary>Rótulo da disposição, o mesmo em tela, CSV, HTML e PDF.</summary>
    public static string DispositionLabel(KnightReferenceDisposition d) => d switch
    {
        KnightReferenceDisposition.Implemented => "Implementado integralmente",
        KnightReferenceDisposition.Partial => "Implementado parcialmente",
        KnightReferenceDisposition.Pending => "Pendente",
        KnightReferenceDisposition.ManualOnly => "Verificação manual",
        KnightReferenceDisposition.RequiresAccess => "Exige acesso que o conector não tem",
        KnightReferenceDisposition.ApiLimitation => "Limitação da API oficial",
        _ => d.ToString(),
    };

    /// <summary>Rótulo curto de uma referência para exibição: "CIS Microsoft 365 Foundations 7.0.0 · 5.2.2.1".</summary>
    public static string Label(KnightReferenceControl c) =>
        $"{c.Framework} {c.Version} · {c.Section ?? "sem seção"}" + (c.Variant is null ? "" : $" ({c.Variant})");

    // ---- Carga ---------------------------------------------------------------------------------------

    private sealed class FileDto
    {
        [JsonPropertyName("referenceCommit")] public string? ReferenceCommit { get; set; }
        [JsonPropertyName("controls")] public List<ControlDto>? Controls { get; set; }
    }

    private sealed class ControlDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("framework")] public string? Framework { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("section")] public string? Section { get; set; }
        [JsonPropertyName("variant")] public string? Variant { get; set; }
        [JsonPropertyName("service")] public string? Service { get; set; }
        [JsonPropertyName("severity")] public string? Severity { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    private static Loaded Load()
    {
        using var stream = typeof(KnightReferenceCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Recurso {ResourceName} ausente do assembly.");
        var file = JsonSerializer.Deserialize<FileDto>(stream)
            ?? throw new InvalidOperationException("Catálogo de referência ilegível.");

        var controls = new List<KnightReferenceControl>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in file.Controls ?? new List<ControlDto>())
        {
            if (string.IsNullOrWhiteSpace(c.Key) || !keys.Add(c.Key))
                throw new InvalidOperationException($"Chave de referência vazia ou duplicada: '{c.Key}'.");
            if (!Enum.TryParse<KnightService>(c.Service, out var service))
                throw new InvalidOperationException($"Serviço desconhecido na referência {c.Key}: '{c.Service}'.");
            if (!Enum.TryParse<SeverityLevel>(c.Severity, out var severity))
                throw new InvalidOperationException($"Severidade desconhecida na referência {c.Key}: '{c.Severity}'.");
            controls.Add(new KnightReferenceControl(
                c.Key, c.Framework ?? "", c.Version ?? "", c.Section, c.Variant, service, severity, c.Title ?? c.Key));
        }

        return new Loaded(file.ReferenceCommit ?? "", controls);
    }
}
