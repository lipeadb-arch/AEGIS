using System;
using System.Collections.Generic;

namespace AegisScore.Application.Knight.Catalog;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Perfis e impactos dos controles de Intune, SharePoint/OneDrive, Fabric, Defender para
/// Office 365 e Purview: O PROBLEMA → POR QUE IMPORTA → O QUE SE ESPERA → O QUE O ACHADO NÃO COMPROVA. Textos autorais;
/// a documentação citada é só a oficial da Microsoft, com endereço conferido.
/// </summary>
public static class M365ServiceProfiles
{
    private static KnightControlReference Doc(string title, string url) => new("Microsoft Learn", null, title, url);

    // ---- Documentação oficial -----------------------------------------------------------------------------
    private static readonly KnightControlReference DocIntuneCompliance = Doc(
        "Políticas de conformidade do Intune (dispositivos sem política atribuída)",
        "https://learn.microsoft.com/en-us/intune/device-security/compliance/overview");
    private static readonly KnightControlReference DocIntuneSettingsApi = Doc(
        "Graph: deviceManagementSettings (secureByDefault)",
        "https://learn.microsoft.com/en-us/graph/api/resources/intune-deviceconfig-devicemanagementsettings");
    private static readonly KnightControlReference DocIntuneEnrollment = Doc(
        "Restrições de registro de dispositivos no Intune",
        "https://learn.microsoft.com/en-us/intune/device-enrollment/restrictions");
    private static readonly KnightControlReference DocSpoSettingsApi = Doc(
        "Graph: sharepointSettings",
        "https://learn.microsoft.com/en-us/graph/api/resources/sharepointsettings");
    private static readonly KnightControlReference DocSpoSharing = Doc(
        "Gerenciar o compartilhamento externo do SharePoint e do OneDrive",
        "https://learn.microsoft.com/en-us/sharepoint/turn-external-sharing-on-or-off");
    private static readonly KnightControlReference DocSpoB2B = Doc(
        "Integração do SharePoint e do OneDrive com o Microsoft Entra B2B",
        "https://learn.microsoft.com/en-us/sharepoint/sharepoint-azureb2b-integration");
    private static readonly KnightControlReference DocSpoTenant = Doc(
        "Set-SPOTenant (configurações do locatário do SharePoint)",
        "https://learn.microsoft.com/en-us/powershell/module/microsoft.online.sharepoint.powershell/set-spotenant");
    private static readonly KnightControlReference DocSpoDefaultLink = Doc(
        "Alterar o tipo de link padrão de compartilhamento",
        "https://learn.microsoft.com/en-us/sharepoint/change-default-sharing-link");
    private static readonly KnightControlReference DocSpoSecurityGroups = Doc(
        "Restringir o compartilhamento externo a grupos de segurança",
        "https://learn.microsoft.com/en-us/sharepoint/manage-security-groups");
    private static readonly KnightControlReference DocSpoSync = Doc(
        "Permitir a sincronização apenas em computadores de domínios específicos",
        "https://learn.microsoft.com/en-us/sharepoint/allow-syncing-only-on-specific-domains");
    private static readonly KnightControlReference DocSpoVirus = Doc(
        "Proteção antivírus integrada do SharePoint, OneDrive e Teams",
        "https://learn.microsoft.com/en-us/defender-office-365/anti-malware-protection-for-spo-odfb-teams-about");
    private static readonly KnightControlReference DocFabricApi = Doc(
        "API de administração do Fabric: listar configurações do locatário",
        "https://learn.microsoft.com/en-us/rest/api/fabric/admin/tenants/list-tenant-settings");
    private static readonly KnightControlReference DocFabricExport = Doc(
        "Configurações de exportação e compartilhamento do Fabric",
        "https://learn.microsoft.com/en-us/fabric/admin/service-admin-portal-export-sharing");
    private static readonly KnightControlReference DocFabricDeveloper = Doc(
        "Configurações de desenvolvedor do Fabric (entidades de serviço e ResourceKey)",
        "https://learn.microsoft.com/en-us/fabric/admin/service-admin-portal-developer");
    private static readonly KnightControlReference DocFabricInfoProtection = Doc(
        "Configurações de proteção de informações do Fabric",
        "https://learn.microsoft.com/en-us/fabric/admin/service-admin-portal-information-protection");
    private static readonly KnightControlReference DocFabricRPython = Doc(
        "Configurações de visuais R e Python do Fabric",
        "https://learn.microsoft.com/en-us/fabric/admin/service-admin-portal-r-python-visuals");
    private static readonly KnightControlReference DocFabricExternalData = Doc(
        "Compartilhamento externo de dados no Fabric",
        "https://learn.microsoft.com/en-us/fabric/governance/external-data-sharing-overview");
    private static readonly KnightControlReference DocSafeLinks = Doc(
        "Links Seguros no Microsoft Defender para Office 365",
        "https://learn.microsoft.com/en-us/defender-office-365/safe-links-about");
    private static readonly KnightControlReference DocSafeAttachments = Doc(
        "Anexos Seguros no Microsoft Defender para Office 365",
        "https://learn.microsoft.com/en-us/defender-office-365/safe-attachments-about");
    private static readonly KnightControlReference DocSafeAttachmentsSpo = Doc(
        "Anexos Seguros para SharePoint, OneDrive e Microsoft Teams",
        "https://learn.microsoft.com/en-us/defender-office-365/safe-attachments-for-spo-odfb-teams-about");
    private static readonly KnightControlReference DocAntiMalware = Doc(
        "Configurar políticas antimalware no EOP",
        "https://learn.microsoft.com/en-us/defender-office-365/anti-malware-policies-configure");
    private static readonly KnightControlReference DocOutboundSpam = Doc(
        "Configurar a filtragem de spam de saída",
        "https://learn.microsoft.com/en-us/defender-office-365/outbound-spam-policies-configure");
    private static readonly KnightControlReference DocInboundSpam = Doc(
        "Configurar políticas antispam no EOP",
        "https://learn.microsoft.com/en-us/defender-office-365/anti-spam-policies-configure");
    private static readonly KnightControlReference DocConnectionFilter = Doc(
        "Configurar a filtragem de conexão",
        "https://learn.microsoft.com/en-us/defender-office-365/connection-filter-policies-configure");
    private static readonly KnightControlReference DocAntiPhish = Doc(
        "Políticas antiphishing no Microsoft 365",
        "https://learn.microsoft.com/en-us/defender-office-365/anti-phishing-policies-about");
    private static readonly KnightControlReference DocSpf = Doc(
        "Configurar o SPF para evitar falsificação",
        "https://learn.microsoft.com/en-us/defender-office-365/email-authentication-spf-configure");
    private static readonly KnightControlReference DocDkim = Doc(
        "Configurar o DKIM para assinar mensagens do domínio",
        "https://learn.microsoft.com/en-us/defender-office-365/email-authentication-dkim-configure");
    private static readonly KnightControlReference DocDmarc = Doc(
        "Configurar o DMARC para validar o domínio do remetente",
        "https://learn.microsoft.com/en-us/defender-office-365/email-authentication-dmarc-configure");
    private static readonly KnightControlReference DocPriorityAccounts = Doc(
        "Recomendações de segurança para contas prioritárias",
        "https://learn.microsoft.com/en-us/defender-office-365/priority-accounts-security-recommendations");
    private static readonly KnightControlReference DocPresetPolicies = Doc(
        "Políticas de segurança predefinidas no EOP e no Defender para Office 365",
        "https://learn.microsoft.com/en-us/defender-office-365/preset-security-policies");
    private static readonly KnightControlReference DocZap = Doc(
        "Limpeza automática em zero hora (ZAP)",
        "https://learn.microsoft.com/en-us/defender-office-365/zero-hour-auto-purge");
    private static readonly KnightControlReference DocAudit = Doc(
        "Ligar ou desligar a auditoria no Microsoft Purview",
        "https://learn.microsoft.com/en-us/purview/audit-log-enable-disable");
    private static readonly KnightControlReference DocDlp = Doc(
        "Prevenção contra perda de dados do Microsoft Purview",
        "https://learn.microsoft.com/en-us/purview/dlp-learn-about-dlp");
    private static readonly KnightControlReference DocDlpTeams = Doc(
        "DLP e Microsoft Teams",
        "https://learn.microsoft.com/en-us/purview/dlp-microsoft-teams");
    private static readonly KnightControlReference DocDlpCopilot = Doc(
        "DLP para o local Microsoft 365 Copilot",
        "https://learn.microsoft.com/en-us/purview/dlp-microsoft365-copilot-location-learn-about");
    private static readonly KnightControlReference DocLabels = Doc(
        "Criar e publicar rótulos de confidencialidade",
        "https://learn.microsoft.com/en-us/purview/create-sensitivity-labels");

    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;
    private static KnightCapability[] Caps(params KnightCapability[] c) => c;

