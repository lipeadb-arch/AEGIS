using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Catalog;

/// <summary>
/// [AEGIS-KNIGHT-CLOSURE-01] Perfis e impactos dos controles do fechamento (leituras em versão preview, sessão ociosa,
/// mensagens denunciadas, referências ao Key Vault e API dos workspaces do Databricks): O PROBLEMA → POR QUE IMPORTA → O QUE
/// SE ESPERA → O QUE O ACHADO NÃO COMPROVA. Textos autorais; documentação só da Microsoft.
/// </summary>
public static class ClosureProfiles
{
    private static KnightControlReference Doc(string title, string url) => new("Microsoft Learn", null, title, url);
    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;

    private const string L = "https://learn.microsoft.com/en-us/";

    private static readonly KnightControlReference DocDiagSettings = Doc("Configurações de diagnóstico no Azure Monitor", L + "azure/azure-monitor/essentials/diagnostic-settings");
    private static readonly KnightControlReference DocDiagApi = Doc("API: listar configurações de diagnóstico (2021-05-01-preview)", L + "rest/api/monitor/diagnostic-settings/list?view=rest-monitor-2021-05-01-preview");
    private static readonly KnightControlReference DocSubDiagApi = Doc("API: configurações de diagnóstico da assinatura (2021-05-01-preview)", L + "rest/api/monitor/subscription-diagnostic-settings/list?view=rest-monitor-2021-05-01-preview");
    private static readonly KnightControlReference DocActivityLog = Doc("Log de atividades do Azure", L + "azure/azure-monitor/essentials/activity-log");
    private static readonly KnightControlReference DocCmkStorage = Doc("Chaves gerenciadas pelo cliente no Armazenamento do Azure", L + "azure/storage/common/customer-managed-keys-overview");
    private static readonly KnightControlReference DocKvLogging = Doc("Log do Azure Key Vault", L + "azure/key-vault/general/logging");
    private static readonly KnightControlReference DocCosmosLogs = Doc("Monitorar o Azure Cosmos DB com logs de recurso", L + "azure/cosmos-db/monitor-resource-logs");
    private static readonly KnightControlReference DocBatchLogs = Doc("Logs de diagnóstico do Batch", L + "azure/batch/batch-diagnostics");
    private static readonly KnightControlReference DocDbrDiag = Doc("Logs de diagnóstico do Azure Databricks", L + "azure/databricks/admin/account-settings/audit-logs");
    private static readonly KnightControlReference DocResourceLogs = Doc("Logs de recurso do Azure", L + "azure/azure-monitor/essentials/resource-logs");
    private static readonly KnightControlReference DocMdcEmail = Doc("Configurar notificações por e-mail do Defender para Nuvem", L + "azure/defender-for-cloud/configure-email-notifications");
    private static readonly KnightControlReference DocContactsApi = Doc("API: contatos de segurança (2023-12-01-preview)", L + "rest/api/defenderforcloud/security-contacts/list?view=rest-defenderforcloud-2023-12-01-preview");
    private static readonly KnightControlReference DocKvRefs = Doc("Referências ao Key Vault no App Service", L + "azure/app-service/app-service-key-vault-references");
    private static readonly KnightControlReference DocKvRefsApi = Doc("API: referências ao Key Vault das configurações do aplicativo", L + "rest/api/appservice/web-apps/get-app-settings-key-vault-references");
    private static readonly KnightControlReference DocUnity = Doc("O que é o Unity Catalog", L + "azure/databricks/data-governance/unity-catalog/");
    private static readonly KnightControlReference DocDbrTokens = Doc("Monitorar e revogar tokens de acesso pessoal", L + "azure/databricks/admin/access-control/tokens");
    private static readonly KnightControlReference DocDbrScim = Doc("Sincronizar usuários e grupos do Microsoft Entra ID com o Databricks", L + "azure/databricks/admin/users-groups/scim/aad");
    private static readonly KnightControlReference DocDbrEncrypt = Doc("Criptografar o tráfego entre os nós de trabalho do cluster", L + "azure/databricks/security/keys/encrypt-otw");
    private static readonly KnightControlReference DocDbrSp = Doc("Tokens do Microsoft Entra ID para entidades de serviço no Databricks", L + "azure/databricks/dev-tools/auth/oauth-m2m");
    private static readonly KnightControlReference DocPerUserMfa = Doc("Habilitar a MFA por usuário (legado) e migrar para acesso condicional", L + "entra/identity/authentication/howto-mfa-userstates");
    private static readonly KnightControlReference DocPerUserMfaApi = Doc("API beta: strongAuthenticationRequirements", L + "graph/api/resources/strongauthenticationrequirements?view=graph-rest-beta");
    private static readonly KnightControlReference DocSystemPreferred = Doc("MFA preferencial do sistema", L + "entra/identity/authentication/concept-system-preferred-multifactor-authentication");
    private static readonly KnightControlReference DocAuthenticatorFeatures = Doc("Recursos da política do Microsoft Authenticator", L + "entra/identity/authentication/how-to-mfa-additional-context");
    private static readonly KnightControlReference DocAuthMethodsBeta = Doc("API beta: authenticationMethodsPolicy", L + "graph/api/resources/authenticationmethodspolicy?view=graph-rest-beta");
    private static readonly KnightControlReference DocAppsServicesApi = Doc("API beta: adminAppsAndServices", L + "graph/api/adminappsandservices-get?view=graph-rest-beta");
    private static readonly KnightControlReference DocSelfService = Doc("Gerenciar compras e avaliações self-service", L + "microsoft-365/commerce/subscriptions/manage-self-service-purchases-admins");
    private static readonly KnightControlReference DocFormsApi = Doc("API beta: adminForms", L + "graph/api/adminforms-get?view=graph-rest-beta");
    private static readonly KnightControlReference DocFormsPhishing = Doc("Administrar o Microsoft Forms (proteção contra phishing)", L + "microsoft-forms/administrator-settings-microsoft-forms");
    private static readonly KnightControlReference DocIdleTimeout = Doc("Tempo limite de sessão ociosa para aplicativos web do Microsoft 365", L + "microsoft-365/admin/manage/idle-session-timeout-web-apps");
    private static readonly KnightControlReference DocActivityTimeoutApi = Doc("API: activityBasedTimeoutPolicy", L + "graph/api/resources/activitybasedtimeoutpolicy");
    private static readonly KnightControlReference DocUserReported = Doc("Configurações de mensagens denunciadas pelos usuários", L + "defender-office-365/submissions-user-reported-messages-custom-mailbox");
    private static readonly KnightControlReference DocReportSubmission = Doc("Get-ReportSubmissionPolicy", L + "powershell/module/exchangepowershell/get-reportsubmissionpolicy");

