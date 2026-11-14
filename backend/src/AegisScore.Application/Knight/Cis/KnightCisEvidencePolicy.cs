using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Cis;

// ============================================================================
//  [AEGIS-KNIGHT-CIS-01] O que cada resultado de uma regra PARCIAL prova contra o critério oficial
// ============================================================================
// Um vínculo integral (Exact) avalia o critério da recomendação: aprovado é conforme, reprovado é não conforme. Um vínculo
// PARCIAL avalia um critério equivalente, mas não idêntico — e cada desfecho dele precisa ser lido à parte:
//   • aprovado só sustenta "Conforme" quando a parte observada é MAIS estrita que o critério ou o cobre (ex.: a regra exige
//     a configuração em todos os usuários e a recomendação aceita um subconjunto);
//   • reprovado só sustenta "Não conforme" quando a parte observada, por si só, prova a violação (ex.: um recurso sem log
//     viola "todos os recursos com log", mesmo que a regra não veja todos os tipos de recurso);
//   • não aplicável só sustenta "Não aplicável" quando a regra diz que o próprio critério não se aplica ao ambiente.
// Nada aqui é padrão silencioso: todo vínculo parcial do catálogo ativo tem uma decisão escrita (os testes exigem), e uma
// decisão "não prova" deixa a recomendação NÃO AVALIADA, com o motivo.

/// <summary>O que um desfecho de uma regra parcial prova contra o critério oficial da recomendação.</summary>
public sealed record KnightCisPartialProof(bool PassProves, bool FailProves, bool NotApplicableProves, string Rationale);

/// <summary>Como as variantes do catálogo de uma recomendação se combinam (decisão explícita por recomendação).</summary>
public enum KnightCisVariantKind
{
    /// <summary>Cada variante é uma parte necessária e, juntas, cobrem o critério (tipos de recurso, portas, papéis listados).</summary>
    Exhaustive = 0,

    /// <summary>As variantes são partes necessárias, mas não cobrem todo o critério: violação prova; conformidade não.</summary>
    NotExhaustive = 1,

    /// <summary>A fonte de rastreio atribui critérios diferentes à mesma seção: a identificação não é confirmada.</summary>
    IdentificationConflict = 2,
}

public sealed record KnightCisVariantGroup(KnightCisVariantKind Kind, string Rationale);

public static class KnightCisEvidencePolicy
{
    /// <summary>
    /// Versões do catálogo KNIGHT cujos vínculos regra → recomendação foram REVISADOS para esta visão, fixadas aqui de forma
    /// explícita. Subir o catálogo não aprova a versão nova: ela entra nesta lista só depois da revisão dos vínculos. Uma avaliação
    /// executada com outra versão não é projetada: as recomendações que dependem dela ficam não avaliadas, com o motivo.
    /// </summary>
    public static IReadOnlyList<string> MappingCatalogVersions { get; } = new[] { "ak-knight-v13" };

    public static bool MappingReliableFor(string? runCatalogVersion) =>
        runCatalogVersion is not null && MappingCatalogVersions.Contains(runCatalogVersion, StringComparer.Ordinal);

    private const string M365 = "CIS-M365-7.0.0:";
    private const string Az = "CIS-AZ-6.0.0:";
    private const string Azc = "CIS-AZC-2.0.0:";
    private const string Azd = "CIS-AZD-2.0.0:";

    private static KnightCisPartialProof P(bool pass, bool fail, bool na, string why) => new(pass, fail, na, why);