    // ---- Ressalvas comuns ---------------------------------------------------------------------------------
    private const string SpoAdmin =
        "Lido pela API administrativa do SharePoint, que só aceita aplicativo autenticado por certificado; sem certificado "
        + "configurado, o controle fica não avaliado com esse motivo.";

    private const string FabricScope =
        "Lido pela API de administração do Fabric. O controle não verifica itens nem workspaces, e não lê quem pertence aos "
        + "grupos de exceção.";

    private const string MdoReach =
        "Só contam as políticas que alcançam alguém: a padrão, para quem não tem outra, e as personalizadas pela regra "
        + "habilitada que as aplica. A associação aos grupos citados nas regras não é lida. O controle lê a configuração; "
        + "não afirma que alguma mensagem maliciosa tenha sido entregue.";

    public static IReadOnlyList<KnightControlProfile> All { get; } = new[]
    {
        // ---- Intune ------------------------------------------------------------------------------------
        new KnightControlProfile("AK-INT-001", KnightSecurityDomain.Governance,
            "A configuração de conformidade do Intune trata como conformes os dispositivos que não têm nenhuma política de conformidade atribuída.",
            "O acesso condicional que exige dispositivo conforme confia no veredito do Intune. Se a ausência de política vale como aprovação, um aparelho que nunca foi avaliado passa pela mesma porta que um aparelho verificado.",
            "Dispositivos sem política de conformidade atribuída marcados como não conformes (secureByDefault ligado).",
            "O controle lê a configuração do serviço; não conta quantos dispositivos estão sem política nem afirma que algum deles tenha acessado dados. O conteúdo das políticas de conformidade existentes não é avaliado aqui.",
            Docs(DocIntuneCompliance, DocIntuneSettingsApi), Caps(KnightCapability.IntuneServiceSettings)),

        new KnightControlProfile("AK-INT-002", KnightSecurityDomain.Governance,
            "A restrição de registro padrão do Intune aceita dispositivos pessoais em ao menos uma plataforma.",
            "Um dispositivo pessoal registrado recebe configurações e aplicativos corporativos, mas a organização pouco sabe sobre quem mais usa o aparelho e como ele é mantido. Na restrição padrão, a permissão vale para quem não está coberto por outra restrição.",
            "Na restrição padrão, cada plataforma bloqueada ou com o registro de dispositivo pessoal bloqueado; exceções tratadas em restrições por grupo.",
            "Só a restrição PADRÃO é avaliada; as restrições por grupo com prioridade maior ficam como evidência, e o controle não determina quem está em cada grupo. Não afirma que algum dispositivo pessoal esteja registrado.",
            Docs(DocIntuneEnrollment), Caps(KnightCapability.IntuneEnrollmentRestrictions)),

        // ---- SharePoint e OneDrive -------------------------------------------------------------------------
        new KnightControlProfile("AK-SPO-001", KnightSecurityDomain.Identity,
            "O SharePoint e o OneDrive aceitam protocolos de autenticação legada.",
            "Protocolos legados não suportam segundo fator nem as condições do acesso condicional: uma senha descoberta basta para autenticar por eles, mesmo quando a entrada interativa exige MFA.",
            "Protocolos de autenticação legada desligados no locatário, com acesso somente por autenticação moderna.",
            "Configuração não é uso: o controle não afirma que algum cliente legado tenha acessado o SharePoint. A leitura principal é a do Microsoft Graph; a da API administrativa só é usada quando a primeira não informa o valor.",
            Docs(DocSpoSettingsApi, DocSpoTenant), Caps(KnightCapability.SharePointTenantSettings, KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-002", KnightSecurityDomain.Identity,
            "A integração do SharePoint e do OneDrive com o Microsoft Entra B2B está desligada.",
            "Sem a integração, pessoas de fora convidadas pelo SharePoint usam um mecanismo próprio de código de verificação e não viram contas de convidado do diretório: ficam fora do acesso condicional, das revisões de acesso e do inventário de convidados.",
            "Integração com o Microsoft Entra B2B ligada.",
            SpoAdmin + " O controle não conta convidados existentes nem afirma que algum convite tenha sido feito.",
            Docs(DocSpoB2B), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-003", KnightSecurityDomain.DataProtection,
            "O compartilhamento externo do SharePoint permite links “qualquer pessoa”.",
            "Um link anônimo não identifica quem abre: pode ser encaminhado, publicado ou indexado, e cada acesso chega sem identidade para auditar ou bloquear.",
            "Nível de compartilhamento externo em “convidados novos e existentes” ou mais restrito, sem links “qualquer pessoa”.",
            "O controle lê o nível máximo do LOCATÁRIO; sites podem ter nível mais restrito. Não enumera links existentes nem afirma que algum arquivo tenha sido aberto por quem não deveria.",
            Docs(DocSpoSharing, DocSpoSettingsApi), Caps(KnightCapability.SharePointTenantSettings)),

        new KnightControlProfile("AK-SPO-004", KnightSecurityDomain.DataProtection,
            "O OneDrive permite compartilhar arquivos com pessoas de fora da organização.",
            "O OneDrive é o espaço pessoal de cada colaborador; permitir compartilhamento externo ali entrega a cada pessoa a decisão de expor conteúdo, sem a governança que um site do SharePoint tem.",
            "Compartilhamento do OneDrive restrito a pessoas da organização; colaboração externa por sites do SharePoint com dono e política.",
            SpoAdmin + " Não afirma que algum arquivo tenha sido compartilhado para fora.",
            Docs(DocSpoSharing, DocSpoTenant), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-005", KnightSecurityDomain.Collaboration,
            "Convidados podem compartilhar novamente arquivos, pastas e sites que não possuem.",
            "O recompartilhamento transfere para alguém de fora a decisão de quem acessa: o convidado passa a estender o acesso a terceiros que a organização nunca avaliou.",
            "Recompartilhamento por convidados desligado.",
            "Não se aplica quando o compartilhamento externo está desligado. O controle não enumera recompartilhamentos já feitos.",
            Docs(DocSpoSharing, DocSpoSettingsApi), Caps(KnightCapability.SharePointTenantSettings)),

        new KnightControlProfile("AK-SPO-006", KnightSecurityDomain.DataProtection,
            "O compartilhamento externo não é limitado a uma lista de domínios permitidos.",
            "Sem lista de permitidos, um convite pode ir para qualquer endereço — inclusive um e-mail pessoal ou digitado errado —, e a organização não controla com quem colabora.",
            "Restrição por domínio em lista de permitidos, com os domínios das organizações parceiras.",
            "Não se aplica quando o compartilhamento externo está desligado. Uma lista de bloqueados não atende: ela só impede domínios já conhecidos.",
            Docs(DocSpoSharing, DocSpoSettingsApi), Caps(KnightCapability.SharePointTenantSettings)),

        new KnightControlProfile("AK-SPO-007", KnightSecurityDomain.DataProtection,
            "O tipo de link sugerido ao compartilhar não é “pessoas específicas”.",
            "A maioria das pessoas aceita o link sugerido. Um padrão amplo (organização inteira ou qualquer pessoa) faz o alcance real do compartilhamento ser maior que o pretendido.",
            "Tipo de link padrão “pessoas específicas”.",
            SpoAdmin + " O padrão pode ser alterado por site; o controle lê o do locatário e não enumera links existentes.",
            Docs(DocSpoDefaultLink, DocSpoTenant), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-008", KnightSecurityDomain.Governance,
            "Qualquer usuário pode compartilhar com pessoas de fora; não há grupos de segurança designados para isso.",
            "Restringir o compartilhamento externo a grupos designados concentra a decisão em quem foi preparado e responde por ela, e reduz o número de contas cuja invasão permite expor conteúdo.",
            "Lista de grupos de segurança autorizados a compartilhar externamente, com ao menos um grupo.",
            "Não se aplica quando o compartilhamento externo está desligado. " + SpoAdmin + " O controle não verifica quem pertence aos grupos.",
            Docs(DocSpoSecurityGroups), Caps(KnightCapability.SharePointAdminTenant, KnightCapability.SharePointTenantSettings)),

        new KnightControlProfile("AK-SPO-009", KnightSecurityDomain.Identity,
            "O acesso de convidados a sites e ao OneDrive não expira em até 30 dias.",
            "Sem expiração, o acesso de um convidado dura enquanto ninguém lembrar de retirá-lo; a expiração obriga a uma renovação consciente.",
            "Expiração do acesso de convidados exigida, em até 30 dias.",
            SpoAdmin + " O controle não conta convidados com acesso antigo.",
            Docs(DocSpoSharing), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-010", KnightSecurityDomain.Identity,
            "Quem acessa com código de verificação não precisa se reautenticar em até 15 dias.",
            "O código de verificação prova o controle de um e-mail no momento do acesso. Sem reautenticação frequente, a sessão continua válida mesmo que a pessoa perca aquele e-mail.",
            "Reautenticação por código de verificação exigida, em até 15 dias.",
            SpoAdmin + " Com a integração B2B ligada, parte dos convidados entra por conta do diretório, e esta configuração não os alcança.",
            Docs(DocSpoSharing), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-011", KnightSecurityDomain.DataProtection,
            "A permissão sugerida nos links de compartilhamento é de edição.",
            "Quem compartilha raramente troca a permissão sugerida; com edição por padrão, destinatários ganham poder de alterar e excluir quando só precisavam ler.",
            "Permissão padrão dos links “exibir”.",
            SpoAdmin + " O controle não enumera links já emitidos com edição.",
            Docs(DocSpoDefaultLink, DocSpoTenant), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-012", KnightSecurityDomain.CloudProtection,
            "Arquivos identificados como infectados pelo antivírus do SharePoint podem ser baixados.",
            "A detecção só protege se o arquivo detectado não chegar ao dispositivo; permitir o download deixa a decisão final a quem pode não entender o aviso.",
            "Download de arquivo infectado bloqueado.",
            SpoAdmin + " Não afirma que haja arquivo infectado armazenado.",
            Docs(DocSpoVirus, DocSpoTenant), Caps(KnightCapability.SharePointAdminTenant)),

        new KnightControlProfile("AK-SPO-013", KnightSecurityDomain.Governance,
            "O aplicativo de sincronização do OneDrive funciona em computadores fora dos domínios autorizados.",
            "A sincronização copia bibliotecas inteiras para o disco local. Num computador não gerenciado, essa cópia fica fora da criptografia, do backup e do apagamento remoto da organização.",
            "Sincronização restrita a computadores ingressados em domínios autorizados, com ao menos um domínio listado.",
            "Lido pelo Microsoft Graph. O controle não identifica computadores que já sincronizam nem avalia o acesso pelo navegador ou por dispositivos móveis.",
            Docs(DocSpoSync, DocSpoSettingsApi), Caps(KnightCapability.SharePointTenantSettings)),

        // ---- Fabric ------------------------------------------------------------------------------------
        new KnightControlProfile("AK-FAB-001", KnightSecurityDomain.Identity,
            "Contas de convidado podem acessar o Microsoft Fabric sem restrição a grupos.",
            "Relatórios e modelos semânticos concentram dados de negócio já tratados. Liberar o acesso de convidados para a organização inteira faz de cada compartilhamento com um convidado uma saída de dados agregados.",
            "AllowGuestUserToAccessSharedContent desligada ou limitada a grupos de segurança designados.",
            FabricScope, Docs(DocFabricExport, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-002", KnightSecurityDomain.Identity,
            "Qualquer usuário pode convidar pessoas de fora ao compartilhar um item do Fabric.",
            "O convite pelo compartilhamento cria conta de convidado no diretório sem passar pelo fluxo de aprovação de convidados da organização.",
            "ExternalSharingV2 desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricExport, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-003", KnightSecurityDomain.Identity,
            "Convidados podem navegar pelo conteúdo do Fabric além do que foi compartilhado diretamente com eles.",
            "Com a navegação liberada, o convidado enxerga a lista de workspaces e itens a que tem acesso por grupos, e descobre conteúdo sem que ninguém tenha compartilhado nada novo.",
            "ElevatedGuestsTenant desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricExport, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-004", KnightSecurityDomain.DataProtection,
            "Qualquer usuário pode publicar relatórios do Fabric na web.",
            "“Publicar na web” gera um endereço público, sem autenticação, que qualquer pessoa na internet abre e que buscadores podem indexar.",
            "PublishToWeb desligada ou limitada a grupos designados.",
            FabricScope + " Não afirma que algum relatório esteja publicado.",
            Docs(DocFabricExport, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-005", KnightSecurityDomain.Governance,
            "Visuais que executam scripts R e Python estão habilitados no Fabric.",
            "Um visual R ou Python executa código embutido no relatório. O código é escrito por quem publica e roda sobre os dados do modelo, com pouca verificação sobre o que ele faz.",
            "RScriptVisual desligada.",
            FabricScope, Docs(DocFabricRPython, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-006", KnightSecurityDomain.DataProtection,
            "Usuários não podem aplicar rótulos de confidencialidade ao conteúdo do Fabric.",
            "Sem rótulo, dados exportados do Fabric perdem a classificação que tinham na origem, e as políticas de proteção do Purview não reconhecem o conteúdo.",
            "EimInformationProtectionEdit ligada.",
            FabricScope, Docs(DocFabricInfoProtection, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-007", KnightSecurityDomain.DataProtection,
            "Qualquer usuário pode criar links do Fabric que dão acesso a toda a organização.",
            "Um link para a organização inteira concede acesso a qualquer conta interna que o receba, inclusive por encaminhamento, sem que o dono do item saiba quem abriu.",
            "ShareLinkToEntireOrg desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricExport, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-008", KnightSecurityDomain.DataProtection,
            "Qualquer usuário pode compartilhar dados do OneLake com locatários externos.",
            "O compartilhamento externo de dados entrega o acesso aos dados, no próprio lugar, para outra organização: o que ela faz depois segue as regras de lá.",
            "AllowExternalDataSharingSwitch desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricExternalData, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-009", KnightSecurityDomain.Identity,
            "A autenticação por ResourceKey não está bloqueada no Fabric.",
            "Com ResourceKey, basta conhecer a chave de um conjunto de dados de streaming para enviar dados a ele: não há identidade, papel nem MFA envolvidos.",
            "BlockResourceKeyAuthentication ligada.",
            FabricScope, Docs(DocFabricDeveloper, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-010", KnightSecurityDomain.IamRbac,
            "Qualquer entidade de serviço do locatário pode chamar as APIs públicas do Fabric.",
            "Aplicações operam sem pessoa e sem MFA. Permitir que qualquer uma chame as APIs aumenta o número de credenciais cuja perda dá acesso programático ao Fabric.",
            "ServicePrincipalAccessGlobalAPIs desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricDeveloper, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-011", KnightSecurityDomain.IamRbac,
            "Entidades de serviço podem criar e usar perfis no Fabric sem restrição.",
            "Perfis de entidade de serviço servem a soluções multilocatário: cada perfil opera conteúdo em nome próprio. Sem restrição, qualquer aplicação cria identidades internas que a governança de usuários não vê.",
            "AllowServicePrincipalsCreateAndUseProfiles desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricDeveloper, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        new KnightControlProfile("AK-FAB-012", KnightSecurityDomain.IamRbac,
            "Entidades de serviço podem criar workspaces, conexões e pipelines de implantação sem restrição.",
            "Criar conexões e pipelines é dar a uma aplicação o poder de ligar o Fabric a fontes de dados e de publicar conteúdo sem revisão humana.",
            "ServicePrincipalAccessPermissionAPIs desligada ou limitada a grupos designados.",
            FabricScope, Docs(DocFabricDeveloper, DocFabricApi), Caps(KnightCapability.FabricTenantSettings)),

        // ---- Defender para Office 365 -------------------------------------------------------------------
        new KnightControlProfile("AK-MDO-001", KnightSecurityDomain.Collaboration,
            "Há política de Links Seguros efetiva que não cobre e-mail, Teams e aplicativos do Office, ou que permite seguir para o site original.",
            "Links maliciosos costumam ser ativados depois da entrega. A verificação no clique, em todos os canais, é o que alcança um endereço que só se tornou perigoso mais tarde.",
            "Toda política de Links Seguros efetiva com e-mail, Teams e Office ligados, verificação em tempo real, rastreamento de cliques e sem clique direto.",
            MdoReach, Docs(DocSafeLinks), Caps(KnightCapability.DefenderSafeLinks)),

        new KnightControlProfile("AK-MDO-002", KnightSecurityDomain.Collaboration,
            "Há política antimalware efetiva com o filtro de tipos comuns de anexo desligado.",
            "O filtro de tipos bloqueia pela extensão, antes de qualquer análise: é a barreira mais simples contra executáveis e scripts enviados como anexo.",
            "Toda política antimalware efetiva com o filtro de tipos de arquivo ligado.",
            MdoReach, Docs(DocAntiMalware), Caps(KnightCapability.DefenderMalwareFilter)),

        new KnightControlProfile("AK-MDO-003", KnightSecurityDomain.Logging,
            "Há política antimalware efetiva que não avisa um administrador quando alguém de dentro envia malware.",
            "Uma conta interna enviando malware é sinal de estação ou conta invadida. Sem o aviso, a equipe depende de outro alerta para perceber.",
            "Toda política antimalware efetiva notificando um endereço de administrador sobre remetentes internos de malware.",
            MdoReach, Docs(DocAntiMalware), Caps(KnightCapability.DefenderMalwareFilter)),

        new KnightControlProfile("AK-MDO-004", KnightSecurityDomain.Collaboration,
            "Há política de Anexos Seguros efetiva desligada ou sem ação de bloqueio.",
            "Anexos Seguros abrem o arquivo num ambiente isolado antes da entrega e pegam malware que as assinaturas ainda não conhecem; sem o bloqueio, o anexo detectado segue para a caixa.",
            "Toda política de Anexos Seguros efetiva ligada, com ação de bloqueio.",
            MdoReach, Docs(DocSafeAttachments), Caps(KnightCapability.DefenderSafeAttachments)),

        new KnightControlProfile("AK-MDO-005", KnightSecurityDomain.Collaboration,
            "Anexos Seguros não protegem o SharePoint, o OneDrive e o Teams, ou Documentos Seguros permite abrir arquivo marcado.",
            "Arquivos colocados em bibliotecas e conversas não passam pelo fluxo de e-mail; sem a proteção própria, um arquivo malicioso enviado ali não é bloqueado.",
            "Anexos Seguros para SharePoint, OneDrive e Teams ligados; Documentos Seguros, quando informado, ligado e sem permitir abrir arquivo marcado.",
            "Configuração global da organização. Documentos Seguros depende de licença; quando a coleta não o informa, só a proteção de SharePoint, OneDrive e Teams é exigida.",
            Docs(DocSafeAttachmentsSpo), Caps(KnightCapability.DefenderAtpPolicy)),

        new KnightControlProfile("AK-MDO-006", KnightSecurityDomain.Logging,
            "Há política antispam de saída efetiva que não notifica nem copia administradores quando um remetente é bloqueado por envio suspeito.",
            "Envio em massa a partir de uma conta interna é um dos primeiros sinais de conta invadida. O aviso encurta o tempo até a equipe agir.",
            "Toda política antispam de saída efetiva com notificação e cópia oculta a destinatários definidos.",
            MdoReach, Docs(DocOutboundSpam), Caps(KnightCapability.DefenderOutboundSpam)),

        new KnightControlProfile("AK-MDO-007", KnightSecurityDomain.Collaboration,
            "Há política antiphishing efetiva sem as proteções de representação, de inteligência de caixa de correio e de falsificação, ou com limiar baixo.",
            "Golpes de fraude se passam por executivos, colegas ou pelo próprio domínio. As proteções de representação e de inteligência são as que reconhecem essas imitações.",
            "Toda política antiphishing efetiva ligada, com limiar 2 ou maior, representação de usuários e dos domínios da organização, inteligência de caixa de correio e de falsificação, e quarentena nas ações de representação.",
            MdoReach, Docs(DocAntiPhish), Caps(KnightCapability.DefenderAntiPhish)),

        new KnightControlProfile("AK-MDO-008", KnightSecurityDomain.Collaboration,
            "Domínios de e-mail próprios sem um registro SPF único que autorize o Exchange Online.",
            "O SPF diz aos destinatários quais servidores podem enviar pelo domínio. Sem ele, ou com vários registros, a validação falha e mensagens forjadas ficam mais difíceis de recusar.",
            "Cada domínio aceito próprio (fora de onmicrosoft.com) com exatamente um registro SPF que inclui spf.protection.outlook.com.",
            "Consulta DNS pública feita pelo AEGIS: consulta não resolvida deixa o domínio sem veredito, nunca reprovado. Não valida o limite de pesquisas DNS do SPF nem outros remetentes autorizados. Domínios onmicrosoft.com são gerenciados pela Microsoft e ficam fora.",
            Docs(DocSpf), Caps(KnightCapability.DefenderAcceptedDomains, KnightCapability.DefenderDnsRecords)),

        new KnightControlProfile("AK-MDO-009", KnightSecurityDomain.Collaboration,
            "Domínios de e-mail próprios sem assinatura DKIM ligada no Exchange Online.",
            "A assinatura DKIM prova que a mensagem saiu da organização e não foi alterada no caminho; é também o que permite ao DMARC aprovar mensagens encaminhadas.",
            "Cada domínio aceito próprio com a configuração DKIM ligada.",
            "Lê a configuração DKIM do Exchange Online; não consulta no DNS os registros CNAME das chaves nem avalia a rotação delas.",
            Docs(DocDkim), Caps(KnightCapability.DefenderAcceptedDomains, KnightCapability.DefenderDkim)),

        new KnightControlProfile("AK-MDO-010", KnightSecurityDomain.Collaboration,
            "Domínios de e-mail próprios sem DMARC em quarentena ou rejeição para todas as mensagens.",
            "O DMARC diz aos destinatários o que fazer com uma mensagem que falha na autenticação. Sem política de quarentena ou rejeição, a mensagem forjada é aceita normalmente.",
            "Cada domínio aceito próprio com um único registro DMARC com p=quarantine ou p=reject e pct ausente ou 100.",
            "Consulta DNS pública (_dmarc do domínio) feita pelo AEGIS: consulta não resolvida deixa o domínio sem veredito. Não avalia relatórios DMARC nem o alinhamento de cada remetente.",
            Docs(DocDmarc), Caps(KnightCapability.DefenderAcceptedDomains, KnightCapability.DefenderDnsRecords)),

        new KnightControlProfile("AK-MDO-011", KnightSecurityDomain.Collaboration,
            "Há política antimalware efetiva cujo filtro de anexos não inclui todos os tipos de arquivo de alto risco da lista da referência.",
            "A lista padrão do filtro não cobre formatos usados hoje para entregar malware, como imagens de disco, atalhos e instaladores; um tipo fora da lista passa sem ser barrado pela extensão.",
            "Toda política antimalware efetiva com o filtro ligado e contendo cada uma das 184 extensões de alto risco da lista da referência.",
            "Usa a lista da referência, que ela mesma declara abrangente, mas não exaustiva: um tipo fora dela não é avaliado. " + MdoReach,
            Docs(DocAntiMalware), Caps(KnightCapability.DefenderMalwareFilter)),

        new KnightControlProfile("AK-MDO-012", KnightSecurityDomain.Collaboration,
            "O filtro de conexão tem endereços IP na lista de permitidos.",
            "Mensagens de IPs permitidos pulam a filtragem de spam. Se um desses endereços for de um serviço compartilhado ou for tomado, o atalho passa a valer para quem o usa.",
            "Filtro de conexão sem IPs permitidos; exceções tratadas por regras específicas e revisáveis.",
            "O filtro de conexão é único e vale para a organização inteira. O controle não afirma que algum desses endereços tenha enviado mensagem maliciosa.",
            Docs(DocConnectionFilter), Caps(KnightCapability.DefenderConnectionFilter)),

        new KnightControlProfile("AK-MDO-013", KnightSecurityDomain.Collaboration,
            "A lista segura do filtro de conexão está ligada.",
            "A lista segura é mantida pela Microsoft e isenta da filtragem remetentes que a organização não escolheu nem acompanha.",
            "Lista segura do filtro de conexão desligada.",
            "O filtro de conexão é único e vale para a organização inteira. O controle lê a configuração; não identifica mensagens aceitas por causa dela.",
            Docs(DocConnectionFilter), Caps(KnightCapability.DefenderConnectionFilter)),

        new KnightControlProfile("AK-MDO-014", KnightSecurityDomain.Collaboration,
            "Há política antispam de entrada efetiva com domínios de remetente permitidos.",
            "Um domínio permitido pula a filtragem de spam, e o endereço do remetente é fácil de forjar: a exceção vale para qualquer um que se apresente com aquele domínio.",
            "Nenhuma política antispam de entrada efetiva com domínios de remetente permitidos.",
            MdoReach, Docs(DocInboundSpam), Caps(KnightCapability.DefenderInboundSpam)),

        new KnightControlProfile("AK-MDO-015", KnightSecurityDomain.Collaboration,
            "Há política antispam de saída efetiva sem limites de envio definidos ou sem bloqueio do remetente ao atingi-los.",
            "Limites explícitos contêm o volume que uma conta invadida consegue enviar antes de ser parada; o bloqueio do usuário interrompe o envio automaticamente.",
            "Toda política antispam de saída efetiva com limites por hora (externos e internos) e por dia maiores que zero, e ação BlockUser.",
            "Valor 0 significa o padrão do serviço e não é tratado como limite definido pela organização. " + MdoReach,
            Docs(DocOutboundSpam), Caps(KnightCapability.DefenderOutboundSpam)),

        new KnightControlProfile("AK-MDO-016", KnightSecurityDomain.Identity,
            "A proteção de contas prioritárias está desligada ou nenhuma conta está marcada como prioritária.",
            "Executivos e administradores são os alvos preferidos de golpes dirigidos. Marcá-los como prioritários lhes dá filtragem diferenciada e destaque em alertas e relatórios.",
            "Proteção de contas prioritárias ligada, com as contas de executivos e de administração marcadas.",
            "Não afirma quais contas deveriam ser prioritárias: a escolha é da organização. A marcação é lida no Exchange Online.",
            Docs(DocPriorityAccounts), Caps(KnightCapability.DefenderPriorityAccounts)),

        new KnightControlProfile("AK-MDO-017", KnightSecurityDomain.Identity,
            "Contas prioritárias não estão incluídas nas regras da política de segurança predefinida estrita.",
            "A política estrita aplica os limiares mais rigorosos de spam, phishing e malware. Quem mais é visado deveria receber a proteção mais rigorosa.",
            "Cada conta prioritária incluída, por pessoa ou por domínio, nas duas regras estritas habilitadas (proteção de e-mail e Defender para Office 365).",
            "A inclusão é conferida por pessoa e por domínio. Quando as regras incluem GRUPOS, a associação não é lida e a conta fica sem veredito — nunca aprovada por suposição.",
            Docs(DocPresetPolicies, DocPriorityAccounts), Caps(KnightCapability.DefenderPriorityAccounts, KnightCapability.DefenderPresetPolicies)),

        new KnightControlProfile("AK-MDO-018", KnightSecurityDomain.Collaboration,
            "A limpeza automática em zero hora (ZAP) está desligada para mensagens do Teams.",
            "Algumas mensagens só são reconhecidas como maliciosas depois de entregues. O ZAP retira a mensagem do chat assim que o veredito muda.",
            "Política de proteção do Teams com ZAP ligado.",
            "A política de proteção do Teams depende de licença do Defender para Office 365; sem ela, o controle não é avaliado.",
            Docs(DocZap), Caps(KnightCapability.DefenderTeamsProtection)),

        // ---- Purview ------------------------------------------------------------------------------------
        new KnightControlProfile("AK-PUR-001", KnightSecurityDomain.Logging,
            "A ingestão do log de auditoria unificado do Microsoft 365 está desligada.",
            "O log unificado registra acessos, alterações e compartilhamentos em todos os serviços; sem ele, uma investigação não tem o que pesquisar.",
            "Ingestão do log de auditoria unificado ligada.",
            "Lido no Security & Compliance pela sessão de aplicativo. Não verifica a retenção nem se os eventos são exportados para um SIEM.",
            Docs(DocAudit), Caps(KnightCapability.PurviewAuditConfig)),

        new KnightControlProfile("AK-PUR-002", KnightSecurityDomain.DataProtection,
            "Nenhuma política de prevenção contra perda de dados (DLP) está em modo de aplicação.",
            "A DLP reconhece informação sensível — documentos pessoais, cartões, dados de saúde — e impede ou avisa antes que ela saia. Em modo de teste, ela só observa.",
            "Ao menos uma política de DLP habilitada, em modo de aplicação, para os tipos de informação sensível da organização.",
            "O controle verifica a EXISTÊNCIA de política em vigor; não avalia se os tipos de informação e os locais escolhidos são os adequados para a organização.",
            Docs(DocDlp), Caps(KnightCapability.PurviewDlpPolicies)),

        new KnightControlProfile("AK-PUR-003", KnightSecurityDomain.DataProtection,
            "Nenhuma política de DLP em vigor alcança todas as conversas e canais do Teams.",
            "O chat é onde informação sensível circula de forma informal. Uma política que não alcança o Teams deixa esse canal sem a proteção que o e-mail tem.",
            "Ao menos uma política de DLP em modo de aplicação com o local do Teams em “todos”.",
            "Políticas limitadas a algumas contas ou grupos ficam como evidência e não aprovam o critério.",
            Docs(DocDlpTeams), Caps(KnightCapability.PurviewDlpPolicies)),

        new KnightControlProfile("AK-PUR-004", KnightSecurityDomain.DataProtection,
            "Nenhuma política de DLP em vigor alcança o Microsoft 365 Copilot e o Copilot Chat.",
            "O Copilot resume e cita o conteúdo a que o usuário tem acesso. A DLP no local do Copilot impede que arquivos com rótulos restritos sejam processados nas respostas.",
            "Ao menos uma política de DLP em modo de aplicação no local Microsoft 365 Copilot e Copilot Chat.",
            "Não verifica se a organização tem licenças do Copilot. O local é reconhecido pelo identificador documentado pela Microsoft.",
            Docs(DocDlpCopilot), Caps(KnightCapability.PurviewDlpPolicies)),

        new KnightControlProfile("AK-PUR-005", KnightSecurityDomain.DataProtection,
            "Nenhuma política de rótulos de confidencialidade está publicada para os usuários.",
            "Rótulos são a base da classificação: sem eles publicados, as pessoas não conseguem marcar o que é confidencial, e a criptografia e a DLP por rótulo não têm o que usar.",
            "Ao menos uma política de rótulos habilitada, com rótulos e locais de publicação.",
            "Considera publicada a política habilitada com rótulos e locais; não avalia se os rótulos são usados nem a configuração de criptografia deles.",
            Docs(DocLabels), Caps(KnightCapability.PurviewLabelPolicies)),
    };

    /// <summary>Impacto potencial de cada controle deste bloco (ver <see cref="KnightControlImpacts"/>).</summary>
    public static IReadOnlyDictionary<string, string> Impacts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AK-INT-001"] = "Um notebook ou celular que nunca passou por verificação de criptografia, bloqueio de tela ou versão de sistema pode abrir e-mail e arquivos corporativos como se tivesse sido aprovado.",
        ["AK-INT-002"] = "Aparelhos de uso particular, compartilhados com familiares ou sem manutenção de segurança, passam a guardar cópias de mensagens e arquivos corporativos.",

        ["AK-SPO-001"] = "Bibliotecas e arquivos pessoais das contas atingidas podem ser lidos e baixados só com a senha, por um caminho que dispensa a verificação adicional pedida no navegador.",
        ["AK-SPO-002"] = "Terceiros com acesso a sites e arquivos continuam invisíveis às revisões de convidados, e ninguém é lembrado de retirar esse acesso quando a relação termina.",
        ["AK-SPO-003"] = "Um documento compartilhado por link pode ser aberto por quem receber o endereço, sem entrar, e não há como saber quem o leu.",
        ["AK-SPO-004"] = "Arquivos de trabalho guardados no espaço pessoal podem sair da organização por decisão individual, fora de qualquer site com dono e política.",
        ["AK-SPO-005"] = "Conteúdo enviado a um parceiro pode chegar, pelas mãos dele, a pessoas de outras organizações sem que ninguém da sua tenha autorizado.",
        ["AK-SPO-006"] = "Arquivos podem ser enviados a endereços pessoais ou de concorrentes, por engano ou de propósito, sem barreira técnica.",
        ["AK-SPO-007"] = "Um link enviado a uma pessoa passa a funcionar para quem o receber adiante, e o documento circula além de quem foi escolhido.",
        ["AK-SPO-008"] = "Uma conta invadida ou uma pessoa desatenta pode mandar conteúdo interno para fora sem passar por quem responde por essa decisão.",
        ["AK-SPO-009"] = "Pessoas que já encerraram a colaboração continuam abrindo sites e arquivos compartilhados meses depois.",
        ["AK-SPO-010"] = "Alguém que deixou a empresa parceira, mas mantém a sessão aberta, continua abrindo o conteúdo compartilhado.",
        ["AK-SPO-011"] = "Destinatários de um link podem modificar ou apagar documentos, e alterações indevidas se propagam para quem usa a versão compartilhada.",
        ["AK-SPO-012"] = "Um arquivo malicioso já identificado pode ser aberto no computador de quem o baixar, levando o código malicioso até a estação.",
        ["AK-SPO-013"] = "Cópias completas de bibliotecas podem ficar em computadores pessoais e permanecer lá depois da saída do colaborador.",

        ["AK-FAB-001"] = "Painéis com números financeiros, comerciais ou de clientes podem ser abertos por pessoas de outras organizações.",
        ["AK-FAB-002"] = "Contas de convidado passam a existir por decisão de quem compartilha um relatório, fora da aprovação que o diretório exige para terceiros.",
        ["AK-FAB-003"] = "Um terceiro descobre relatórios e conjuntos de dados que não lhe foram enviados e pode pedir ou obter acesso a eles.",
        ["AK-FAB-004"] = "Um relatório com números internos pode ficar acessível na internet para quem encontrar o endereço, sem registro de quem viu.",
        ["AK-FAB-005"] = "Um relatório pode carregar código que processa os dados do modelo de formas que ninguém revisou, e esse código se replica a cada cópia do relatório.",
        ["AK-FAB-006"] = "Uma planilha exportada de um relatório confidencial sai sem marcação nem criptografia, e a DLP não a distingue de um arquivo comum.",
        ["AK-FAB-007"] = "Relatórios destinados a uma área podem ser abertos por qualquer colaborador que obtenha o link, inclusive prestadores.",
        ["AK-FAB-008"] = "Tabelas do OneLake podem ser lidas por outro locatário, onde a retenção, a auditoria e o descarte não seguem as regras da organização.",
        ["AK-FAB-009"] = "Quem tiver a chave pode injetar números falsos em painéis de tempo real, distorcendo as decisões tomadas com base neles.",
        ["AK-FAB-010"] = "O segredo vazado de qualquer aplicação do diretório pode ser usado para ler ou alterar, por automação, os workspaces a que ela tenha acesso.",
        ["AK-FAB-011"] = "Workspaces e itens podem passar a pertencer a perfis criados por aplicações, sem dono humano que responda por eles.",
        ["AK-FAB-012"] = "Uma aplicação comprometida pode criar conexões com fontes de dados e publicar conteúdo em produção sem que uma pessoa aprove.",

        ["AK-MDO-001"] = "Um clique num link de phishing recebido por e-mail, chat do Teams ou documento do Office leva a pessoa direto à página falsa, sem verificação no momento do acesso.",
        ["AK-MDO-002"] = "Arquivos executáveis e scripts anexados chegam às caixas de correio e dependem só da atenção de quem os recebe.",
        ["AK-MDO-003"] = "Uma conta interna invadida pode continuar distribuindo malware aos colegas até que alguém perceba por outro caminho.",
        ["AK-MDO-004"] = "Anexos maliciosos inéditos chegam às caixas de correio e são abertos nas estações, já que nenhuma assinatura os reconhecia.",
        ["AK-MDO-005"] = "Um arquivo malicioso colocado numa biblioteca ou conversa pode ser aberto por várias pessoas da equipe que confiam no espaço compartilhado.",
        ["AK-MDO-006"] = "Uma conta interna invadida pode enviar spam ou phishing em nome da organização até que alguém de fora reclame, afetando a reputação dos domínios.",
        ["AK-MDO-007"] = "Mensagens que imitam um diretor ou um fornecedor chegam às caixas sem aviso, levando a pagamentos ou envio de informação a golpistas.",
        ["AK-MDO-008"] = "Destinatários externos têm menos como distinguir uma mensagem legítima do domínio de uma forjada, e as mensagens verdadeiras podem cair em spam.",
        ["AK-MDO-009"] = "Mensagens da organização encaminhadas por listas ou serviços intermediários tendem a falhar na autenticação e ser recusadas ou marcadas como suspeitas.",
        ["AK-MDO-010"] = "Criminosos podem enviar a clientes e parceiros mensagens com o endereço exato do domínio da organização, entregues na caixa de entrada.",
        ["AK-MDO-011"] = "Anexos em formatos como .iso, .lnk ou .msi podem chegar às caixas e iniciar a execução de código ao serem abertos.",
        ["AK-MDO-012"] = "Spam e phishing enviados a partir dos endereços liberados chegam às caixas sem a filtragem que as outras mensagens recebem.",
        ["AK-MDO-013"] = "Mensagens de remetentes que a organização não avaliou chegam sem passar pela filtragem de spam.",
        ["AK-MDO-014"] = "Um golpista que forje o endereço de um domínio da lista entrega mensagens às caixas sem filtragem.",
        ["AK-MDO-015"] = "Uma conta interna invadida pode disparar milhares de mensagens antes de ser contida, levando os domínios da organização a listas de bloqueio.",
        ["AK-MDO-016"] = "Um golpe dirigido a um executivo ou administrador aparece misturado aos demais alertas e pode demorar a ser tratado.",
        ["AK-MDO-017"] = "As contas mais visadas recebem o mesmo nível de filtragem das demais, e golpes elaborados contra elas têm mais chance de chegar.",
        ["AK-MDO-018"] = "Um link ou arquivo malicioso enviado por chat continua disponível para ser aberto mesmo depois de identificado como perigoso.",

        ["AK-PUR-001"] = "Depois de um incidente, não haverá registro de quem abriu, baixou ou compartilhou o quê, e o alcance do vazamento não poderá ser determinado.",
        ["AK-PUR-002"] = "Planilhas com CPFs ou números de cartão podem ser enviadas por e-mail ou compartilhadas sem nenhum bloqueio ou aviso.",
        ["AK-PUR-003"] = "Dados sensíveis colados numa conversa com um convidado externo saem da organização sem que nenhuma regra os reconheça.",
        ["AK-PUR-004"] = "Respostas do assistente podem resumir documentos confidenciais para quem os pediu, espalhando trechos deles em chats e e-mails.",
        ["AK-PUR-005"] = "Documentos confidenciais circulam sem marcação nem criptografia, e quem os recebe não tem indicação de como tratá-los.",
    };
}