    private static readonly List<KnightControlProfile> Built = new();
    private static readonly Dictionary<string, string> BuiltImpacts = new(StringComparer.Ordinal);

    private static void P(string id, KnightSecurityDomain d, string description, string rationale, string expected, string? doesNotProve,
        string impact, KnightControlReference[] docs, params KnightCapability[] caps)
    {
        Built.Add(new KnightControlProfile(id, d, description, rationale, expected, doesNotProve, docs, caps));
        BuiltImpacts[id] = impact;
    }

    /// <summary>Perfil do Azure: a descoberta das assinaturas vem antes de toda família.</summary>
    private static void Az(string id, KnightSecurityDomain d, string description, string rationale, string expected, string? doesNotProve,
        string impact, KnightControlReference[] docs, params KnightCapability[] caps) =>
        P(id, d, description, rationale, expected, doesNotProve, impact, docs, caps.Prepend(KnightCapability.AzureSubscriptions).Distinct().ToArray());

    private const string Preview = " A leitura usa versão preview da API (indicada nas informações técnicas): indisponibilidade ou mudança de contrato deixa o controle não avaliado.";

    static ClosureProfiles()
    {
        // ======== Azure Monitor ==========================================================================
        Az("AK-AZ-MON-017", KnightSecurityDomain.Logging,
            "O log de atividades das assinaturas não é exportado para nenhum destino.",
            "O log de atividades registra quem criou, alterou ou apagou recursos e permissões; dentro do Azure ele fica só 90 dias e não pode ser consultado junto com outras fontes.",
            "Configuração de diagnóstico da assinatura enviando o log de atividades ao Log Analytics, a uma conta de armazenamento ou a um hub de eventos.",
            "Verifica a existência da configuração com destino; não verifica a retenção do destino nem se alguém consulta os registros." + Preview,
            "Alterações de permissão ou exclusões feitas há mais de três meses não poderão ser atribuídas a ninguém numa investigação.",
            Docs(DocActivityLog, DocSubDiagApi), KnightCapability.AzureActivityLogExport);
        Az("AK-AZ-MON-018", KnightSecurityDomain.Logging,
            "A exportação do log de atividades não inclui as categorias Administrative, Alert, Policy e Security.",
            "Cada categoria cobre um tipo de evento: operações de gerenciamento, disparos de alerta, decisões de política e alertas do Defender. Faltando uma, a trilha exportada tem buracos justamente onde o evento de interesse estaria.",
            "As quatro categorias habilitadas numa configuração da assinatura com destino.",
            "Considera qualquer destino; não verifica se as demais categorias (ServiceHealth, Recommendation, Autoscale, ResourceHealth) estão habilitadas." + Preview,
            "Eventos de política negada ou alertas de segurança ficam fora do registro consultável e não entram na correlação com outras fontes.",
            Docs(DocActivityLog, DocDiagSettings), KnightCapability.AzureActivityLogExport);
        Az("AK-AZ-MON-019", KnightSecurityDomain.Logging,
            "A conta de armazenamento que recebe o log de atividades usa chave gerenciada pela Microsoft.",
            "O log de atividades é a evidência de auditoria das assinaturas; com chave do cliente, quem administra a conta de armazenamento não consegue ler os registros sem acesso à chave, e a chave pode ser revogada.",
            "Conta de armazenamento de destino com criptografia por chave gerenciada pelo cliente no Key Vault.",
            "Avalia só exportações para conta de armazenamento; conta de destino fora das assinaturas lidas fica desconhecida." + Preview,
            "Quem obtiver acesso à conta de destino pode ler o histórico de operações das assinaturas sem precisar de acesso à chave de criptografia.",
            Docs(DocCmkStorage, DocSubDiagApi), KnightCapability.AzureActivityLogExport, KnightCapability.AzureStorage);
        Az("AK-AZ-MON-020", KnightSecurityDomain.Logging,
            "Recursos que emitem logs de recurso não os exportam por configuração de diagnóstico.",
            "Logs de recurso registram o que acontece DENTRO de cada serviço (acessos a segredos, consultas, conexões); o Azure não os guarda por padrão — sem configuração de diagnóstico eles simplesmente não existem.",
            "Configuração de diagnóstico com categoria de log habilitada e destino em cada recurso dos tipos que emitem logs.",
            "Examina os tipos de recurso que o KNIGHT lê (lista nas notas da referência); recursos de outros tipos não entram." + Preview,
            "Uso indevido de um banco, cofre ou aplicativo não deixa registro de atividade do próprio serviço para investigação.",
            Docs(DocResourceLogs, DocDiagApi), KnightCapability.AzureResourceDiagnostics, KnightCapability.AzureKeyVault, KnightCapability.AzureDatabases,
            KnightCapability.AzureCompute, KnightCapability.AzureDatabricks, KnightCapability.AzureAppService, KnightCapability.AzureNetworking, KnightCapability.AzureStorage);

        // ======== Logs de recurso por serviço ==============================================================
        Az("AK-AZ-KV-011", KnightSecurityDomain.Secrets,
            "Cofres de chaves não exportam a categoria de auditoria (AuditEvent).",
            "O AuditEvent registra cada leitura de segredo, chave ou certificado e quem a fez; é o único registro de quem acessou um segredo.",
            "Configuração de diagnóstico do cofre com AuditEvent (ou o grupo audit/allLogs) e destino retido.",
            "Verifica a configuração de diagnóstico do cofre; não verifica a retenção do destino." + Preview,
            "A leitura de um segredo por uma identidade comprometida não deixa registro de quem leu nem quando.",
            Docs(DocKvLogging, DocDiagApi), KnightCapability.AzureKeyVault, KnightCapability.AzureResourceDiagnostics);
        Az("AK-AZ-DB-056", KnightSecurityDomain.Logging,
            "Contas do Cosmos DB não exportam logs de diagnóstico.",
            "Os logs do Cosmos DB registram requisições ao plano de dados e de controle; sem eles, consultas anômalas e alterações de chave não ficam registradas fora do serviço.",
            "Configuração de diagnóstico da conta com categorias de log habilitadas e destino.",
            "Aceita qualquer categoria de log habilitada; não verifica quais operações de dados são registradas." + Preview,
            "Uma extração de dados por chave vazada não pode ser reconstruída a partir dos registros do próprio banco.",
            Docs(DocCosmosLogs, DocDiagApi), KnightCapability.AzureDatabases, KnightCapability.AzureResourceDiagnostics);
        Az("AK-AZ-CMP-018", KnightSecurityDomain.Logging,
            "Contas do Batch não exportam logs de diagnóstico.",
            "Os logs do Batch registram eventos de pools, tarefas e trabalhos; sem eles, a execução de cargas não autorizadas na conta não tem trilha.",
            "Configuração de diagnóstico da conta do Batch com categorias de log habilitadas e destino.",
            "Aceita qualquer categoria de log habilitada; não verifica a retenção do destino." + Preview,
            "Uso da capacidade de computação da conta para cargas não autorizadas pode passar sem registro consultável.",
            Docs(DocBatchLogs, DocDiagApi), KnightCapability.AzureCompute, KnightCapability.AzureResourceDiagnostics);
        Az("AK-AZ-DBR-007", KnightSecurityDomain.Logging,
            "Workspaces do Databricks não entregam logs de diagnóstico.",
            "Os logs de diagnóstico do Databricks registram ações de usuários no workspace (clusters, notebooks, segredos, permissões); sem entrega, essa atividade não sai do serviço.",
            "Configuração de diagnóstico do workspace com categorias de log habilitadas e destino (camada Premium).",
            "Aceita qualquer categoria de log habilitada; workspaces na camada Standard não emitem esses logs." + Preview,
            "Acesso indevido a notebooks ou segredos do workspace não pode ser reconstruído depois.",
            Docs(DocDbrDiag, DocDiagApi), KnightCapability.AzureDatabricks, KnightCapability.AzureResourceDiagnostics);

        // ======== Defender para Nuvem: notificações ========================================================
        Az("AK-AZ-MDC-021", KnightSecurityDomain.CloudProtection,
            "Os proprietários das assinaturas não recebem os alertas do Defender para Nuvem por e-mail.",
            "Os proprietários decidem e respondem pelo que roda na assinatura; sem a notificação, um alerta de alta severidade pode esperar até alguém abrir o portal.",
            "Notificação por papel ligada para o papel Owner nas configurações de e-mail do Defender para Nuvem.",
            "Verifica a configuração de notificação; não verifica se as caixas dos proprietários são lidas." + Preview,
            "Um ataque em andamento detectado pelo Defender pode continuar por horas sem que o responsável pela assinatura saiba.",
            Docs(DocMdcEmail, DocContactsApi), KnightCapability.AzureSecurityContacts);
        Az("AK-AZ-MDC-022", KnightSecurityDomain.CloudProtection,
            "Não há endereço de contato de segurança nas notificações do Defender para Nuvem.",
            "O contato de segurança é o canal da equipe que trata incidentes; sem ele, os alertas dependem de quem por acaso tem papel na assinatura.",
            "Ao menos um endereço de contato de segurança (de preferência uma caixa compartilhada da equipe).",
            "Conta os endereços configurados sem guardá-los; não verifica se a caixa existe nem se é monitorada." + Preview,
            "Alertas enviados só aos papéis da assinatura podem não chegar à equipe que sabe responder a um incidente.",
            Docs(DocMdcEmail, DocContactsApi), KnightCapability.AzureSecurityContacts);
        Az("AK-AZ-MDC-023", KnightSecurityDomain.CloudProtection,
            "A notificação de alertas do Defender para Nuvem por severidade está desligada.",
            "Sem a origem de notificação de alertas, o Defender para Nuvem não envia e-mail para alertas novos, nem os de severidade alta.",
            "Origem de notificação de alertas com severidade mínima alta (ou menor, que inclui as altas).",
            "Verifica a configuração; não verifica a entrega dos e-mails." + Preview,
            "Detecções de severidade alta ficam visíveis só no portal e podem ser vistas tarde demais para conter o dano.",
            Docs(DocMdcEmail, DocContactsApi), KnightCapability.AzureSecurityContacts);
        Az("AK-AZ-MDC-024", KnightSecurityDomain.CloudProtection,
            "A notificação de caminhos de ataque do Defender para Nuvem está desligada.",
            "Caminhos de ataque combinam vulnerabilidades e configurações que levam a um ativo crítico; a notificação avisa quando um caminho novo aparece.",
            "Origem de notificação de caminhos de ataque com nível de risco mínimo definido.",
            "Verifica a configuração; o recurso de caminhos de ataque exige o plano Defender CSPM." + Preview,
            "Uma combinação de falhas que abre acesso a um ativo crítico pode existir sem que a equipe seja avisada.",
            Docs(DocMdcEmail, DocContactsApi), KnightCapability.AzureSecurityContacts);

        // ======== App Service: referências ao Key Vault ====================================================
        Az("AK-AZ-APP-027", KnightSecurityDomain.Secrets,
            "Configurações de aplicativos do App Service referenciam o Key Vault, mas a referência não resolve.",
            "Uma referência que não resolve (identidade sem acesso, segredo removido, versão inexistente) deixa o aplicativo sem o segredo — e costuma levar a equipe a colar o valor direto na configuração para resolver a pane.",
            "Toda referência ao Key Vault das configurações com estado Resolved.",
            "Avalia só as configurações que já são referências ao Key Vault; se outras configurações guardam segredo em texto não é visível sem a leitura que devolve os valores, que o AEGIS não usa.",
            "O aplicativo pode falhar ao iniciar ou operar sem a credencial correta, e a correção apressada tende a expor o segredo na configuração.",
            Docs(DocKvRefs, DocKvRefsApi), KnightCapability.AzureAppService, KnightCapability.AzureAppSettingsKeyVaultReferences);

        // ======== Databricks: API do workspace =============================================================
        const string DbxApi = " Depende da leitura da API do workspace habilitada em Integrações e da aplicação adicionada ao workspace como administradora.";
        Az("AK-AZ-DBR-008", KnightSecurityDomain.DataProtection,
            "Workspaces do Databricks sem metastore do Unity Catalog.",
            "Sem o Unity Catalog, permissões de dados ficam em mecanismos por workspace (tabelas legadas e ACLs de cluster), sem governança centralizada, linhagem nem auditoria unificada.",
            "Metastore do Unity Catalog atribuído a cada workspace.",
            "Verifica a atribuição do metastore; não avalia as permissões concedidas dentro do catálogo." + DbxApi,
            "Dados sensíveis podem ser lidos por quem tem acesso a um cluster, sem controle por tabela e sem registro centralizado.",
            Docs(DocUnity, DocDbrSp), KnightCapability.AzureDatabricks, KnightCapability.AzureDatabricksWorkspaceApi);
        Az("AK-AZ-DBR-009", KnightSecurityDomain.Identity,
            "Tokens pessoais do Databricks podem ser usados por todos os usuários ou não expiram.",
            "Um token pessoal dá acesso à API do workspace sem MFA; liberado a todos e sem tempo de vida máximo, ele se torna uma credencial de longa duração fácil de vazar em scripts.",
            "Tokens desabilitados, ou uso restrito a grupos definidos e tempo de vida máximo configurado.",
            "Lê a configuração do workspace e as permissões de uso; não lista os tokens existentes nem a idade deles." + DbxApi,
            "Um token esquecido num repositório ou estação continua dando acesso ao workspace até alguém revogá-lo.",
            Docs(DocDbrTokens, DocDbrSp), KnightCapability.AzureDatabricks, KnightCapability.AzureDatabricksWorkspaceApi);
        Az("AK-AZ-DBR-010", KnightSecurityDomain.Identity,
            "Usuários ou grupos do Databricks não vêm do provisionamento a partir do Microsoft Entra ID.",
            "Contas criadas direto no workspace não acompanham o ciclo de vida do diretório: quem sai da empresa continua com acesso até alguém lembrar de removê-lo.",
            "Todos os usuários e grupos do workspace com vínculo externo do provisionamento a partir do Entra ID.",
            "Usa o vínculo externo (externalId) como prova do provisionamento; o gerenciamento automático de identidades no nível da conta não é lido." + DbxApi,
            "Uma pessoa desligada pode continuar acessando dados e notebooks do workspace depois de removida do diretório.",
            Docs(DocDbrScim, DocDbrSp), KnightCapability.AzureDatabricks, KnightCapability.AzureDatabricksWorkspaceApi);
        Az("AK-AZ-DBR-011", KnightSecurityDomain.Network,
            "Clusters do Databricks sem criptografia do tráfego entre os nós de trabalho.",
            "Por padrão o tráfego entre os nós de um cluster trafega sem criptografia dentro da rede; dados embaralhados entre nós (shuffle) podem ser capturados por quem estiver na mesma rede.",
            "Criptografia de rede do Spark ligada em todos os clusters (normalmente por script de inicialização).",
            "Lê a configuração Spark dos clusters; o conteúdo dos scripts de inicialização não é lido, e cluster que depende de script fica desconhecido." + DbxApi,
            "Dados processados no cluster podem ser interceptados em trânsito por um ponto de presença indevido na rede virtual.",
            Docs(DocDbrEncrypt, DocDbrSp), KnightCapability.AzureDatabricks, KnightCapability.AzureDatabricksWorkspaceApi);

        // ======== Entra ID e Microsoft 365 =================================================================
        P("AK-ENTRA-071", KnightSecurityDomain.Identity,
            "Contas habilitadas usam a MFA por usuário (legado) em vez do acesso condicional.",
            "A MFA por usuário é o mecanismo antigo, configurado conta a conta: ignora os sinais de risco e de dispositivo do acesso condicional e convive mal com ele, gerando exigências duplicadas ou lacunas difíceis de ver.",
            "MFA por usuário desabilitada em todas as contas, com a MFA exigida por acesso condicional (ou security defaults).",
            "Lê o estado de cada conta habilitada, uma a uma, na versão beta do Microsoft Graph; não verifica se o acesso condicional cobre as contas listadas.",
            "Contas configuradas fora do acesso condicional podem escapar das exigências de risco e de dispositivo que valem para as demais.",
            Docs(DocPerUserMfa, DocPerUserMfaApi), KnightCapability.PerUserMfaStates);
        P("AK-ENTRA-072", KnightSecurityDomain.Identity,
            "A MFA preferencial do sistema não está habilitada explicitamente para todos os usuários.",
            "Com ela, o Entra pede o método mais forte que a pessoa registrou, em vez do que ela escolheu por último — o que afasta SMS e voz quando há opção melhor.",
            "systemCredentialPreferences habilitada, com alvo em todos os usuários.",
            "Exige a configuração explícita: o estado padrão gerenciado pela Microsoft não é tratado como habilitado, porque pode mudar sem aviso.",
            "Usuários com Authenticator registrado ainda podem ser desafiados por SMS, método vulnerável a troca de chip.",
            Docs(DocSystemPreferred, DocAuthMethodsBeta), KnightCapability.AuthenticationMethodsPolicyPreview);
        P("AK-ENTRA-073", KnightSecurityDomain.Identity,
            "O Microsoft Authenticator pode ser usado em aplicativos complementares (como o Outlook) para aprovar entradas.",
            "Aprovar a entrada a partir de um aplicativo complementar no mesmo aparelho enfraquece a separação entre o fator e o dispositivo em uso.",
            "companionAppAllowedState desabilitado explicitamente na política do Microsoft Authenticator.",
            "Exige a configuração explícita: o estado padrão gerenciado pela Microsoft não é tratado como desabilitado.",
            "Uma sessão de aplicativo comprometida no aparelho pode servir também para aprovar a entrada que deveria protegê-la.",
            Docs(DocAuthenticatorFeatures, DocAuthMethodsBeta), KnightCapability.AuthenticationMethodsPolicyPreview);
        P("AK-ENTRA-074", KnightSecurityDomain.Governance,
            "Usuários podem obter aplicativos da Office Store ou iniciar avaliações de serviços por conta própria.",
            "Aplicativos e avaliações iniciados pelo usuário passam a tratar dados da organização sem revisão de segurança, contrato ou registro no inventário de TI.",
            "Office Store e avaliações iniciadas pelos usuários bloqueadas nas configurações da organização.",
            "Lê as duas opções da organização; não lista aplicativos já obtidos pelos usuários.",
            "Dados corporativos podem ser enviados a serviços que ninguém da TI aprovou nem sabe que existem.",
            Docs(DocSelfService, DocAppsServicesApi), KnightCapability.M365AppsAndServicesSettings);
        P("AK-FORMS-001", KnightSecurityDomain.Collaboration,
            "A verificação interna de phishing do Microsoft Forms está desligada.",
            "Formulários do próprio locatário são usados em golpes para pedir senhas e dados pessoais com a aparência de um pedido interno legítimo.",
            "Proteção interna contra phishing do Forms ligada.",
            "Lê a configuração da organização; não analisa formulários existentes.",
            "Colaboradores podem entregar credenciais a um formulário fraudulento hospedado no próprio domínio da organização.",
            Docs(DocFormsPhishing, DocFormsApi), KnightCapability.M365FormsSettings);
        P("AK-ENTRA-075", KnightSecurityDomain.Identity,
            "O tempo limite de sessão ociosa do Microsoft 365 não está configurado ou passa de 3 horas.",
            "Sem o tempo limite, uma sessão aberta num computador compartilhado ou esquecido continua válida indefinidamente nos aplicativos web do Microsoft 365.",
            "Política de inatividade padrão da organização com tempo limite de 3 horas ou menos.",
            "Lê a política de inatividade do locatário; a restrição imposta pelo aplicativo por acesso condicional, outra parte do critério, é avaliada no AK-ENTRA-069.",
            "Quem usar o computador depois pode ler e enviar e-mails e arquivos em nome da pessoa que deixou a sessão aberta.",
            Docs(DocIdleTimeout, DocActivityTimeoutApi), KnightCapability.ActivityBasedTimeoutPolicy);
        P("AK-ENTRA-076", KnightSecurityDomain.Identity,
            "Usuários membros não têm nenhum método capaz de MFA registrado.",
            "Uma conta sem método capaz de MFA não consegue cumprir uma exigência de MFA: ou fica bloqueada, ou entra numa exceção — e quem registrar primeiro um método nela, inclusive um atacante com a senha, passa a controlá-la.",
            "Todo usuário membro com ao menos um método capaz de MFA registrado.",
            "Lê o relatório de registro de métodos (o mesmo da cobertura de MFA); registro não prova que a MFA é exigida, e contas desabilitadas aparecem no relatório como as demais.",
            "Um atacante que obtenha a senha de um membro sem método registrado pode cadastrar o próprio segundo fator e manter o acesso mesmo depois da troca de senha.",
            Docs(Doc("Relatório de registro de métodos de autenticação", L + "entra/identity/authentication/howto-authentication-methods-activity"),
                Doc("API: userRegistrationDetails", L + "graph/api/resources/userregistrationdetails")), KnightCapability.MfaRegistration);
        P("AK-MDO-019", KnightSecurityDomain.Collaboration,
            "As mensagens denunciadas pelos usuários no Teams e no Outlook não vão para a caixa de relatórios da organização.",
            "A denúncia do usuário é um dos sinais mais rápidos de phishing em andamento; se ela não chega à equipe de segurança, o relato não vira resposta.",
            "Denúncias do Teams e do Outlook enviadas à caixa de relatórios da organização, sem envio das denúncias do Teams à Microsoft.",
            "Lê a política de envio; não verifica se a caixa de relatórios é monitorada. A permissão de denunciar no Teams, outra parte do critério, é avaliada no AK-TEAMS-017.",
            "Uma campanha de phishing relatada por colaboradores pode continuar sem bloqueio porque os relatos não chegaram a quem age.",
            Docs(DocUserReported, DocReportSubmission), KnightCapability.DefenderReportSubmissionPolicy);
    }

    public static IReadOnlyList<KnightControlProfile> All => Built;

    public static IReadOnlyDictionary<string, string> Impacts => BuiltImpacts;
}
