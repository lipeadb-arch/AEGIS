using System;
using System.Collections.Generic;

namespace AegisScore.Application.Knight.Reference;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Disposições DECLARADAS para controles de referência que o KNIGHT não avalia de forma
/// automatizada — e o porquê, fundamentado na documentação oficial do fornecedor. Três motivos que NÃO se misturam:
///   • LIMITAÇÃO DE API — não há leitura na versão ESTÁVEL da API oficial (só na versão beta, cujo uso em produção o
///     fornecedor não suporta, ou só no portal). Não é "impossível": é o que a API suportada permite hoje;
///   • EXIGE OUTRO ACESSO — existe método oficial, mas com autenticação ou permissão que o conector atual não tem;
///   • VERIFICAÇÃO MANUAL — o critério depende de contexto organizacional que nenhuma configuração expressa.
/// Um controle que nenhum controle ativo cita e que não está aqui é PENDENTE: ainda não implementado. Uma limitação
/// documentada não é avaliação executada — estes controles nunca contam como aprovados nem como cobertos.
/// </summary>
public static class KnightReferenceDispositions
{
    public sealed record Declared(KnightReferenceDisposition Disposition, string Note);

    private const string StableGraph = "versão estável (v1.0) do Microsoft Graph";

    private static readonly IReadOnlyDictionary<string, Declared> ByKey = new Dictionary<string, Declared>(StringComparer.Ordinal)
    {
        // ---- Microsoft Entra ID: sem leitura na API estável ----------------------------------------------------
        ["CIS-M365-7.0.0:5.1.2.1"] = Api(
            $"O estado da MFA por usuário (legado) só é exposto pela versão beta do Microsoft Graph (strongAuthenticationRequirements), "
            + "cujo uso em produção a Microsoft não suporta, e exige uma leitura por usuário. Verificação manual no centro de administração."),
        ["CIS-AZ-6.0.0:5.1.3"] = Api(
            $"O estado da MFA por usuário (legado) só é exposto pela versão beta do Microsoft Graph (strongAuthenticationRequirements), "
            + "cujo uso em produção a Microsoft não suporta. Verificação manual no centro de administração."),
        ["CIS-AZ-6.0.0:5.1.4"] = Api(
            "A opção de lembrar a MFA em dispositivos confiáveis pertence às configurações do serviço de MFA por usuário (legado), "
            + $"que não têm leitura na {StableGraph}. Verificação manual no portal do serviço de MFA."),
        ["CIS-M365-7.0.0:5.1.2.4"] = Api(
            "A restrição de acesso ao centro de administração do Microsoft Entra não é uma propriedade da política de autorização "
            + $"(defaultUserRolePermissions) na {StableGraph}. Verificação manual em Usuários → Configurações de usuário."),
        ["CIS-M365-7.0.0:5.1.2.5"] = Api(
            "A opção de ocultar “Continuar conectado?” não consta das propriedades de identidade visual (loginPageTextVisibilitySettings) "
            + $"na {StableGraph}. Verificação manual em Identidade visual da empresa."),
        ["CIS-M365-7.0.0:5.1.2.6"] = Api(
            "As conexões de conta do LinkedIn não são uma propriedade da política de autorização "
            + $"(defaultUserRolePermissions) na {StableGraph}. Verificação manual em Usuários → Configurações de usuário."),
        ["CIS-M365-7.0.0:5.1.3.2"] = Api(
            $"As opções de grupos self-service do painel de acesso não são expostas na {StableGraph}. Verificação manual em Grupos → Configurações gerais."),
        ["CIS-M365-7.0.0:5.1.3.3"] = Api(
            $"A opção de proprietários gerenciarem solicitações de associação em Meus Grupos não é exposta na {StableGraph}. Verificação manual em Grupos → Configurações gerais."),
        ["CIS-M365-7.0.0:5.1.6.1"] = Api(
            "A lista de domínios permitidos ou bloqueados para convites só é exposta pelo endpoint legado da versão beta do Microsoft Graph "
            + "(B2BManagementPolicy), não suportado em produção. Verificação manual em Identidades externas → Configurações de colaboração externa."),
        ["CIS-M365-7.0.0:5.2.3.6"] = Api(
            "A MFA preferencial do sistema (systemCredentialPreferences) só é exposta pela versão beta do Microsoft Graph, "
            + "cujo uso em produção a Microsoft não suporta. Verificação manual em Métodos de autenticação → Configurações."),
        ["CIS-M365-7.0.0:5.2.3.10"] = Api(
            "O uso do Authenticator em aplicativos complementares (companionAppAllowedState) só é exposto pela versão beta do Microsoft Graph; "
            + "a versão estável expõe apenas a exibição do nome do aplicativo e da localização. Verificação manual na política do Microsoft Authenticator."),
        ["CIS-M365-7.0.0:5.2.2.16"] = Api(
            "O controle de sessão de proteção de token não consta dos controles de sessão do acesso condicional (conditionalAccessSessionControls) "
            + $"na {StableGraph}. Verificação manual nas políticas de acesso condicional."),
        ["CIS-M365-7.0.0:1.3.4"] = Api(
            "As configurações de aplicativos e serviços próprios dos usuários (adminAppsAndServices) só são expostas pela versão beta do Microsoft Graph. "
            + "Verificação manual no centro de administração do Microsoft 365 → Configurações da organização."),
        ["CIS-M365-7.0.0:5.2.4.1"] = Api(Sspr),
        ["CIS-M365-7.0.0:5.2.4.2"] = Api(Sspr),
        ["CIS-M365-7.0.0:5.2.4.3"] = Api(Sspr),
        ["CIS-M365-7.0.0:5.2.4.4"] = Api(Sspr),
        ["CIS-M365-7.0.0:5.2.4.5"] = Api(Sspr),

        // ---- Microsoft Entra ID: critério organizacional ------------------------------------------------------
        ["CIS-M365-7.0.0:1.1.2"] = Manual(
            "A designação de contas de acesso de emergência é uma decisão organizacional, não uma propriedade legível do diretório. "
            + "O controle AK-ENTRA-015 registra a lacuna como não avaliada até que a designação seja informada."),
        ["CIS-AZ-6.0.0:5.3.1"] = Manual(
            "Depende de como as pessoas usam as contas administrativas no dia a dia, o que nenhuma configuração expressa. "
            + "Indícios automatizados relacionados: AK-ENTRA-003 (caixa de correio) e AK-ENTRA-048 (licenças de produtividade)."),

        // ---- [AEGIS-KNIGHT-COVERAGE-04] Microsoft 365: sem leitura na API estável ----------------------------
        // Métodos examinados (documentação oficial, 30/09/2026): o recurso admin do Microsoft Graph na versão estável (v1.0) e
        // na beta, e a lista de cargas de trabalho do Tenant Configuration Management (TCM, v1.0), que é o método oficial
        // de leitura declarativa de configuração entre serviços.
        ["CIS-M365-7.0.0:1.3.5"] = Api(
            "Dado ausente: a proteção interna contra phishing do Microsoft Forms (isInOrgFormsPhishingScanEnabled). Métodos examinados: "
            + "o recurso admin da " + StableGraph + " não tem contêiner do Forms; a leitura existe só na versão beta (adminForms → "
            + "formsSettings), cujo uso em produção a Microsoft não suporta; o Tenant Configuration Management não lista o Forms entre "
            + "as cargas de trabalho suportadas. Verificação manual no centro de administração do Microsoft 365 → Configurações da "
            + "organização → Microsoft Forms."),
        ["CIS-M365-7.0.0:1.3.8"] = Api(
            "Dado ausente: a configuração de compartilhamento externo do Sway. Métodos examinados: o recurso admin do Microsoft Graph, "
            + "na versão estável (v1.0) e na beta, não tem contêiner do Sway; o Tenant Configuration Management não lista o Sway entre as "
            + "cargas de trabalho suportadas. Verificação manual no centro de administração do Microsoft 365 → Configurações da "
            + "organização → Sway."),

        // ---- [AEGIS-KNIGHT-COVERAGE-04] Microsoft 365: critério organizacional -------------------------------
        ["CIS-M365-7.0.0:2.2.1"] = Manual(
            "O critério pede que a atividade das contas de acesso de emergência seja monitorada. Dizer quais contas são de emergência "
            + "é uma decisão organizacional (ver AK-ENTRA-015), e o monitoramento pode viver fora do Microsoft 365 (SIEM, SOC). "
            + "Nenhuma configuração lida pelo AEGIS prova que um alerta sobre ESSAS contas existe e é tratado."),

        // ---- Microsoft Teams: existe leitura oficial, mas não com a autenticação deste conector ---------------
        ["CIS-M365-7.0.0:8.4.1"] = Access(
            "O comando oficial que lê as políticas de permissão de aplicativos (Get-CsTeamsAppPermissionPolicy) só se aplica a "
            + "locatários NÃO migrados para o gerenciamento centrado em aplicativos (ACM/UAM); depois da migração essas políticas não "
            + "podem mais ser acessadas, editadas nem usadas. Dizer se o locatário migrou exigiria Get-AllM365TeamsApps, "
            + "Get-M365TeamsApp ou Get-M365UnifiedTenantSettings — os três listados nominalmente pela Microsoft entre os comandos NÃO "
            + "SUPORTADOS com autenticação baseada em aplicativo, que é a forma de acesso desta coleta. Não é falta de permissão: "
            + "nenhum papel adicional torna um comando não suportado suportado. O AEGIS coleta e preserva a configuração legada como "
            + "evidência em AK-TEAMS-007, sem veredito. Concluir dependeria de uma sessão administrativa delegada — outra forma de "
            + "acesso ao locatário, que é decisão do cliente."),
    };