    private static readonly IReadOnlyDictionary<string, KnightCisPartialProof> Partial = new Dictionary<string, KnightCisPartialProof>(StringComparer.Ordinal)
    {
        // ---- Microsoft Entra ID e Microsoft 365 ---------------------------------------------------------------
        [Key(M365 + "5.2.3.4", "AK-ENTRA-006")] = P(false, true, false,
            "A regra reprova abaixo de 90% de usuários com MFA registrada: isso já prova que nem todos são capazes de MFA. Acima do limiar não prova que TODOS são."),
        [Key(M365 + "5.2.2.1", "AK-ENTRA-008")] = P(true, false, false,
            "A regra verifica todos os papéis de diretório ativos, mais do que o conjunto da recomendação: aprovada, cobre os papéis listados. Uma reprovação pode estar num papel fora da lista."),
        [Key(M365 + "5.2.2.2", "AK-ENTRA-014")] = P(false, true, false,
            "Sem security defaults e sem política de MFA para todos, a recomendação é violada. A aprovação pode vir de security defaults, que não é a política de acesso condicional que a recomendação pede."),
        [Key(Az + "5.1.1", "AK-ENTRA-014")] = P(false, true, false,
            "Sem security defaults e sem acesso condicional equivalente, a recomendação é violada. A aprovação pode vir de acesso condicional, e a recomendação trata de security defaults."),
        [Key(M365 + "5.3.1", "AK-ENTRA-050")] = P(false, false, false,
            "O conjunto de papéis examinado é o da documentação da Microsoft, não necessariamente o da recomendação; contas de emergência aparecem para decisão humana. Nenhum desfecho conclui sozinho."),
        [Key(M365 + "5.3.2", "AK-ENTRA-053")] = P(false, true, false,
            "Sem revisão de acesso dos convidados, a recomendação é violada. A aprovação considera o alcance documentado da configuração (sem grupos dinâmicos e atribuíveis a papéis), que não comprova sozinho todo o critério."),
        [Key(Az + "5.3.2", "AK-ENTRA-053")] = P(false, true, false,
            "A regra aceita recorrência mensal e a recomendação do Azure pede ao menos quinzenal: reprovada, viola as duas; aprovada, não prova a recorrência pedida."),
        [Key(M365 + "5.2.2.16", "AK-ENTRA-070")] = P(true, false, false,
            "A regra exige a proteção de token para todos os usuários, mais do que a recomendação (usuários e plataformas escolhidos pela organização): aprovada, cobre o critério; reprovada, não prova a violação."),
        [Key(M365 + "7.2.2", "AK-SPO-002")] = P(false, false, true, SpoOnlyWhenOff),
        [Key(M365 + "7.2.8", "AK-SPO-008")] = P(false, false, true, SpoOnlyWhenOff),
        [Key(M365 + "7.2.9", "AK-SPO-009")] = P(false, false, true, SpoOnlyWhenOff),
        [Key(M365 + "7.2.10", "AK-SPO-010")] = P(false, false, true, SpoOnlyWhenOff),
        [Key(M365 + "7.2.4", "AK-SPO-004")] = P(true, false, false,
            "Pelo Microsoft Graph, a regra só aprova quando o SharePoint está restrito à organização, caso em que o OneDrive também está. Nos demais níveis ela não conclui."),

        // ---- Composições (cada parte é necessária; ver KnightReferenceDispositions) ---------------------------
        [Key(M365 + "1.3.2", "AK-ENTRA-069")] = P(false, true, false, Part),
        [Key(M365 + "1.3.2", "AK-ENTRA-075")] = P(false, true, false, Part),
        [Key(M365 + "8.6.1", "AK-TEAMS-017")] = P(false, true, false, Part),
        [Key(M365 + "8.6.1", "AK-MDO-019")] = P(false, true, false, Part),
        [Key(M365 + "5.2.3.5", "AK-ENTRA-029")] = P(false, true, false, Part),
        [Key(M365 + "5.2.3.5", "AK-ENTRA-031")] = P(false, true, false, Part),

        // ---- Azure ---------------------------------------------------------------------------------------------
        [Key(Az + "6.1.4", "AK-AZ-MON-020")] = P(false, true, false,
            "Um recurso sem configuração de diagnóstico viola a recomendação. A regra só lê os tipos de recurso que o KNIGHT coleta: aprovada, não prova os demais tipos."),
        [Key(Azc + "2.5", "AK-AZ-APP-027")] = P(false, false, false,
            "A regra confere as configurações que já referenciam o Key Vault; se OUTRAS configurações guardam segredo em texto não é visível sem ler os valores. Nenhum desfecho conclui sozinho."),
        [Key(Az + "2.1.5", "AK-AZ-DBR-008")] = P(true, false, false,
            "O Unity Catalog habilitado aprova; desabilitado ou ausente fica desconhecido, porque a atribuição pelo console da conta pode não aparecer na propriedade lida."),
        [Key(Az + "2.1.4", "AK-AZ-DBR-010")] = P(true, false, false,
            "Todos os usuários e grupos com vínculo externo do Entra ID cobrem o critério. Uma conta local pode ser legítima (de serviço) e fica para decisão humana."),
        [Key(Az + "2.1.3", "AK-AZ-DBR-011")] = P(true, true, false,
            "A configuração Spark lida em cada cluster decide; o cluster que depende de script de inicialização fica não avaliado pela própria regra."),
        [Key(Az + "5.3.3", "AK-AZ-IAM-001")] = P(false, true, false,
            "A atribuição de Administrador de Acesso do Usuário no escopo raiz viola a recomendação. Atribuições em grupos de gerenciamento sem assinatura no escopo não são vistas: aprovada, não prova."),
        [Key(Az + "6.1.1.8", "AK-AZ-MON-004")] = P(false, true, false,
            "Faltar uma das categorias exigidas pela regra já viola a recomendação. As categorias de risco dependem de licença e ficam como evidência: aprovada, não prova todas."),
        [Key(Az + "8.1.16", "AK-AZ-MDC-018")] = P(true, false, false,
            "O workspace do Defender EASM encontrado cobre o critério. Sem ele na assinatura, o locatário pode concentrar o EASM numa assinatura fora do escopo."),
        [Key(Az + "8.4.1", "AK-AZ-NET-020")] = P(true, false, false,
            "O Bastion encontrado cobre o critério. Sem ele, um Bastion centralizado numa rede emparelhada de outra assinatura não é considerado."),
        [Key(Azc + "2.1.17", "AK-AZ-APP-025")] = P(true, false, false,
            "As zonas DNS privadas ligadas aos endpoints cobrem o critério. Um servidor DNS próprio, também aceito, não é visível pelo Resource Manager."),
        [Key(Azc + "20.7", "AK-AZ-CMP-013")] = P(true, false, false,
            "As extensões de proteção reconhecidas cobrem o critério. Uma solução de terceiros sem extensão não é visível."),
        [Key(Azd + "2.1#redis-enterprise", "AK-AZ-DB-013")] = P(true, false, false,
            "Sem chaves de acesso, o Redis gerenciado só autentica pelo Entra ID: aprovada, cobre o critério. Aceitar também as chaves não prova que o Entra ID está desligado."),
        [Key(Azd + "3.5", "AK-AZ-DB-019")] = P(true, false, false,
            "A regra exige a chave do cliente em todas as contas: aprovada, cobre as que guardam dados críticos. Quais contas são críticas é decisão organizacional."),
        [Key(Az + "2.1.8", "AK-AZ-DBR-003")] = P(false, false, false,
            "A regra aceita a chave do cliente nos serviços gerenciados OU no DBFS: não mostra onde estão os dados críticos do workspace nem se a superfície que os guarda está coberta. Nenhum desfecho conclui sozinho — é preciso confirmar o escopo dos dados críticos."),
    };

