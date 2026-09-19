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
    };

    private const string Sspr =
        "As opções de redefinição de senha self-service (escopo, número de métodos, reconfirmação e notificações) não são expostas "
        + "pela versão estável do Microsoft Graph: a política de métodos de autenticação v1.0 informa apenas o estado da migração das "
        + "políticas legadas de MFA e SSPR. Verificação manual em Redefinição de senha.";

    private static Declared Api(string note) => new(KnightReferenceDisposition.ApiLimitation, note);
    private static Declared Manual(string note) => new(KnightReferenceDisposition.ManualOnly, note);

    public static Declared? For(string key) => ByKey.TryGetValue(key, out var d) ? d : null;

    /// <summary>Todas as disposições declaradas (para testes e auditoria da lista).</summary>
    public static IReadOnlyDictionary<string, Declared> All => ByKey;

    /// <summary>Nota padrão de um controle pendente, pelo serviço (o que falta para implementá-lo).</summary>
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
