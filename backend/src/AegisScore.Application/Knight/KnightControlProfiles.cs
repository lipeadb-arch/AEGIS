using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-MULTICLOUD-01] PERFIL de cada controle do catálogo KNIGHT
// ============================================================================
// O veredito continua sendo da regra determinística do catálogo. O perfil é o que o relatório precisa para
// ser lido por quem vai agir: O PROBLEMA → POR QUE IMPORTA → ONDE FOI ENCONTRADO → O QUE FAZER. Ele separa três
// eixos que a interface não pode misturar:
//   • DOMÍNIO de segurança (Identidade, IAM/RBAC, Rede, …) — o que está sendo protegido;
//   • SERVIÇO (Microsoft Entra ID, Google Drive, …) — onde a configuração vive;
//   • PROVEDOR (Microsoft, Google) — quem opera o serviço.
//
// Textos AUTORAIS do AEGIS. As referências externas são só documentação OFICIAL cujo endereço foi conferido;
// quando não há endereço comprovado, não há link. Nada aqui é copiado de outra ferramenta de assessment.

/// <summary>Domínio de segurança. Lista completa prevista; o relatório mostra só os que têm controles no escopo.</summary>
public enum KnightSecurityDomain
{
    Identity = 0,
    IamRbac = 1,
    Network = 2,
    Compute = 3,
    Storage = 4,
    Databases = 5,
    Secrets = 6,
    CloudProtection = 7,
    Logging = 8,
    Governance = 9,
    Collaboration = 10,
    DataProtection = 11,
}

/// <summary>Referência a um framework ou documentação. <paramref name="Url"/> só existe quando o endereço foi conferido.</summary>
public sealed record KnightControlReference(string Framework, string? Version, string Code, string? Url = null);

/// <summary>Perfil descritivo de um controle do catálogo.</summary>
/// <param name="Description">O PROBLEMA: o que a condição encontrada é.</param>
/// <param name="Rationale">O RISCO: o caminho que a condição abre.</param>
/// <param name="Impact">[AEGIS-KNIGHT-COVERAGE-01] O IMPACTO POTENCIAL, no limite do acesso que a condição concede (ver <see cref="KnightControlImpacts"/>).</param>
public sealed record KnightControlProfile(
    string IndicatorId,
    KnightSecurityDomain Domain,
    string Description,
    string Rationale,
    string ExpectedConfiguration,
    string? DoesNotProve,
    IReadOnlyList<KnightControlReference> Documentation,
    IReadOnlyList<KnightCapability> RequiredCapabilities,
    string? ServiceOverride = null,
    string? Impact = null);

public static class KnightControlProfiles
{
    public const string NistFramework = "NIST CSF";
    public const string NistVersion = "2.0";
    public const string MitreFramework = "MITRE ATT&CK";

    private static readonly KnightControlReference DocLegacy = new(
        "Microsoft Learn", null, "Bloquear autenticação legada com acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/policy-block-legacy-authentication");
    private static readonly KnightControlReference DocDefaults = new(
        "Microsoft Learn", null, "Security defaults do Microsoft Entra ID",
        "https://learn.microsoft.com/en-us/entra/fundamentals/security-defaults");
    private static readonly KnightControlReference DocAdminMfa = new(
        "Microsoft Learn", null, "Exigir MFA para administradores com acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/policy-old-require-mfa-admin");
    private static readonly KnightControlReference DocRoles = new(
        "Microsoft Learn", null, "Boas práticas para papéis do Microsoft Entra",
        "https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/best-practices");
    private static readonly KnightControlReference DocBreakGlass = new(
        "Microsoft Learn", null, "Contas de acesso de emergência",
        "https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/security-emergency-access");
    private static readonly KnightControlReference DocConsent = new(
        "Microsoft Learn", null, "Configurar o consentimento de usuários a aplicações",
        "https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/configure-user-consent");

    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;
    private static KnightCapability[] Caps(params KnightCapability[] c) => c;

    private static readonly IReadOnlyDictionary<string, KnightControlProfile> ById = BaseProfiles()
        .Concat(EntraConfigurationProfiles.All)
        .Select(p => p with { Impact = KnightControlImpacts.For(p.IndicatorId) })
        .ToDictionary(p => p.IndicatorId, StringComparer.Ordinal);

