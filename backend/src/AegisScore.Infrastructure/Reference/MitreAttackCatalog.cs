using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Services;

namespace AegisScore.Infrastructure.Reference;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Carrega o catálogo compacto MITRE ATT&CK Enterprise v17.1 de
/// <c>mitre_attack_enterprise_v17_1.json</c> UMA vez (singleton) — reference data derivado do STIX OFICIAL
/// versionado (ver <c>scripts/mitre/generate_mitre_catalog.py</c>), NUNCA de blog/tabela autoral/IA. Runtime e
/// testes NÃO dependem de internet: só leem este artefato commitado.
///
/// ⚠️ FAIL-CLOSED (mesmo idioma do <see cref="ControlLanguageCatalog"/>): ausência do arquivo, JSON inválido,
/// versão diferente de 17.1, lista vazia, técnica sem ID/nome válido ou tática malformada ABORTAM o carregamento —
/// sem catálogo, a validação MITRE (a autoridade que impede mapeamento inventado) não teria como funcionar.
///
/// <see cref="GetTechnique"/> devolve SOMENTE técnicas CORRENTES (não revogadas, não deprecadas): a matriz do
/// Google SecOps usa a versão corrente, então um ID revogado/deprecado é tratado como mapeamento INVÁLIDO.
/// </summary>
public sealed class MitreAttackCatalog : IMitreAttackCatalog
{
    private const string ExpectedAttackVersion = "17.1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // Tradução DETERMINÍSTICA (pt-BR) das 14 táticas Enterprise — tabela fixa de referência, sem IA por render.
    private static readonly IReadOnlyDictionary<string, string> TacticNamePt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TA0043"] = "Reconhecimento",
        ["TA0042"] = "Desenvolvimento de Recursos",
        ["TA0001"] = "Acesso Inicial",
        ["TA0002"] = "Execução",
        ["TA0003"] = "Persistência",
        ["TA0004"] = "Escalonamento de Privilégios",
        ["TA0005"] = "Evasão de Defesas",
        ["TA0006"] = "Acesso a Credenciais",
        ["TA0007"] = "Descoberta",
        ["TA0008"] = "Movimentação Lateral",
        ["TA0009"] = "Coleta",
        ["TA0011"] = "Comando e Controle",
        ["TA0010"] = "Exfiltração",
        ["TA0040"] = "Impacto",
    };

    private readonly IReadOnlyDictionary<string, MitreTechnique> _techniques;
    private readonly IReadOnlyDictionary<string, MitreTactic> _tactics;

    public string AttackVersion { get; }
    public string DisplayLabel { get; }
    public int ActiveTechniqueCount => _techniques.Count;

    public MitreAttackCatalog(string path, ILogger<MitreAttackCatalog> logger)
    {
        var (version, techniques, tactics) = Load(path, logger);
        AttackVersion = version;
        _techniques = techniques;
        _tactics = tactics;
        DisplayLabel =
            $"MITRE ATT&CK Enterprise v{version} — alinhado à versão 17 suportada pelo Google SecOps.";
    }

    public MitreTechnique? GetTechnique(string? techniqueId)
    {
        if (string.IsNullOrWhiteSpace(techniqueId)) return null;
        return _techniques.TryGetValue(techniqueId.Trim().ToUpperInvariant(), out var t) ? t : null;
    }

    public MitreTactic? GetTactic(string? tacticId)
    {
        if (string.IsNullOrWhiteSpace(tacticId)) return null;
        return _tactics.TryGetValue(tacticId.Trim().ToUpperInvariant(), out var t) ? t : null;
    }

    private static (string Version,
        IReadOnlyDictionary<string, MitreTechnique> Techniques,
        IReadOnlyDictionary<string, MitreTactic> Tactics) Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Catálogo MITRE ATT&CK não encontrado em '{path}'. Ele é obrigatório: sem ele a validação de " +
                "técnicas (que impede mapeamento MITRE inventado) não funcionaria. Verifique se o Data/ do projeto " +
                "da API foi copiado para o output/imagem.", path);

        MitreCatalogFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize<MitreCatalogFileJson>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' está malformado (JSON inválido).", ex);
        }

        var version = file?.Provenance?.AttackVersion?.Trim();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' sem 'provenance.attackVersion'.");
        if (!string.Equals(version, ExpectedAttackVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Catálogo MITRE ATT&CK em '{path}' é v{version}, mas este pacote fixa v{ExpectedAttackVersion} " +
                "(alinhada à v17 do Google SecOps). Não use silenciosamente outra versão.");

        if (file!.Techniques is null || file.Techniques.Count == 0)
            throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' sem técnicas (lista 'techniques' vazia).");
        if (file.Tactics is null || file.Tactics.Count == 0)
            throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' sem táticas (lista 'tactics' vazia).");

        var tactics = new Dictionary<string, MitreTactic>(StringComparer.Ordinal);
        foreach (var t in file.Tactics)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
                throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' com tática sem id/nome.");
            var id = t.Id.Trim().ToUpperInvariant();
            var namePt = TacticNamePt.TryGetValue(id, out var pt) ? pt : t.Name!.Trim();
            if (!tactics.TryAdd(id, new MitreTactic(id, t.ShortName?.Trim() ?? "", t.Name!.Trim(), namePt)))
                throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' com tática DUPLICADA: '{id}'.");
        }

        var techniques = new Dictionary<string, MitreTechnique>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var t in file.Techniques)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
                throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' com técnica sem id/nome.");
            // Só técnicas CORRENTES entram na busca: revogada/deprecada = mapeamento inválido (fora da matriz corrente).
            if (t.Revoked || t.Deprecated) { skipped++; continue; }
            var id = t.Id.Trim().ToUpperInvariant();
            var tacticIds = (t.Tactics ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var parent = string.IsNullOrWhiteSpace(t.Parent) ? null : t.Parent!.Trim().ToUpperInvariant();
            if (!techniques.TryAdd(id, new MitreTechnique(id, t.Name!.Trim(), t.IsSubtechnique, parent, tacticIds)))
                throw new InvalidOperationException($"Catálogo MITRE ATT&CK em '{path}' com técnica DUPLICADA: '{id}'.");
        }

        logger.LogInformation(
            "Catálogo MITRE ATT&CK v{Version} carregado de '{Path}': {Techniques} técnicas correntes ({Skipped} revogadas/deprecadas ignoradas), {Tactics} táticas.",
            version, path, techniques.Count, skipped, tactics.Count);

        return (version, techniques, tactics);
    }

    // ---- Forma crua do artefato (proveniência ignorada aqui exceto a versão; técnicas + táticas) ----
    private sealed record MitreCatalogFileJson(
        MitreProvenanceJson? Provenance,
        IReadOnlyList<MitreTacticJson>? Tactics,
        IReadOnlyList<MitreTechniqueJson>? Techniques);

    private sealed record MitreProvenanceJson(string? AttackVersion, string? ContentSha256, string? SourceSha256);

    private sealed record MitreTacticJson(string? Id, string? ShortName, string? Name);

    private sealed record MitreTechniqueJson(
        string? Id, string? Name, bool IsSubtechnique, string? Parent,
        List<string>? Tactics, bool Revoked, bool Deprecated);
}
