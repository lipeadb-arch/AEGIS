using System.Collections.Generic;

namespace AegisScore.Application.Knight.Catalog;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Perfis dos controles de configuração do Entra ID: O PROBLEMA → POR QUE IMPORTA → O
/// QUE SE ESPERA → O QUE O ACHADO NÃO COMPROVA. Textos autorais; referências só a documentação oficial conferida.
/// </summary>
public static class EntraConfigurationProfiles
{
    private static KnightControlReference Doc(string title, string url) => new("Microsoft Learn", null, title, url);

    private static readonly KnightControlReference DocDefaultPermissions = Doc("Permissões padrão de usuários no Microsoft Entra ID",
        "https://learn.microsoft.com/en-us/entra/fundamentals/users-default-permissions");
    private static readonly KnightControlReference DocUserConsent = Doc("Configurar o consentimento de usuários a aplicações",
        "https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/configure-user-consent");
    private static readonly KnightControlReference DocAdminConsent = Doc("Configurar o fluxo de consentimento do administrador",
        "https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/configure-admin-consent-workflow");
    private static readonly KnightControlReference DocGuestAccess = Doc("Restringir as permissões de acesso de convidados",
        "https://learn.microsoft.com/en-us/entra/identity/users/users-restrict-guest-permissions");
    private static readonly KnightControlReference DocExternalCollab = Doc("Configurar as definições de colaboração externa",
        "https://learn.microsoft.com/en-us/entra/external-id/external-collaboration-settings-configure");
    private static readonly KnightControlReference DocAppPolicy = Doc("Política de gerenciamento de aplicações do locatário",
        "https://learn.microsoft.com/en-us/graph/api/resources/tenantappmanagementpolicy");
    private static readonly KnightControlReference DocNumberMatch = Doc("Correspondência de números nas notificações de MFA",
        "https://learn.microsoft.com/en-us/entra/identity/authentication/how-to-mfa-number-match");
    private static readonly KnightControlReference DocAuthMethods = Doc("Gerenciar métodos de autenticação",
        "https://learn.microsoft.com/en-us/entra/identity/authentication/concept-authentication-methods-manage");
    private static readonly KnightControlReference DocBannedPasswords = Doc("Eliminar senhas fracas com a proteção de senha",
        "https://learn.microsoft.com/en-us/entra/identity/authentication/concept-password-ban-bad");
    private static readonly KnightControlReference DocBannedOnPrem = Doc("Proteção de senha para o Active Directory local",
        "https://learn.microsoft.com/en-us/entra/identity/authentication/concept-password-ban-bad-on-premises");
    private static readonly KnightControlReference DocSmartLockout = Doc("Bloqueio inteligente do Microsoft Entra",
        "https://learn.microsoft.com/en-us/entra/identity/authentication/howto-password-smart-lockout");
    private static readonly KnightControlReference DocGroupCreation = Doc("Gerenciar quem pode criar grupos do Microsoft 365",
        "https://learn.microsoft.com/en-us/microsoft-365/solutions/manage-creation-of-groups");
    private static readonly KnightControlReference DocPasswordExpiration = Doc("Definir a política de expiração de senhas",
        "https://learn.microsoft.com/en-us/microsoft-365/admin/manage/set-password-expiration-policy");
    private static readonly KnightControlReference DocPhs = Doc("Sincronização de hash de senha",
        "https://learn.microsoft.com/en-us/entra/identity/hybrid/connect/whatis-phs");
    private static readonly KnightControlReference DocDevices = Doc("Gerenciar identidades de dispositivos",
        "https://learn.microsoft.com/en-us/entra/identity/devices/manage-device-identities");
    private static readonly KnightControlReference DocLaps = Doc("LAPS do Microsoft Entra",
        "https://learn.microsoft.com/en-us/entra/identity/devices/howto-manage-local-admin-passwords");
    private static readonly KnightControlReference DocRoles = Doc("Boas práticas para papéis do Microsoft Entra",
        "https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/best-practices");
    private static readonly KnightControlReference DocPim = Doc("Configurar papéis no Privileged Identity Management",
        "https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-configure");
    private static readonly KnightControlReference DocAccessReviews = Doc("Revisões de acesso do Microsoft Entra",
        "https://learn.microsoft.com/en-us/entra/id-governance/access-reviews-overview");
    private static readonly KnightControlReference DocLocations = Doc("Condição de localização no acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-assignment-network");
    private static readonly KnightControlReference DocSession = Doc("Gerenciamento de sessão com acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-session-lifetime");
    private static readonly KnightControlReference DocAuthStrength = Doc("Forças de autenticação do acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/authentication/concept-authentication-strengths");
    private static readonly KnightControlReference DocRiskPolicies = Doc("Políticas de risco do Identity Protection",
        "https://learn.microsoft.com/en-us/entra/id-protection/howto-identity-protection-configure-risk-policies");
    private static readonly KnightControlReference DocDeviceCompliance = Doc("Exigir dispositivo em conformidade com acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/policy-all-users-device-compliance");
    private static readonly KnightControlReference DocSecurityInfo = Doc("Proteger o registro de informações de segurança",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/policy-all-users-security-info-registration");
    private static readonly KnightControlReference DocAuthFlows = Doc("Fluxos de autenticação no acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-authentication-flows");
    private static readonly KnightControlReference DocGroupsVisibility = Doc("Gerenciar grupos do Microsoft 365",
        "https://learn.microsoft.com/en-us/microsoft-365/admin/create-groups/manage-groups");

    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;
    private static KnightCapability[] Caps(params KnightCapability[] c) => c;

    public static IReadOnlyList<KnightControlProfile> All { get; } = new[]
    {
        new KnightControlProfile("AK-ENTRA-016", KnightSecurityDomain.Governance,
            "Qualquer usuário do diretório pode registrar aplicações.",
            "Uma aplicação registrada por um usuário pode receber credenciais e pedir permissões; sem controle, ela vira um caminho de persistência e de coleta de dados fora da governança.",
            "Registro de aplicações restrito a papéis autorizados.", null,
            Docs(DocDefaultPermissions), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-017", KnightSecurityDomain.Governance,
            "Usuários sem papel administrativo podem criar novos locatários do Microsoft Entra.",
            "O criador de um locatário vira o administrador dele. Locatários criados fora do processo ficam fora do inventário, das políticas e da auditoria da organização.",
            "Criação de locatários restrita a administradores.", null,
            Docs(DocDefaultPermissions), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-018", KnightSecurityDomain.Governance,
            "Usuários comuns podem criar grupos de segurança.",
            "Grupos de segurança concedem acesso a recursos; criados sem governança, multiplicam atribuições difíceis de revisar.",
            "Criação de grupos de segurança restrita a administradores ou a processos aprovados.", null,
            Docs(DocDefaultPermissions), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-019", KnightSecurityDomain.DataProtection,
            "Usuários podem ler as chaves de recuperação do BitLocker dos próprios dispositivos.",
            "Quem obtém a chave de recuperação consegue desbloquear o disco fora do controle da organização — inclusive quem tomou a conta do usuário.",
            "Recuperação de chaves do BitLocker feita pelo suporte, não pelo próprio usuário.", null,
            Docs(DocDefaultPermissions), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-020", KnightSecurityDomain.IamRbac,
            "Usuários podem consentir, em nome próprio, que aplicações acessem dados da organização.",
            "O phishing de consentimento não rouba senha: convence o usuário a autorizar uma aplicação maliciosa, que passa a ler dados com as permissões concedidas, mesmo com MFA.",
            "Consentimento do usuário não permitido; pedidos tratados pelo fluxo de consentimento do administrador.",
            "A configuração não remove consentimentos já concedidos (tenant-wide eles aparecem no AK-ENTRA-013).",
            Docs(DocUserConsent), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-021", KnightSecurityDomain.Identity,
            "Convidados têm o mesmo acesso de leitura ao diretório que os membros.",
            "Um convidado com acesso amplo enumera usuários, grupos e estruturas da organização — informação útil para ataques direcionados.",
            "Acesso de convidados limitado ou restrito às propriedades dos próprios objetos.", null,
            Docs(DocGuestAccess), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-022", KnightSecurityDomain.Identity,
            "Membros comuns (ou qualquer pessoa) podem convidar convidados.",
            "Convites sem controle trazem identidades externas sem dono, sem aprovação e sem revisão.",
            "Convites restritos a administradores e ao papel Emissor de Convites.", null,
            Docs(DocExternalCollab), Caps(KnightCapability.AuthorizationPolicy)),

        new KnightControlProfile("AK-ENTRA-023", KnightSecurityDomain.IamRbac,
            "O fluxo de consentimento do administrador está desabilitado ou sem revisores.",
            "Sem um caminho formal, pedidos de acesso de aplicações são negados sem registro ou concedidos por atalho, sem análise das permissões.",
            "Fluxo de consentimento do administrador habilitado com revisores definidos.", null,
            Docs(DocAdminConsent), Caps(KnightCapability.AdminConsentPolicy)),

        new KnightControlProfile("AK-ENTRA-024", KnightSecurityDomain.Secrets,
            "Aplicações podem receber novos segredos (senhas).",
            "Segredos de aplicação são credenciais estáticas, fáceis de vazar em código e configurações, e dão acesso sem usuário e sem MFA.",
            "Adição de senhas bloqueada pela política padrão; uso de certificados ou identidades gerenciadas.",
            "A política padrão não se aplica a aplicações com política própria atribuída.",
            Docs(DocAppPolicy), Caps(KnightCapability.AppManagementPolicy)),

        new KnightControlProfile("AK-ENTRA-025", KnightSecurityDomain.Secrets,
            "Segredos de aplicações podem viver mais de 180 dias.",
            "Quanto mais longa a vida útil, maior a janela em que um segredo vazado continua funcionando.",
            "Vida útil máxima de segredos de até 180 dias na política padrão.",
            "A política padrão não se aplica a aplicações com política própria atribuída.",
            Docs(DocAppPolicy), Caps(KnightCapability.AppManagementPolicy)),

        new KnightControlProfile("AK-ENTRA-026", KnightSecurityDomain.Secrets,
            "Segredos de aplicações podem ser definidos manualmente.",
            "Segredos escolhidos por pessoas tendem a ser previsíveis ou reaproveitados.",
            "Segredos de aplicações sempre gerados pelo sistema.",
            "A política padrão não se aplica a aplicações com política própria atribuída.",
            Docs(DocAppPolicy), Caps(KnightCapability.AppManagementPolicy)),

        new KnightControlProfile("AK-ENTRA-027", KnightSecurityDomain.Secrets,
            "Aplicações registradas com certificados de vigência acima de 180 dias.",
            "Certificados longevos ficam esquecidos e, se a chave privada vazar, garantem acesso por muito tempo sem rotação.",
            "Certificados de aplicações com vigência de até 180 dias, rotacionados antes do fim.",
            "Mede a vigência declarada do certificado (início ao fim), não o uso nem a exposição da chave.",
            Docs(DocAppPolicy), Caps(KnightCapability.ApplicationInventory)),

        new KnightControlProfile("AK-ENTRA-028", KnightSecurityDomain.Identity,
            "O Microsoft Authenticator não fixa, para todos os usuários, o nome do aplicativo e a localização nas notificações de MFA.",
            "Ataques de fadiga repetem pedidos de aprovação até o usuário aceitar. Nome do aplicativo e localização dão ao usuário contexto para reconhecer e recusar o pedido falso.",
            "Nome do aplicativo e localização geográfica habilitados para todos os usuários (a correspondência de números já é aplicada pela plataforma).",
            "Um estado “gerenciado pela Microsoft” pode estar ativo hoje, mas não é o valor fixado que a referência pede.",
            Docs(DocNumberMatch, DocAuthMethods), Caps(KnightCapability.AuthenticationMethodsPolicy)),

        new KnightControlProfile("AK-ENTRA-029", KnightSecurityDomain.Identity,
            "SMS ou chamada de voz estão habilitados como método de autenticação.",
            "Códigos por telefone podem ser interceptados por troca de chip, engenharia social com a operadora ou páginas falsas em tempo real.",
            "SMS e chamada de voz desabilitados; usuários migrados para métodos mais fortes.", null,
            Docs(DocAuthMethods), Caps(KnightCapability.AuthenticationMethodsPolicy)),

        new KnightControlProfile("AK-ENTRA-030", KnightSecurityDomain.Governance,
            "As atribuições do papel Criador de Locatário não têm revisão periódica de acesso.",
            "Quem tem o papel continua podendo criar locatários mesmo com a restrição geral ativa; sem revisão, a atribuição permanece depois que a necessidade acaba.",
            "Revisão recorrente (mensal ou mais frequente) das atribuições do papel, com remoção do acesso negado.", null,
            Docs(DocAccessReviews), Caps(KnightCapability.AccessReviews)),

        new KnightControlProfile("AK-ENTRA-031", KnightSecurityDomain.Identity,
            "O método de senha única por e-mail está habilitado.",
            "O código chega a uma caixa de correio que pode estar comprometida junto com a conta.",
            "Método de senha única por e-mail desabilitado.", null,
            Docs(DocAuthMethods), Caps(KnightCapability.AuthenticationMethodsPolicy)),

        new KnightControlProfile("AK-ENTRA-033", KnightSecurityDomain.Identity,
            "A lista personalizada de senhas proibidas não está em uso.",
            "Senhas com o nome da empresa, produtos ou cidade são as primeiras tentadas em ataques de senha.",
            "Lista personalizada ativa, com termos da organização.",
            "A lista global da Microsoft continua ativa; o controle trata só da lista personalizada.",
            Docs(DocBannedPasswords), Caps(KnightCapability.DirectorySettings)),

        new KnightControlProfile("AK-ENTRA-034", KnightSecurityDomain.Identity,
            "A proteção de senha não está imposta no Active Directory local.",
            "Em ambiente híbrido, senhas fracas definidas no AD local chegam à nuvem pela sincronização.",
            "Proteção de senha habilitada no modo Impor.",
            "Mede a configuração no locatário; a instalação dos agentes nos controladores de domínio não é verificável por esta coleta.",
            Docs(DocBannedOnPrem), Caps(KnightCapability.DirectorySettings, KnightCapability.DirectorySynchronization)),

        new KnightControlProfile("AK-ENTRA-035", KnightSecurityDomain.Identity,
            "O limite de tentativas antes do bloqueio está acima de 10.",
            "Um limite alto dá mais tentativas a ataques de adivinhação de senha antes do bloqueio.",
            "Limite de bloqueio de até 10 tentativas.", null,
            Docs(DocSmartLockout), Caps(KnightCapability.DirectorySettings)),

        new KnightControlProfile("AK-ENTRA-036", KnightSecurityDomain.Identity,
            "A duração do bloqueio está abaixo de 60 segundos.",
            "Um bloqueio curto permite retomar os ataques de senha quase imediatamente.",
            "Duração do bloqueio de ao menos 60 segundos.", null,
            Docs(DocSmartLockout), Caps(KnightCapability.DirectorySettings)),

        new KnightControlProfile("AK-ENTRA-037", KnightSecurityDomain.Collaboration,
            "Qualquer usuário pode criar grupos do Microsoft 365.",
            "Cada grupo cria caixa de correio, site e equipe; sem governança, conteúdo e acessos se espalham sem dono.",
            "Criação de grupos restrita (ou liberada só a um grupo autorizado).", null,
            Docs(DocGroupCreation), Caps(KnightCapability.DirectorySettings)),

        new KnightControlProfile("AK-ENTRA-038", KnightSecurityDomain.Identity,
            "Senhas expiram periodicamente nos domínios gerenciados.",
            "A troca forçada leva a senhas previsíveis (a mesma com um número a mais); a orientação atual é não expirar e proteger com MFA e bloqueio de senhas vazadas.",
            "Senhas sem expiração periódica nos domínios gerenciados.",
            "Domínios federados seguem a política do provedor de identidade federado e não são avaliados aqui.",
            Docs(DocPasswordExpiration), Caps(KnightCapability.Domains)),

        new KnightControlProfile("AK-ENTRA-039", KnightSecurityDomain.Identity,
            "A sincronização de hash de senha está desabilitada num diretório híbrido.",
            "Sem ela, a detecção de credenciais vazadas não funciona e a autenticação em nuvem depende da infraestrutura local.",
            "Sincronização de hash de senha habilitada.", null,
            Docs(DocPhs), Caps(KnightCapability.DirectorySynchronization)),

        new KnightControlProfile("AK-ENTRA-040", KnightSecurityDomain.Identity,
            "Registrar ou ingressar dispositivos não exige MFA.",
            "Com só a senha, um atacante registra o próprio dispositivo e passa a cumprir condições de acesso baseadas em dispositivo.",
            "MFA exigida no registro de dispositivos, pela política de dispositivos ou por acesso condicional.", null,
            Docs(DocDevices), Caps(KnightCapability.DeviceRegistrationPolicy, KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-041", KnightSecurityDomain.Governance,
            "Qualquer usuário pode ingressar dispositivos no Microsoft Entra.",
            "Dispositivos ingressados ganham confiança no diretório; sem restrição, dispositivos pessoais ou de atacantes entram no inventário.",
            "Ingresso restrito a usuários ou grupos selecionados.", null,
            Docs(DocDevices), Caps(KnightCapability.DeviceRegistrationPolicy)),

        new KnightControlProfile("AK-ENTRA-042", KnightSecurityDomain.Governance,
            "Cada usuário pode registrar muitos dispositivos.",
            "Uma cota alta facilita o registro de dispositivos indevidos com uma única conta comprometida.",
            "Até 10 dispositivos por usuário.", null,
            Docs(DocDevices), Caps(KnightCapability.DeviceRegistrationPolicy)),

        new KnightControlProfile("AK-ENTRA-043", KnightSecurityDomain.IamRbac,
            "Administradores globais recebem direitos de administrador local em todos os dispositivos ingressados.",
            "Amplia o alcance de uma conta global comprometida para todos os dispositivos, e expõe credenciais globais em máquinas de usuários.",
            "Administradores globais não adicionados como administradores locais.", null,
            Docs(DocDevices), Caps(KnightCapability.DeviceRegistrationPolicy)),

        new KnightControlProfile("AK-ENTRA-044", KnightSecurityDomain.IamRbac,
            "Todo usuário que ingressa um dispositivo vira administrador local dele.",
            "Administrador local desativa proteções, instala software e extrai credenciais do dispositivo.",
            "Administradores locais adicionais restritos a selecionados ou a ninguém.", null,
            Docs(DocDevices), Caps(KnightCapability.DeviceRegistrationPolicy)),

        new KnightControlProfile("AK-ENTRA-045", KnightSecurityDomain.Secrets,
            "A solução de senha de administrador local (LAPS) está desabilitada.",
            "Sem rotação, a mesma senha de administrador local tende a se repetir entre máquinas e permite movimento lateral.",
            "LAPS habilitada no locatário e aplicada nos dispositivos.",
            "Mede a habilitação no locatário; a aplicação em cada dispositivo depende da política de gerenciamento.",
            Docs(DocLaps), Caps(KnightCapability.DeviceRegistrationPolicy)),

        new KnightControlProfile("AK-ENTRA-046", KnightSecurityDomain.Collaboration,
            "Há grupos do Microsoft 365 públicos.",
            "Em um grupo público, qualquer pessoa da organização entra e lê arquivos, conversas e calendário do grupo.",
            "Grupos públicos só quando aprovados; os demais privados.",
            "A aprovação de um grupo público não é verificável por API — a lista é material de revisão, não prova de irregularidade.",
            Docs(DocGroupsVisibility), Caps(KnightCapability.GroupVisibility)),

        new KnightControlProfile("AK-ENTRA-047", KnightSecurityDomain.IamRbac,
            "Contas com papel privilegiado são sincronizadas do Active Directory local.",
            "Quem compromete o domínio local controla a conta sincronizada e, com ela, o administrador de nuvem.",
            "Contas administrativas somente em nuvem.", null,
            Docs(DocRoles), Caps(KnightCapability.PrivilegedAccountDetails, KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-048", KnightSecurityDomain.IamRbac,
            "Contas administrativas têm licenças de e-mail, colaboração ou Office.",
            "Uma conta administrativa que lê e-mail e navega em arquivos compartilhados fica exposta a phishing e a conteúdo malicioso.",
            "Contas administrativas só com licenças necessárias à administração.",
            "Avalia os planos de serviço efetivos das licenças atribuídas; não avalia o uso real dos serviços.",
            Docs(DocRoles), Caps(KnightCapability.PrivilegedAccountDetails, KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-049", KnightSecurityDomain.IamRbac,
            "O número de administradores globais está fora do intervalo de 2 a 4.",
            "Poucos administradores arriscam a continuidade; muitos ampliam a superfície do papel mais poderoso do locatário.",
            "Entre dois e quatro administradores globais.",
            "Conta os membros ativos do papel; atribuições elegíveis (PIM) são tratadas no AK-ENTRA-050.",
            Docs(DocRoles), Caps(KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-050", KnightSecurityDomain.IamRbac,
            "Papéis privilegiados têm atribuições ativas permanentes.",
            "Privilégio permanente fica disponível o tempo todo para quem roubar a credencial; com ativação sob demanda, o acesso existe só quando é usado e deixa registro.",
            "Atribuições privilegiadas elegíveis no PIM; permanentes só para contas de emergência.",
            "Contas de emergência podem justificar atribuição permanente e não são identificáveis por API — confirme cada caso.",
            Docs(DocPim), Caps(KnightCapability.PrivilegedIdentityManagement)),

        new KnightControlProfile("AK-ENTRA-051", KnightSecurityDomain.IamRbac,
            "Ativar o papel Administrador Global não exige aprovação.",
            "Sem aprovação, a ativação do papel mais poderoso depende só da credencial de quem é elegível.",
            "Aprovação exigida, com ao menos dois aprovadores.", null,
            Docs(DocPim), Caps(KnightCapability.PrivilegedIdentityManagement)),

        new KnightControlProfile("AK-ENTRA-052", KnightSecurityDomain.IamRbac,
            "Ativar o papel Administrador de Função Privilegiada não exige aprovação.",
            "Quem ativa este papel pode atribuir qualquer outro, inclusive Administrador Global.",
            "Aprovação exigida, com ao menos dois aprovadores.", null,
            Docs(DocPim), Caps(KnightCapability.PrivilegedIdentityManagement)),

        new KnightControlProfile("AK-ENTRA-053", KnightSecurityDomain.Governance,
            "Não há revisão periódica do acesso de convidados.",
            "Convidados permanecem com acesso depois que o projeto ou o contrato termina, sem ninguém confirmar a necessidade.",
            "Revisão recorrente (mensal ou mais frequente) de todos os convidados, com remoção do acesso negado.", null,
            Docs(DocAccessReviews), Caps(KnightCapability.AccessReviews)),

        new KnightControlProfile("AK-ENTRA-054", KnightSecurityDomain.Governance,
            "Papéis altamente privilegiados sem revisão periódica de acesso.",
            "Atribuições privilegiadas acumulam-se com mudanças de função; a revisão é o que as remove.",
            "Revisão recorrente para Administrador Global, do Exchange, do SharePoint, do Teams e de Segurança.", null,
            Docs(DocAccessReviews), Caps(KnightCapability.AccessReviews)),

        new KnightControlProfile("AK-ENTRA-055", KnightSecurityDomain.Network,
            "Não há local nomeado marcado como confiável.",
            "Sem locais confiáveis, políticas e detecções não conseguem distinguir a rede da organização de uma rede qualquer.",
            "Locais nomeados confiáveis definidos para a rede da organização.", null,
            Docs(DocLocations), Caps(KnightCapability.NamedLocations)),

        new KnightControlProfile("AK-ENTRA-056", KnightSecurityDomain.Network,
            "Nenhuma política restringe o acesso por geografia.",
            "Bloquear países onde a organização não opera reduz tentativas de acesso de origem improvável.",
            "Política habilitada que bloqueia locais por país (ou permite só a partir deles), quando aplicável ao negócio.",
            "A referência pede que a política seja considerada; ela pode não se aplicar a organizações com operação global.",
            Docs(DocLocations), Caps(KnightCapability.NamedLocations, KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-057", KnightSecurityDomain.DataProtection,
            "Usuários podem usar serviços de armazenamento de terceiros no Microsoft 365 na web.",
            "Arquivos abertos e salvos em serviços de terceiros saem do controle de retenção, DLP e auditoria da organização.",
            "Armazenamento de terceiros desabilitado no Microsoft 365 na web.", null,
            Docs(), Caps(KnightCapability.ServicePrincipalSettings)),

        new KnightControlProfile("AK-ENTRA-058", KnightSecurityDomain.Identity,
            "Administradores não têm frequência de entrada curta e sessão não persistente.",
            "Uma sessão administrativa longa ou persistente num navegador aumenta o valor de um token roubado.",
            "Frequência de entrada de até 4 horas e navegador nunca persistente para administradores.", null,
            Docs(DocSession), Caps(KnightCapability.ConditionalAccessPolicies, KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-059", KnightSecurityDomain.Identity,
            "Administradores não precisam de MFA resistente a phishing.",
            "MFA comum pode ser contornada por páginas falsas que repassam a autenticação em tempo real; métodos resistentes a phishing não.",
            "Força de autenticação resistente a phishing exigida de administradores em todas as aplicações.", null,
            Docs(DocAuthStrength), Caps(KnightCapability.ConditionalAccessPolicies, KnightCapability.PrivilegedRoleInventory)),

        new KnightControlProfile("AK-ENTRA-060", KnightSecurityDomain.Identity,
            "Não há política que responda ao risco alto de usuário.",
            "Usuário de risco alto é uma conta provavelmente comprometida; sem resposta automática, ela continua usável.",
            "Troca de senha com MFA exigida em risco alto de usuário.",
            "A avaliação de risco depende do licenciamento do Identity Protection.",
            Docs(DocRiskPolicies), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-061", KnightSecurityDomain.Identity,
            "Não há política que exija MFA em entradas de risco médio e alto.",
            "Entradas de risco indicam uso anômalo; sem exigência adicional, a senha sozinha basta.",
            "MFA exigida em entradas de risco médio e alto.",
            "A avaliação de risco depende do licenciamento do Identity Protection.",
            Docs(DocRiskPolicies), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-062", KnightSecurityDomain.Identity,
            "Entradas de risco médio e alto não são bloqueadas.",
            "Bloquear o risco elevado interrompe o uso da credencial enquanto a investigação acontece.",
            "Bloqueio de entradas de risco médio e alto.",
            "A avaliação de risco depende do licenciamento do Identity Protection.",
            Docs(DocRiskPolicies), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-063", KnightSecurityDomain.Identity,
            "O acesso não exige dispositivo gerenciado.",
            "Sem exigência de dispositivo, uma credencial roubada funciona a partir de qualquer máquina.",
            "Dispositivo em conformidade ou ingressado exigido para todos os usuários e aplicações.", null,
            Docs(DocDeviceCompliance), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-064", KnightSecurityDomain.Identity,
            "Registrar informações de segurança não exige dispositivo gerenciado.",
            "Quem registra o próprio método de MFA numa conta roubada garante acesso persistente.",
            "Dispositivo gerenciado exigido na ação de registrar informações de segurança.", null,
            Docs(DocSecurityInfo), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-065", KnightSecurityDomain.Identity,
            "O registro de dispositivos no Intune não exige reautenticação a cada vez.",
            "Uma sessão reaproveitada permite registrar um dispositivo indevido como gerenciado.",
            "MFA e frequência de entrada “a cada vez” no aplicativo de registro do Intune.", null,
            Docs(DocSession), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-066", KnightSecurityDomain.Identity,
            "O fluxo de código de dispositivo não está bloqueado.",
            "O fluxo de código de dispositivo é explorado em phishing: a vítima digita um código e entrega a sessão ao atacante.",
            "Fluxo de código de dispositivo bloqueado para todos, com exceções só para cenários aprovados.", null,
            Docs(DocAuthFlows), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-067", KnightSecurityDomain.Identity,
            "Não há reautenticação periódica.",
            "Sessões sem limite mantêm tokens válidos por muito tempo depois de um roubo.",
            "Frequência de entrada de até 7 dias para todos.", null,
            Docs(DocSession), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-068", KnightSecurityDomain.Identity,
            "A transferência de autenticação entre dispositivos não está bloqueada.",
            "A transferência move uma sessão autenticada para outro dispositivo — útil a quem tenta levar a sessão para fora do controle.",
            "Transferência de autenticação bloqueada.", null,
            Docs(DocAuthFlows), Caps(KnightCapability.ConditionalAccessPolicies)),

        new KnightControlProfile("AK-ENTRA-069", KnightSecurityDomain.Identity,
            "O acesso pelo navegador não aplica restrições de sessão ociosa.",
            "Sessões esquecidas abertas em computadores compartilhados ou não gerenciados ficam disponíveis a quem sentar depois.",
            "Tempo limite de sessão ociosa configurado e política com restrições impostas pelo aplicativo no navegador.",
            "A configuração de tempo limite do Microsoft 365, parte do critério de referência, ainda não é lida.",
            Docs(DocSession), Caps(KnightCapability.ConditionalAccessPolicies)),
    };
}
