using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Catálogo ÚNICO das fontes do KNIGHT
// ============================================================================
// Até o bloco anterior, cada superfície (sincronização, consolidado, nomes aceitos na rota, rótulos do relatório)
// tinha a própria lista de três fontes. Com o restante do Microsoft 365 e o Azure, listas paralelas divergiriam na
// primeira fonte esquecida — e uma fonte esquecida no consolidado é uma avaliação que some do relatório sem aviso.
// Este catálogo é a autoridade: rótulo, apelido de rota, provedor, se é alimentada pelo conector Microsoft e se
// entra no relatório consolidado. A ORDEM é a de apresentação em toda superfície.

/// <summary>Descrição estável de uma fonte do KNIGHT.</summary>
/// <param name="Slug">Apelido curto usado nas rotas e na tela.</param>
/// <param name="Aliases">Outros nomes aceitos de fora (rota, query string, corpo), sem distinção de caixa.</param>
/// <param name="MicrosoftConnector">A fonte é alimentada pelo conector Microsoft de postura de identidade (mesmo registro de aplicação).</param>
/// <param name="Consolidable">A fonte pode compor o relatório consolidado.</param>
public sealed record KnightSourceDescriptor(
    KnightSourceType Source,
    string Label,
    string Slug,
    string Provider,
    IReadOnlyList<string> Aliases,
    bool MicrosoftConnector,
    bool Consolidable);

public static class KnightSourceCatalog
{
    private static KnightSourceDescriptor Ms(KnightSourceType s, string label, string slug, params string[] aliases) =>
        new(s, label, slug, "Microsoft", aliases, MicrosoftConnector: true, Consolidable: true);

    /// <summary>Todas as fontes, na ordem de apresentação.</summary>
    public static IReadOnlyList<KnightSourceDescriptor> All { get; } = new[]
    {
        Ms(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "entra", "entraid", "microsoftentraid"),
        Ms(KnightSourceType.MicrosoftTeams, "Microsoft Teams", "teams", "microsoftteams"),
        Ms(KnightSourceType.MicrosoftExchangeOnline, "Exchange Online", "exchange", "exchangeonline", "microsoftexchangeonline"),
        Ms(KnightSourceType.MicrosoftDefenderForOffice365, "Microsoft Defender para Office 365", "defender-office365",
            "defenderforoffice365", "microsoftdefenderforoffice365", "defender-o365", "mdo"),
        Ms(KnightSourceType.MicrosoftPurview, "Microsoft Purview", "purview", "microsoftpurview"),
        Ms(KnightSourceType.MicrosoftSharePoint, "SharePoint e OneDrive", "sharepoint", "microsoftsharepoint", "onedrive"),
        Ms(KnightSourceType.MicrosoftIntune, "Microsoft Intune", "intune", "microsoftintune"),
        Ms(KnightSourceType.MicrosoftFabric, "Microsoft Fabric (Power BI)", "fabric", "microsoftfabric", "powerbi"),
        // Azure: mesma aplicação registrada, outra autorização — papel Leitor do Azure RBAC nas assinaturas do escopo.
        Ms(KnightSourceType.MicrosoftAzure, "Microsoft Azure", "azure", "microsoftazure"),
        new(KnightSourceType.GoogleWorkspace, "Google Workspace", "google", "Google", new[] { "googleworkspace" },
            MicrosoftConnector: false, Consolidable: false),
        new(KnightSourceType.Demo, "Demonstração (sintético)", "demo", "Demonstração", Array.Empty<string>(),
            MicrosoftConnector: false, Consolidable: false),
    };

    private static readonly IReadOnlyDictionary<KnightSourceType, KnightSourceDescriptor> BySource =
        All.ToDictionary(d => d.Source);

    public static KnightSourceDescriptor? Describe(KnightSourceType source) =>
        BySource.TryGetValue(source, out var d) ? d : null;

    /// <summary>Rótulo estável da fonte (mesmo quando ela nunca produziu avaliação).</summary>
    public static string Label(KnightSourceType source) =>
        Describe(source)?.Label ?? (source == KnightSourceType.Consolidated ? "Consolidado" : source.ToString());

    public static string Slug(KnightSourceType source) =>
        Describe(source)?.Slug ?? source.ToString().ToLowerInvariant();

    /// <summary>Fontes alimentadas pelo conector Microsoft de postura de identidade (a principal primeiro).</summary>
    public static IReadOnlyList<KnightSourceType> MicrosoftConnectorSources { get; } =
        All.Where(d => d.MicrosoftConnector).Select(d => d.Source).ToList();

    /// <summary>Fontes candidatas do relatório consolidado, na ordem de apresentação.</summary>
    public static IReadOnlyList<KnightSourceType> ConsolidationCandidates { get; } =
        All.Where(d => d.Consolidable).Select(d => d.Source).ToList();

    /// <summary>Resolve um nome vindo de fora: enum, apelido ou sinônimo. Desconhecido → <c>false</c>.</summary>
    public static bool TryParse(string? value, out KnightSourceType source)
    {
        var wanted = (value ?? "").Trim();
        foreach (var d in All)
        {
            if (d.Source.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || d.Slug.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || d.Aliases.Any(a => a.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                source = d.Source;
                return true;
            }
        }
        source = default;
        return false;
    }
}