    private const string Sspr =
        "As opções de redefinição de senha self-service (escopo, número de métodos, reconfirmação e notificações) não são expostas "
        + "pela versão estável do Microsoft Graph: a política de métodos de autenticação v1.0 informa apenas o estado da migração das "
        + "políticas legadas de MFA e SSPR. Verificação manual em Redefinição de senha.";

    private static Declared Api(string note) => new(KnightReferenceDisposition.ApiLimitation, note);
    private static Declared Manual(string note) => new(KnightReferenceDisposition.ManualOnly, note);
    private static Declared Access(string note) => new(KnightReferenceDisposition.RequiresAccess, note);

    public static Declared? For(string key) => ByKey.TryGetValue(key, out var d) ? d : null;

    /// <summary>Todas as disposições declaradas (para testes e auditoria da lista).</summary>
    public static IReadOnlyDictionary<string, Declared> All => ByKey;

    /// <summary>Nota padrão de um controle pendente, pelo serviço (o que falta para implementá-lo).</summary>
    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Referências PENDENTES com pesquisa em andamento: o que já foi examinado e o que ainda
    /// falta demonstrar. Continuam pendentes — uma busca que não encontrou a leitura não é prova de que ela não existe.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Research = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CIS-M365-7.0.0:2.4.3"] =
            "Pesquisa pendente. Dado buscado: os conectores de aplicativos do Defender for Cloud Apps (Microsoft 365 e Azure) e as "
            + "integrações com o Defender for Endpoint e o Defender for Identity. Examinado: a API REST do Defender for Cloud Apps "
            + "documenta só atividades, alertas, enriquecimento de dados, entidades e arquivos — nenhum recurso de configuração de "
            + "conector. Falta examinar os tipos de recurso da carga “Microsoft Defender” do Tenant Configuration Management.",
        ["CIS-M365-7.0.0:2.4.5"] =
            "Pesquisa pendente. Dado buscado: a configuração de correção automatizada da investigação e resposta automatizadas (AIR) do "
            + "Defender para Office 365. Examinado: os comandos Get do módulo do Exchange Online já usados pelo AEGIS não a expõem. "
            + "Falta examinar a API do Microsoft Defender XDR e os tipos de recurso da carga “Microsoft Defender” do Tenant "
            + "Configuration Management antes de classificar a referência.",
    };

    /// <summary>Motivo de uma referência pendente: a pesquisa em andamento, quando houver; senão, o do serviço.</summary>
    public static string PendingNote(KnightReferenceControl control) =>
        Research.TryGetValue(control.Key, out var research) ? research : PendingNote(control.Service);

    public static IReadOnlyCollection<string> ResearchKeys => (IReadOnlyCollection<string>)Research.Keys;

    public static string PendingNote(KnightService service) =>
        (KnightServices.Describe(service)?.Platform) switch
        {
            KnightPlatform.EntraId => "Ainda não implementado no KNIGHT.",
            KnightPlatform.Microsoft365 =>
                "Ainda não implementado: a coleta deste serviço do Microsoft 365 está prevista nos próximos blocos. O método oficial de leitura "
                + "e a autenticação exigida serão confirmados controle a controle.",
            KnightPlatform.Azure =>
                "Ainda não implementado: a coleta dos recursos do Azure (Azure Resource Manager, somente leitura) está prevista nos próximos blocos.",
            _ => "Ainda não implementado no KNIGHT.",
        };
}
