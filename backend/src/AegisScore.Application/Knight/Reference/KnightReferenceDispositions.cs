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

        // ---- [AEGIS-KNIGHT-COVERAGE-04] Azure: sem operação na versão estável da API ---------------------------
        // Conferido em 01/10/2026 em TODAS as versões estáveis publicadas em Azure/azure-rest-api-specs: 33 do provedor
        // Microsoft.Insights, 25 do Microsoft.Security e nenhuma do Microsoft.Intune. O AEGIS não usa versão preview da
        // ARM pela mesma regra que o impede de usar a versão beta do Microsoft Graph: o fornecedor não a suporta em produção.
        ["CIS-AZ-6.0.0:6.1.1.1"] = Api(SubDiag + " Verificação manual em Monitor → Log de atividades → Exportar logs de atividades."),
        ["CIS-AZ-6.0.0:6.1.1.2"] = Api(SubDiag + " As categorias capturadas pertencem à mesma configuração. Verificação manual em Monitor → Log de atividades → Exportar logs de atividades."),
        ["CIS-AZ-6.0.0:6.1.1.3"] = Api(SubDiag + " Sem ler a configuração, não se sabe qual conta de armazenamento recebe o log de atividades para conferir a chave dela. Verificação manual na configuração de exportação e na criptografia da conta de destino."),
        ["CIS-AZ-6.0.0:6.1.1.4"] = Api(ResourceDiag("Key Vault") + " Verificação manual em cada cofre → Configurações de diagnóstico (categoria AuditEvent)."),
        ["CIS-AZ-6.0.0:6.1.4"] = Api(ResourceDiag("recurso") + " Verificação manual em Monitor → Configurações de diagnóstico, recurso a recurso."),
        ["CIS-AZD-2.0.0:3.7"] = Api(ResourceDiag("Cosmos DB") + " Verificação manual em cada conta → Configurações de diagnóstico."),
        ["CIS-AZC-2.0.0:15.7"] = Api(ResourceDiag("Batch") + " Verificação manual em cada conta do Batch → Configurações de diagnóstico."),
        ["CIS-AZ-6.0.0:2.1.7"] = Api(ResourceDiag("Databricks") + " Verificação manual em cada workspace → Configurações de diagnóstico."),
        ["CIS-AZ-6.0.0:6.1.1.9"] = Api(
            "Dado ausente: as configurações de diagnóstico do Intune (microsoft.intune/diagnosticSettings), recurso do Azure Resource Manager. "
            + "Métodos examinados: não há nenhuma versão estável publicada do provedor Microsoft.Intune em Azure/azure-rest-api-specs. "
            + "Verificação manual no centro de administração do Intune → Administração de locatários → Configurações de diagnóstico."),
        ["CIS-AZ-6.0.0:8.1.12"] = Api(SecurityContacts + " Critério: notificar os proprietários da assinatura sobre alertas. Verificação manual em Defender para Nuvem → Configurações de ambiente → Notificações por e-mail."),
        ["CIS-AZ-6.0.0:8.1.13"] = Api(SecurityContacts + " Critério: e-mail de contato de segurança adicional. Verificação manual em Defender para Nuvem → Configurações de ambiente → Notificações por e-mail."),
        ["CIS-AZ-6.0.0:8.1.14"] = Api(SecurityContacts + " Critério: notificação de alertas a partir de uma severidade. Verificação manual em Defender para Nuvem → Configurações de ambiente → Notificações por e-mail."),
        ["CIS-AZ-6.0.0:8.1.15"] = Api(SecurityContacts + " Critério: notificação de caminhos de ataque por nível de risco. Verificação manual em Defender para Nuvem → Configurações de ambiente → Notificações por e-mail."),

        // ---- [AEGIS-KNIGHT-COVERAGE-04] Azure: critério organizacional ----------------------------------------
        ["CIS-AZ-6.0.0:5.3.4"] = Manual(
            "O critério é a REVISÃO periódica das atribuições privilegiadas, um processo. O AEGIS lê as atribuições (ver AK-AZ-IAM-001, "
            + "AK-AZ-IAM-002 e AK-AZ-IAM-006), mas nenhuma configuração do Azure RBAC prova que alguém as revisa."),
        ["CIS-AZ-6.0.0:5.3.7"] = Manual(
            "O critério é a REVISÃO periódica das atribuições não privilegiadas, um processo; nenhuma configuração lida pelo AEGIS a comprova."),
        ["CIS-AZ-6.0.0:6.2"] = Manual(
            "Quais recursos são de missão crítica é decisão organizacional. O AEGIS lê os bloqueios (ver AK-AZ-STO-019 para o armazenamento), "
            + "mas não sabe a quais outros recursos o critério se aplica."),
        ["CIS-AZ-6.0.0:6.1.5"] = Manual(
            "Quais cargas são de produção é decisão organizacional; o SKU de um recurso não diz se ele é produção."),
        ["CIS-AZ-6.0.0:7.7"] = Manual(
            "O critério é a AVALIAÇÃO periódica dos IPs públicos, um processo. O AEGIS lê os IPs públicos (família de rede), mas a necessidade "
            + "de cada um é decisão organizacional."),
        ["CIS-AZ-6.0.0:8.3.10"] = Manual(
            "Usar HSM gerenciado “quando exigido” depende de requisito regulatório ou contratual da organização, não de configuração."),
        ["CIS-AZ-6.0.0:8.1.5.2"] = Manual(
            "O critério é que os alertas de proteção contra ameaças do armazenamento sejam MONITORADOS (triagem por uma equipe). O AEGIS "
            + "avalia se o plano que os gera está ligado (AK-AZ-MDC-005), mas não o tratamento dos alertas."),
        ["CIS-AZ-6.0.0:9.3.10"] = Manual(
            "O critério pede que bloqueios somente leitura sejam CONSIDERADOS: o bloqueio ReadOnly impede operações que dependem de listar "
            + "chaves, e a decisão de usá-lo é organizacional. Os bloqueios encontrados aparecem como evidência em AK-AZ-STO-019."),
        ["CIS-AZ-6.0.0:9.3.11"] = Manual(
            "Quais contas de armazenamento são críticas é decisão organizacional; a redundância (SKU) sozinha não diz se a conta precisa dela."),
        ["CIS-AZ-6.0.0:3.1.1"] = Manual(VmMfa),
        ["CIS-AZC-2.0.0:20.9"] = Manual(VmMfa),
        ["CIS-AZC-2.0.0:3.3"] = Manual(
            "Menor privilégio dos papéis da identidade gerenciada depende do que o contêiner precisa fazer, o que nenhuma configuração expressa."),
        ["CIS-AZC-2.0.0:4.1"] = Manual(
            "O CycleCloud é uma aplicação instalada numa máquina virtual; a configuração de SSL dele não é um recurso do Azure Resource Manager. "
            + "Verificação na configuração do servidor web do CycleCloud."),
        ["CIS-AZC-2.0.0:20.6"] = Manual(
            "Quais extensões são aprovadas é decisão organizacional. O AEGIS lê as extensões de cada máquina (ver AK-AZ-CMP-013), mas não tem a "
            + "lista de aprovadas da organização."),
        ["CIS-AZD-2.0.0:2.4"] = Manual(
            "O critério é que as políticas de acesso do Redis sejam implementadas e REVISADAS periodicamente; a adequação de cada política e a "
            + "revisão são processos organizacionais."),
        ["CIS-AZD-2.0.0:4.4"] = Manual(
            "Gerenciar privilégios do Data Factory por RBAC é um critério de processo (quem recebe quais papéis); a fábrica não tem configuração "
            + "que o expresse."),
        ["CIS-AZ-6.0.0:2.1.12"] = Manual(
            "O critério é a REVISÃO periódica dos grupos do Databricks, um processo organizacional."),

        // ---- [AEGIS-KNIGHT-COVERAGE-04] Azure: existe leitura oficial, mas com outro acesso --------------------
        ["CIS-AZC-2.0.0:2.5"] = Access(
            "Dado ausente: as configurações de aplicativo (app settings) e cadeias de conexão, para saber se os segredos são referências ao "
            + "Key Vault. Método oficial: POST /sites/{nome}/config/appsettings/list, que exige a ação Microsoft.Web/sites/config/list/action — "
            + "ausente do papel Leitor, porque devolve os VALORES dos segredos. O AEGIS não pede um papel que lê segredos para avaliar postura."),
        ["CIS-AZ-6.0.0:2.1.3"] = Access(DatabricksWorkspace("a criptografia do tráfego entre os nós (configuração de cluster ou script de inicialização)")),
        ["CIS-AZ-6.0.0:2.1.4"] = Access(DatabricksWorkspace("a sincronização de usuários e grupos (SCIM) com o Entra ID")),
        ["CIS-AZ-6.0.0:2.1.5"] = Access(DatabricksWorkspace("o metastore do Unity Catalog atribuído ao workspace")),
        ["CIS-AZ-6.0.0:2.1.6"] = Access(DatabricksWorkspace("as permissões e a expiração de tokens pessoais")),
    };

    private const string SubDiag =
        "Dado ausente: as configurações de diagnóstico da ASSINATURA (exportação do log de atividades). Métodos examinados: a operação "
        + "Microsoft.Insights/diagnosticSettings no escopo da assinatura existe só em versões preview da API do Azure Monitor "
        + "(2017-05-01-preview e 2021-05-01-preview); nenhuma versão estável publicada a contém, e a referência REST da Microsoft "
        + "abre essa API na versão 2021-05-01-preview.";

    private static string ResourceDiag(string service) =>
        $"Dado ausente: as configurações de diagnóstico do {service}. Métodos examinados: a listagem "
        + "{recurso}/providers/Microsoft.Insights/diagnosticSettings existe só em versões preview da API do Azure Monitor; a única operação "
        + "na versão estável (2016-09-01) lê exclusivamente a configuração legada de nome “service”, que não representa as configurações "
        + "criadas hoje — ler só ela faria um recurso com logs parecer sem logs.";

    private const string SecurityContacts =
        "Dado ausente: os contatos de segurança e as notificações por e-mail do Defender para Nuvem (Microsoft.Security/securityContacts). "
        + "Métodos examinados: a operação existe só em versões preview (2017-08-01-preview, 2020-01-01-preview, 2023-12-01-preview); nenhuma "
        + "versão estável publicada do provedor Microsoft.Security a contém.";

    private const string VmMfa =
        "Exigir MFA no acesso privilegiado às máquinas depende de QUEM acessa e de COMO (login do Entra ID na VM, Bastion, acesso condicional "
        + "sobre o aplicativo de login das VMs). O AEGIS lê as atribuições de papel do Azure e as políticas de acesso condicional, mas ligar "
        + "as duas ao uso real do acesso às máquinas é avaliação contextual; contas locais das máquinas não são visíveis pelo Resource Manager.";

    private static string DatabricksWorkspace(string what) =>
        $"Dado ausente: {what}. Método oficial: a API REST do PRÓPRIO workspace do Databricks (https://<workspace>.azuredatabricks.net), "
        + "que exige que a aplicação seja adicionada ao workspace como entidade de serviço com permissão de leitura ou de administrador — "
        + "acesso concedido dentro do Databricks, que o papel Leitor do Azure não dá. É decisão do cliente conceder esse acesso.";

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
        // ---- [AEGIS-KNIGHT-COVERAGE-04] Azure -------------------------------------------------------------------
        ["CIS-AZ-6.0.0:7.16"] =
            "Pesquisa pendente. Dado buscado: a associação de recursos PaaS a um perímetro de segurança de rede. Examinado: a operação "
            + "estável de perímetros (Microsoft.Network/networkSecurityPerimeters) existe. Falta definir QUAIS tipos de recurso o critério "
            + "exige no perímetro e ler as associações de cada perímetro antes de implementar.",
        ["CIS-AZD-2.0.0:4.3"] =
            "Pesquisa pendente. Dado buscado: se as credenciais dos serviços vinculados do Data Factory são referências ao Key Vault. "
            + "Examinado: a listagem de serviços vinculados (factories/{nome}/linkedservices) é estável e lida com o papel Leitor. Falta "
            + "confirmar, tipo a tipo de serviço vinculado, como a referência ao Key Vault aparece sem que a resposta exponha segredo.",
        ["CIS-AZD-2.0.0:2.1#redis-enterprise"] =
            "Pesquisa pendente. Dado buscado: a autenticação do Entra ID nos bancos do Redis Enterprise. Examinado: a versão estável "
            + "2025-07-01 expõe accessKeysAuthentication por banco (usado em AK-AZ-DB-013). Falta confirmar se o Entra ID é sempre aceito "
            + "nessa versão (o que tornaria o critério equivalente ao bloqueio das chaves) ou se há propriedade própria.",
        ["CIS-AZC-2.0.0:s/n#plano-endpoint-privado"] =
            "Pesquisa pendente. Dado buscado: se a camada do plano do App Service suporta endpoints privados. Examinado: a camada (sku.tier) "
            + "do plano é lida. Falta a tabela oficial de camadas com suporte para comparar sem lista fixa no código.",
        ["CIS-AZC-2.0.0:2.1.17"] =
            "Pesquisa pendente. Dado buscado: a zona DNS privada (privatelink.azurewebsites.net) ligada aos endpoints privados do App Service. "
            + "Examinado: as conexões de endpoint privado do aplicativo são lidas. Falta ler os grupos de zonas DNS dos endpoints privados "
            + "(Microsoft.Network/privateEndpoints/privateDnsZoneGroups) e casar com o aplicativo.",
        ["CIS-AZC-2.0.0:15.6"] =
            "Pesquisa pendente. Dado buscado: a zona DNS privada dos endpoints privados das contas do Batch. Examinado: as conexões de "
            + "endpoint privado da conta são lidas. Falta ler os grupos de zonas DNS dos endpoints privados e casar com a conta.",
        ["CIS-AZ-6.0.0:8.1.10"] =
            "Pesquisa pendente. Dado buscado: o estado da recomendação “aplicar atualizações do sistema” do Defender para Nuvem. Examinado: "
            + "planos e iniciativa padrão (lidos). Falta examinar as avaliações (Microsoft.Security/assessments) por máquina na versão estável e "
            + "o volume dessa leitura antes de implementar.",

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
            KnightPlatform.Azure => "Ainda não implementado no KNIGHT.",
            _ => "Ainda não implementado no KNIGHT.",
        };
}