    private static IEnumerable<KnightControlProfile> BaseProfiles() => new[]
    {
        new KnightControlProfile("AK-ENTRA-001", KnightSecurityDomain.Identity,
            "Contas com papel privilegiado aparecem no relatório de registro do diretório sem nenhum método capaz de autenticação multifator.",
            "Uma conta administrativa que só tem senha é o caminho mais curto entre uma credencial vazada e o controle do ambiente. Sem um método de segundo fator registrado, a conta não consegue atender a uma exigência de MFA quando ela existir.",
            "Todas as contas privilegiadas com ao menos um método capaz de MFA registrado — de preferência resistente a phishing.",
            "Registro de método não comprova que uma política EXIGE MFA nem que a autenticação APLICOU o segundo fator. A exigência por política é avaliada no controle AK-ENTRA-008.",
            Docs(DocRoles), Caps(KnightCapability.PrivilegedRoleInventory, KnightCapability.MfaRegistration)),

        new KnightControlProfile("AK-ENTRA-002", KnightSecurityDomain.IamRbac,
            "O número de identidades com papel administrativo no diretório — contas de usuário, convidados, aplicações e grupos — está acima do teto de menor privilégio parametrizado no AEGIS.",
            "Cada identidade com papel administrativo amplia o que um atacante pode aproveitar e o que a auditoria precisa acompanhar. Manter o mínimo necessário reduz as duas coisas.",
            "Quantidade de identidades privilegiadas no mínimo necessário, com elevação sob demanda (just-in-time) quando disponível.",
            "O teto é um parâmetro do AEGIS; o NIST recomenda menor privilégio, mas não fixa um número. Estar na lista não significa que o acesso seja indevido.",
            Docs(DocRoles), Caps(KnightCapability.PrivilegedRoleInventory, KnightCapability.DirectoryUsers)),

        new KnightControlProfile("AK-ENTRA-003", KnightSecurityDomain.Identity,
            "Contas administrativas com caixa de correio ativa ficam expostas a phishing direcionado ao administrador.",
            "Quem lê e-mail com a mesma conta que administra o ambiente entrega ao atacante um canal direto para a identidade mais valiosa.",
            "Contas administrativas dedicadas, sem caixa de correio, separadas das contas de uso diário.",
            null, Docs(DocRoles), Caps(KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-004", KnightSecurityDomain.Identity,
            "Contas de convidado sem sinal de acesso dentro da janela definida pela regra.",
            "Acessos concedidos a terceiros continuam válidos até que alguém os reveja. Sem confirmação de que ainda são necessários, eles permanecem como porta aberta sem dono definido.",
            "Convidados revisados periodicamente, com expiração automática do acesso de terceiros.",
            "Atividade desconhecida não é inatividade comprovada: parte dos convidados pode não ter registro de acesso disponível.",
            Docs(), Caps(KnightCapability.GuestAccounts, KnightCapability.DirectoryUsers)),

        new KnightControlProfile("AK-ENTRA-005", KnightSecurityDomain.Identity,
            "Contas técnicas isentas de MFA sem controle compensatório comprovado.",
            "Uma conta de serviço com senha e sem segundo fator é um alvo estável: a credencial raramente muda e o uso raramente é observado.",
            "Contas de serviço migradas para identidades gerenciadas ou credenciais rotacionadas; isenções justificadas por controle compensatório comprovado.",
            null, Docs(), Caps(KnightCapability.ServiceAccountExemptions)),

        new KnightControlProfile("AK-ENTRA-006", KnightSecurityDomain.Identity,
            "Parte relevante dos usuários habilitados não tem método capaz de MFA registrado.",
            "Usuários sem método registrado não conseguem cumprir uma exigência de MFA — e, sem ela, uma senha comprometida basta para entrar.",
            "Cobertura de registro de MFA igual ou superior ao mínimo definido no catálogo.",
            "Registro não comprova exigência por política nem aplicação efetiva na autenticação.",
            Docs(), Caps(KnightCapability.MfaRegistration)),

        new KnightControlProfile("AK-ENTRA-007", KnightSecurityDomain.Identity,
            "Protocolos de autenticação legada não estão comprovadamente bloqueados por política habilitada ou pelos security defaults.",
            "Autenticação legada não suporta MFA: ela abre um caminho em que a exigência de segundo fator simplesmente não se aplica, e é o canal preferido de ataques de senha em massa.",
            "Política de acesso condicional HABILITADA bloqueando os clientes Exchange ActiveSync e \"outros clientes\" (ou todos os tipos de cliente) para todos os usuários e todas as aplicações — ou security defaults habilitados.",
            "Exclusões explícitas (como contas de emergência) não são tratadas como irregulares nem como bloqueadas: sem outra política que as cubra, o controle fica não avaliado, com as exceções listadas. Uma política em somente relatório não bloqueia nada.",
            Docs(DocLegacy, DocDefaults), Caps(KnightCapability.ConditionalAccessPolicies, KnightCapability.SecurityBaseline)),

        new KnightControlProfile("AK-ENTRA-008", KnightSecurityDomain.Identity,
            "Papéis administrativos ativos sem política habilitada que EXIJA MFA em todas as aplicações.",
            "Contas administrativas são o alvo prioritário de roubo de credencial. Sem exigência de segundo fator por política, uma senha comprometida de um administrador basta para controlar o ambiente.",
            "Para cada membro dos papéis privilegiados: ao menos uma política de acesso condicional HABILITADA que o alcance (pelo papel, por todos os usuários ou nominalmente), em todas as aplicações, sem condição que estreite a exigência, exigindo MFA ou uma força de autenticação — ou security defaults habilitados.",
            "Exigência por política não comprova que cada autenticação aplicou o segundo fator. Exclusões explícitas (como contas de emergência) não são irregulares por si, mas não comprovam proteção: sem outra política que as cubra, o controle fica não avaliado e as contas aparecem nomeadas. Pertencimento a grupos não é coletado.",
            Docs(DocAdminMfa, DocDefaults),
            Caps(KnightCapability.ConditionalAccessPolicies, KnightCapability.SecurityBaseline, KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-009", KnightSecurityDomain.Identity,
            "Aplicações com segredos ou certificados vencidos ou perto do vencimento.",
            "Credencial de aplicação vencida quebra integrações; credencial longeva e esquecida é uma chave que ninguém vigia.",
            "Credenciais rotacionadas antes do vencimento; preferência por certificados ou identidades gerenciadas.",
            null, Docs(), Caps(KnightCapability.ApplicationInventory)),

        new KnightControlProfile("AK-ENTRA-010", KnightSecurityDomain.IamRbac,
            "Aplicações com permissões de aplicativo de alto privilégio efetivamente concedidas sobre o diretório.",
            "Uma aplicação com permissão ampla age sem usuário e sem MFA; comprometer o segredo dela equivale a comprometer um administrador.",
            "Permissões de aplicativo no mínimo necessário, revisadas periodicamente.",
            "Mede permissões CONCEDIDAS (appRoleAssignments), não as solicitadas pela aplicação.",
            Docs(), Caps(KnightCapability.ApplicationPermissions)),

        new KnightControlProfile("AK-ENTRA-011", KnightSecurityDomain.IamRbac,
            "Contas privilegiadas sem atividade dentro da janela definida.",
            "Privilégio que ninguém usa continua podendo ser usado por quem roubar a credencial — e costuma ser o menos vigiado.",
            "Acesso privilegiado sem uso recente revisado e removido, ou convertido em elevação sob demanda.",
            null, Docs(DocRoles), Caps(KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-012", KnightSecurityDomain.IamRbac,
            "Membros externos (convidados) com papel privilegiado no diretório.",
            "Uma identidade externa com papel administrativo depende da segurança de outra organização, fora do seu controle.",
            "Papéis privilegiados restritos a identidades internas; exceções aprovadas e revisadas.",
            null, Docs(DocRoles), Caps(KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-013", KnightSecurityDomain.IamRbac,
            "Aplicações com consentimento delegado concedido a todos os usuários do diretório.",
            "Consentimento amplo permite que uma aplicação leia dados em nome de qualquer usuário — um caminho comum de persistência após phishing de consentimento.",
            "Consentimento do usuário restrito; concessões tenant-wide revisadas.",
            null, Docs(DocConsent), Caps(KnightCapability.ApplicationConsents)),

        new KnightControlProfile("AK-ENTRA-014", KnightSecurityDomain.Governance,
            "Nenhuma base mínima de proteção de identidade: sem security defaults e sem política habilitada exigindo MFA de todos os usuários.",
            "Sem uma base mínima, cada usuário depende só da própria senha, e nenhum controle do provedor compensa isso.",
            "Security defaults habilitados, ou ao menos uma política de acesso condicional HABILITADA exigindo MFA com alvo declarado em todos os usuários, em todas as aplicações e sem condição que a estreite.",
            "Presença de baseline não significa cobertura completa: exclusões são listadas e o alcance para administradores é avaliado em AK-ENTRA-008. Política de alcance restrito não sustenta uma base para o ambiente.",
            Docs(DocDefaults), Caps(KnightCapability.ConditionalAccessPolicies, KnightCapability.SecurityBaseline)),

        new KnightControlProfile("AK-ENTRA-015", KnightSecurityDomain.Governance,
            "Não há contas de acesso de emergência (break-glass) designadas.",
            "Sem conta de emergência, um erro de política ou uma indisponibilidade do provedor de identidade pode trancar todos os administradores fora do ambiente.",
            "Ao menos duas contas de emergência designadas, protegidas e monitoradas.",
            "A designação normalmente exige marcação manual e não é inferível por API.",
            Docs(DocBreakGlass), Caps(KnightCapability.BreakGlassDesignation)),

        new KnightControlProfile("AK-GWS-001", KnightSecurityDomain.Identity,
            "Superadministradores sem verificação em duas etapas (2SV) inscrita.",
            "O superadministrador controla todo o Workspace; sem 2SV, a senha dele é o único obstáculo.",
            "Todos os superadministradores com 2SV inscrita, de preferência com chaves de segurança.",
            "Inscrição em 2SV não comprova que ela é exigida em cada acesso.",
            Docs(), Caps(KnightCapability.DirectoryUsers)),

        new KnightControlProfile("AK-GWS-002", KnightSecurityDomain.Identity,
            "Baixa cobertura de 2SV entre os usuários ativos do diretório.",
            "Usuários sem segundo fator são o ponto de entrada mais barato para comprometer contas do Workspace.",
            "Cobertura de 2SV igual ou superior ao mínimo do catálogo.",
            null, Docs(), Caps(KnightCapability.DirectoryUsers)),

        new KnightControlProfile("AK-GWS-003", KnightSecurityDomain.IamRbac,
            "Superadministradores sem login dentro da janela definida.",
            "Privilégio máximo sem uso é superfície de ataque sem benefício operacional.",
            "Superadministradores sem uso recente revisados e reduzidos.",
            null, Docs(), Caps(KnightCapability.DirectoryUsers)),

        new KnightControlProfile("AK-GWS-004", KnightSecurityDomain.Collaboration,
            "Membros externos (fora dos domínios da organização) em grupos do Workspace.",
            "Grupos distribuem e-mail e acesso a arquivos; um membro externo recebe tudo o que o grupo recebe.",
            "Associação externa restrita e aprovada, com revisão periódica.",
            null, Docs(), Caps(KnightCapability.DirectoryGroups), ServiceOverride: "Google Groups"),

        new KnightControlProfile("AK-GWS-005", KnightSecurityDomain.DataProtection,
            "Eventos recentes de compartilhamento externo ou público de arquivos no Drive.",
            "Arquivo compartilhado por link público ou com terceiros sai do perímetro de controle da organização.",
            "Compartilhamento externo restrito por política e eventos de exposição revisados.",
            "Os eventos são de mudança de visibilidade na janela; não indicam que houve acesso indevido ao conteúdo.",
            Docs(), Caps(KnightCapability.DriveSharingAudit), ServiceOverride: "Google Drive"),

        new KnightControlProfile("AK-GWS-006", KnightSecurityDomain.IamRbac,
            "Autorizações OAuth recentes concedidas a aplicativos de terceiros.",
            "Um aplicativo autorizado passa a agir em nome do usuário com os escopos concedidos, fora do controle direto da organização.",
            "Aplicativos de terceiros revisados; consentimento do usuário restrito a aplicativos confiáveis.",
            null, Docs(), Caps(KnightCapability.OAuthTokenAudit)),
    };

    /// <summary>Perfil do controle, ou <c>null</c> quando o indicador não tem perfil catalogado (nunca inventado).</summary>
    public static KnightControlProfile? For(string indicatorId) =>
        ById.TryGetValue((indicatorId ?? "").Trim(), out var p) ? p : null;

    /// <summary>Todos os perfis (ordem estável).</summary>
    public static IReadOnlyList<KnightControlProfile> All { get; } =
        ById.Values.OrderBy(p => p.IndicatorId, StringComparer.Ordinal).ToList();

    /// <summary>Provedor que opera o serviço da FONTE — nunca "Microsoft" por omissão.</summary>
    public static string ProviderOf(KnightSourceType source) => source switch
    {
        KnightSourceType.MicrosoftEntraId => "Microsoft",
        KnightSourceType.GoogleWorkspace => "Google",
        KnightSourceType.Demo => "Demonstração",
        _ => source.ToString(),
    };

    /// <summary>
    /// Serviço TIPADO do controle: o declarado na definição do catálogo; nos controles compartilhados, o da fonte
    /// (com o ajuste de serviço do perfil, quando houver).
    /// </summary>
    public static KnightService ServiceKindOf(string indicatorId, KnightSourceType source)
    {
        if (source == KnightSourceType.Demo) return KnightService.Demo;
        var declared = KnightCatalog.Indicators.FirstOrDefault(d => d.Id == indicatorId)?.Service ?? KnightService.Unspecified;
        if (declared != KnightService.Unspecified) return declared;
        if (source == KnightSourceType.GoogleWorkspace)
            return For(indicatorId)?.ServiceOverride switch
            {
                "Google Groups" => KnightService.GoogleGroups,
                "Google Drive" => KnightService.GoogleDrive,
                _ => KnightService.GoogleWorkspace,
            };
        return KnightServices.DefaultFor(source);
    }

    /// <summary>Plataforma do controle (primeiro nível de filtro: Entra ID, Microsoft 365, Azure, Google Workspace).</summary>
    public static string PlatformOf(string indicatorId, KnightSourceType source) =>
        KnightServices.Describe(ServiceKindOf(indicatorId, source)) is { } d
            ? KnightServices.PlatformLabel(d.Platform)
            : ProviderOf(source);

    /// <summary>Serviço onde a configuração avaliada vive, para aquela fonte.</summary>
    public static string ServiceOf(string indicatorId, KnightSourceType source)
    {
        if (source == KnightSourceType.Demo) return "Demonstração (sintético)";
        if (KnightServices.Describe(ServiceKindOf(indicatorId, source)) is { } described
            && KnightCatalog.Indicators.FirstOrDefault(d => d.Id == indicatorId)?.Service is { } svc && svc != KnightService.Unspecified)
            return described.Label;
        var p = For(indicatorId);
        if (p?.ServiceOverride is { } s && source == KnightSourceType.GoogleWorkspace) return s;
        return source switch
        {
            KnightSourceType.MicrosoftEntraId => "Microsoft Entra ID",
            KnightSourceType.GoogleWorkspace => "Google Workspace",
            _ => source.ToString(),
        };
    }

    /// <summary>Domínio do controle; sem perfil, deriva da categoria (todas as categorias atuais são de identidade/IAM).</summary>
    public static KnightSecurityDomain DomainOf(string indicatorId, KnightIndicatorCategory category) =>
        For(indicatorId)?.Domain ?? category switch
        {
            KnightIndicatorCategory.IdentityGovernance => KnightSecurityDomain.IamRbac,
            _ => KnightSecurityDomain.Identity,
        };

    public static string DomainLabel(KnightSecurityDomain d) => d switch
    {
        KnightSecurityDomain.Identity => "Identidade",
        KnightSecurityDomain.IamRbac => "IAM/RBAC",
        KnightSecurityDomain.Network => "Rede",
        KnightSecurityDomain.Compute => "Computação",
        KnightSecurityDomain.Storage => "Armazenamento",
        KnightSecurityDomain.Databases => "Bancos de dados",
        KnightSecurityDomain.Secrets => "Segredos/Key Vault",
        KnightSecurityDomain.CloudProtection => "Proteção cloud",
        KnightSecurityDomain.Logging => "Logs/Monitoramento",
        KnightSecurityDomain.Governance => "Governança",
        KnightSecurityDomain.Collaboration => "Colaboração/comunicação",
        KnightSecurityDomain.DataProtection => "Proteção de dados",
        _ => d.ToString(),
    };

    /// <summary>
    /// Referências de framework do controle: NIST CSF 2.0 e MITRE ATT&amp;CK a partir dos códigos do próprio
    /// indicador, mais a documentação oficial conferida. Um controle mapeado a vários frameworks continua sendo
    /// UM controle — a contagem nunca multiplica por framework.
    /// </summary>
    public static IReadOnlyList<KnightControlReference> ReferencesOf(
        string indicatorId, IEnumerable<string> nistCodes, IEnumerable<string> mitreTechniques)
    {
        var list = new List<KnightControlReference>();
        foreach (var n in nistCodes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            list.Add(new KnightControlReference(NistFramework, NistVersion, n.Trim()));
        foreach (var m in mitreTechniques.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            list.Add(new KnightControlReference(MitreFramework, null, m.Trim()));
        list.AddRange(BenchmarksOf(indicatorId));
        if (For(indicatorId) is { } p) list.AddRange(p.Documentation);
        return list;
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Benchmarks de configuração que o controle avalia, com a versão fixada. Equivalência
    /// parcial é dita no próprio código da referência — o leitor não precisa adivinhar se o critério é idêntico.
    /// </summary>
    public static IReadOnlyList<KnightControlReference> BenchmarksOf(string indicatorId)
    {
        var def = KnightCatalog.Indicators.FirstOrDefault(d => d.Id == indicatorId);
        if (def is null) return Array.Empty<KnightControlReference>();
        var list = new List<KnightControlReference>();
        foreach (var link in def.References)
        {
            if (KnightReferenceCatalog.Find(link.Key) is not { } c) continue;
            var code = (c.Section ?? "sem seção") + (c.Variant is null ? "" : $" ({c.Variant})")
                + (link.Match == KnightReferenceMatch.Partial ? " — equivalência parcial" : "");
            list.Add(new KnightControlReference(c.Framework, c.Version, code));
        }
        return list;
    }

    /// <summary>Framework de benchmark de configuração (as referências fixadas no catálogo de referência).</summary>
    public static bool IsBenchmark(string framework) =>
        framework.StartsWith("CIS ", StringComparison.Ordinal);

    /// <summary>Capacidades de coleta que o controle consome — base de "controles prejudicados" por limitação.</summary>
    public static IReadOnlyList<KnightCapability> RequiredCapabilitiesOf(string indicatorId) =>
        For(indicatorId)?.RequiredCapabilities ?? Array.Empty<KnightCapability>();
}

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Monta a apresentação de UM controle de uma avaliação: perfil do catálogo corrente,
/// eixos e contribuição para a nota pela fórmula oficial. O critério da regra só é exibido quando a avaliação
/// usou o MESMO catálogo — uma avaliação antiga não recebe a descrição de uma regra que ela não aplicou.
/// </summary>
public static class KnightControlPresentations
{
    public static KnightControlPresentation For(
        string indicatorId, KnightIndicatorCategory category, SeverityLevel severity, KnightIndicatorStatus status,
        KnightSourceType source, IEnumerable<string> nistCodes, IEnumerable<string> mitreTechniques, string runCatalogVersion)
    {
        var profile = KnightControlProfiles.For(indicatorId);
        var domain = KnightControlProfiles.DomainOf(indicatorId, category);
        var weight = KnightScoreFormula.WeightFor(severity);
        var factor = KnightScoreFormula.FactorFor(status);
        var criterion = string.Equals(runCatalogVersion, KnightCatalog.Version, StringComparison.Ordinal)
            ? KnightCatalog.Indicators.FirstOrDefault(d => d.Id == indicatorId)?.ExpectedEvidence
            : null;

        return new KnightControlPresentation(
            domain.ToString(),
            KnightControlProfiles.DomainLabel(domain),
            KnightControlProfiles.ServiceOf(indicatorId, source),
            KnightControlProfiles.ProviderOf(source),
            profile?.Description,
            profile?.Rationale,
            profile?.ExpectedConfiguration,
            profile?.DoesNotProve,
            criterion,
            KnightControlProfiles.ReferencesOf(indicatorId, nistCodes, mitreTechniques),
            KnightControlProfiles.RequiredCapabilitiesOf(indicatorId).Select(c => c.ToString()).ToList(),
            weight,
            factor,
            factor is null ? null : weight * factor.Value,
            factor is null ? null : weight,
            profile?.Impact,
            KnightControlProfiles.PlatformOf(indicatorId, source),
            KnightControlProfiles.ServiceKindOf(indicatorId, source).ToString());
    }
}