    private const string SpoOnlyWhenOff =
        "Pelo Microsoft Graph, o critério só se conclui quando o compartilhamento externo está desligado — e então ele não se aplica. "
        + "Nos demais casos a leitura depende da API administrativa do SharePoint, que exige permissão de escrita e o AEGIS não usa.";

    private const string Part =
        "Parte necessária de um critério composto: reprovada, prova a violação; aprovada, só vale junto das demais partes.";

    private static string Key(string referenceKey, string indicatorId) => referenceKey + "|" + indicatorId;

    /// <summary>A decisão escrita para um vínculo parcial, ou nula quando não há (nenhum desfecho prova).</summary>
    public static KnightCisPartialProof? PartialProofOf(string referenceKey, string indicatorId) =>
        Partial.TryGetValue(Key(referenceKey, indicatorId), out var p) ? p : null;

    public static IEnumerable<(string ReferenceKey, string IndicatorId)> DeclaredPartialLinks =>
        Partial.Keys.Select(k => { var i = k.IndexOf('|'); return (k[..i], k[(i + 1)..]); });

    /// <summary>
    /// Recomendações com mais de uma variante no catálogo e como elas se combinam. Toda recomendação com variantes tem decisão
    /// escrita (os testes exigem), para que uma variante nova não mude o veredito em silêncio.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, KnightCisVariantGroup> Groups = new Dictionary<string, KnightCisVariantGroup>(StringComparer.Ordinal)
    {
        [Az + "7.3"] = new(KnightCisVariantKind.NotExhaustive,
            "As variantes conferem as portas UDP de amplificação mais exploradas (DNS, NTP, SNMP, CLDAP e SSDP). Uma delas aberta à Internet "
            + "já viola a recomendação; todas fechadas não provam que nenhum outro acesso UDP está aberto."),
        [Az + "7.4"] = new(KnightCisVariantKind.Exhaustive, "HTTP (80) e HTTPS (443) são as duas partes do critério."),
        [Azc + "2.4.11"] = new(KnightCisVariantKind.IdentificationConflict,
            "A fonte de rastreio atribui à seção 2.4.11 dois critérios diferentes para slots de função (certificado de cliente e "
            + "identidade gerenciada). Sem a edição oficial, o AEGIS não decide qual é o da recomendação; os dois resultados ficam "
            + "visíveis nas regras do KNIGHT."),
        [Azc + "2.1.12"] = new(KnightCisVariantKind.Exhaustive, "O mesmo critério de autenticação, no aplicativo e nos slots do aplicativo."),
        [Azd + "2.1"] = new(KnightCisVariantKind.Exhaustive, Redis),
        [Azd + "2.3"] = new(KnightCisVariantKind.Exhaustive, Redis),
        [Azd + "2.5"] = new(KnightCisVariantKind.Exhaustive, Redis),
        [Azd + "2.6"] = new(KnightCisVariantKind.Exhaustive, Redis),
        [Azd + "2.7"] = new(KnightCisVariantKind.Exhaustive, Redis),
        [Azd + "2.9"] = new(KnightCisVariantKind.Exhaustive, Redis),
        [Azd + "9.3"] = new(KnightCisVariantKind.Exhaustive,
            "As duas formas de abrir o firewall do SQL a qualquer origem (a faixa inteira e a regra de serviços do Azure) são as partes do critério."),
        [Azd + "9.4"] = new(KnightCisVariantKind.Exhaustive, Sql),
        [Azd + "9.6"] = new(KnightCisVariantKind.Exhaustive, Sql),
        [Azd + "9.8"] = new(KnightCisVariantKind.Exhaustive, Sql),
        [M365 + "5.3.3"] = new(KnightCisVariantKind.Exhaustive,
            "Uma variante por papel privilegiado que a fonte de rastreio lista para a recomendação; cada papel é uma parte necessária."),
    };

    private const string Redis = "O mesmo critério no Azure Cache for Redis e no Redis gerenciado (Enterprise), como a fonte de rastreio mapeia.";
    private const string Sql = "O mesmo critério no banco SQL e na instância gerenciada, como a fonte de rastreio mapeia.";

    /// <summary>Decisão sobre as variantes de uma recomendação ("CIS-AZ-6.0.0:7.3"); nula quando ela tem uma entrada só.</summary>
    public static KnightCisVariantGroup? GroupOf(string recommendationId) =>
        Groups.TryGetValue(recommendationId, out var g) ? g : null;

    public static IReadOnlyCollection<string> DeclaredGroups => (IReadOnlyCollection<string>)Groups.Keys;
}
