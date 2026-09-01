using System.Collections.Generic;

namespace AegisScore.Application.Services;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Catálogo MITRE ATT&CK Enterprise FIXADO na v17.1 (alinhado à versão 17 suportada
/// pelo Google SecOps), carregado UMA vez de um artefato de referência derivado do STIX OFICIAL versionado — NUNCA
/// de blog, tabela autoral ou IA. É a ÚNICA autoridade de nome, hierarquia (técnica/subtécnica) e táticas de uma
/// técnica; a IA não cria, altera nem infere mapeamento MITRE. A versão é registrada explicitamente e nunca troca
/// silenciosamente para a versão global mais recente do MITRE.
/// </summary>
public interface IMitreAttackCatalog
{
    /// <summary>Versão do ATT&CK Enterprise (ex.: "17.1").</summary>
    string AttackVersion { get; }

    /// <summary>Rótulo estável para a UI/relatórios (v17.1 alinhada à v17 do Google SecOps).</summary>
    string DisplayLabel { get; }

    /// <summary>Quantidade de técnicas CORRENTES (não revogadas/deprecadas) do catálogo.</summary>
    int ActiveTechniqueCount { get; }

    /// <summary>
    /// Resolve uma técnica CORRENTE e VÁLIDA (existe no catálogo, não revogada, não deprecada). Devolve <c>null</c>
    /// para ID desconhecido, revogado ou deprecado — um mapeamento assim é INVÁLIDO (diagnóstico), nunca inventado.
    /// O ID é normalizado (caixa/espaços) antes da busca.
    /// </summary>
    MitreTechnique? GetTechnique(string? techniqueId);

    /// <summary>Resolve uma tática por ID (ex.: "TA0002"). Null quando desconhecida.</summary>
    MitreTactic? GetTactic(string? tacticId);
}

/// <summary>Uma técnica/subtécnica MITRE do catálogo fixado. Táticas = IDs (TA####) relacionados pela matriz oficial.</summary>
public sealed record MitreTechnique(
    string Id, string Name, bool IsSubtechnique, string? ParentId, IReadOnlyList<string> TacticIds);

/// <summary>Uma tática MITRE do catálogo, com nome oficial (en) e tradução determinística (pt-BR) para a UI.</summary>
public sealed record MitreTactic(string Id, string ShortName, string Name, string NamePt);
