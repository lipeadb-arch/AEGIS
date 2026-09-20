using System.Collections.Generic;

namespace AegisScore.Application.Knight.Catalog;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Perfis dos controles de configuração do Microsoft Teams: O PROBLEMA → POR QUE
/// IMPORTA → O QUE SE ESPERA → O QUE O ACHADO NÃO COMPROVA, mais o IMPACTO POTENCIAL. Textos autorais;
/// referências só a documentação oficial conferida.
/// </summary>
public static class TeamsConfigurationProfiles
{
    private static KnightControlReference Doc(string title, string url) => new("Microsoft Learn", null, title, url);

    private static readonly KnightControlReference DocCloudStorage = Doc(
        "Configurações do cliente do Teams (armazenamento em nuvem de terceiros)",
        "https://learn.microsoft.com/en-us/powershell/module/microsoftteams/set-csteamsclientconfiguration");
    private static readonly KnightControlReference DocChannelEmail = Doc(
        "Endereço de e-mail de um canal do Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/manage-email-integration-for-microsoft-teams");
    private static readonly KnightControlReference DocExternalAccess = Doc(
        "Gerenciar o acesso externo no Microsoft Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/manage-external-access");
    private static readonly KnightControlReference DocFederation = Doc(
        "Configuração de federação do locatário",
        "https://learn.microsoft.com/en-us/powershell/module/microsoftteams/set-cstenantfederationconfiguration");
    private static readonly KnightControlReference DocTrialTenants = Doc(
        "Acesso externo com locatários de avaliação",
        "https://learn.microsoft.com/en-us/microsoftteams/trusted-organizations-external-meetings-chat");
    private static readonly KnightControlReference DocAppPermission = Doc(
        "Políticas de permissão de aplicativos do Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/teams-app-permission-policies");
    private static readonly KnightControlReference DocAppCentric = Doc(
        "Gerenciamento centrado em aplicativos (ACM) para o acesso a aplicativos do Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/app-centric-management");
    private static readonly KnightControlReference DocMeetingPolicy = Doc(
        "Políticas de reunião do Microsoft Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/settings-policies-reference");
    private static readonly KnightControlReference DocLobby = Doc(
        "Controlar quem entra direto na reunião e quem espera no lobby",
        "https://learn.microsoft.com/en-us/microsoftteams/who-can-bypass-meeting-lobby");
    private static readonly KnightControlReference DocPresenter = Doc(
        "Papéis em uma reunião do Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/meeting-who-present-request-control");
    private static readonly KnightControlReference DocRecording = Doc(
        "Gravação de reuniões na nuvem no Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/cloud-recording");
    private static readonly KnightControlReference DocReporting = Doc(
        "Denúncia de mensagens suspeitas pelo usuário no Teams",
        "https://learn.microsoft.com/en-us/microsoftteams/manage-end-user-report-message-teams");
    private static readonly KnightControlReference DocGroupAssignment = Doc(
        "Atribuir políticas do Teams a um grupo",
        "https://learn.microsoft.com/en-us/microsoftteams/assign-policies-users-and-groups");

    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;
    private static KnightCapability[] Caps(params KnightCapability[] c) => c;

    /// <summary>
    /// Todo controle que lê uma POLÍTICA consome também as atribuições a grupos: é o que sustenta a afirmação de
    /// alcance. Sem elas o controle continua avaliável (a política padrão da organização sempre se aplica), mas o
    /// alcance das personalizadas fica declarado como não demonstrado.
    /// </summary>
    private static KnightCapability[] PolicyCaps(KnightCapability policies) =>
        Caps(policies, KnightCapability.TeamsPolicyAssignments);

    private const string ReachCaveat =
        "O alcance demonstrado é o da política padrão da organização e o das políticas atribuídas a grupos: "
        + "a atribuição direta por conta de usuário não é enumerada nesta coleta.";

    public static IReadOnlyList<KnightControlProfile> All { get; } = new[]
    {
        new KnightControlProfile("AK-TEAMS-001", KnightSecurityDomain.DataProtection,
            "O cliente do Teams oferece serviços de armazenamento de nuvem de terceiros para anexar e compartilhar arquivos.",
            "Um arquivo enviado por um serviço de terceiros sai do SharePoint e do OneDrive: deixa de ter a retenção, a classificação, "
            + "a prevenção de perda de dados e a auditoria da organização, e continua acessível por uma conta pessoal depois que a pessoa sai.",
            "Nenhum provedor de armazenamento de terceiros habilitado, salvo os que fazem parte do ambiente aprovado.",
            "O controle lê o que o cliente OFERECE; não afirma que algum arquivo foi de fato compartilhado por esses serviços.",
            Docs(DocCloudStorage), Caps(KnightCapability.TeamsClientConfiguration)),

        new KnightControlProfile("AK-TEAMS-002", KnightSecurityDomain.Collaboration,
            "Os canais do Teams têm endereço de e-mail e aceitam mensagens enviadas de fora do cliente.",
            "Uma mensagem entregue por e-mail aparece no canal sem a identidade do remetente no Teams. É um caminho para entregar "
            + "conteúdo de golpe dentro de um espaço em que as pessoas confiam por padrão.",
            "Envio de e-mail para endereço de canal desabilitado; quando o recurso for necessário, remetentes restritos aos domínios aceitos.",
            "O controle lê a configuração do locatário; não afirma que algum canal recebeu mensagem por esse caminho.",
            Docs(DocChannelEmail), Caps(KnightCapability.TeamsClientConfiguration)),

        new KnightControlProfile("AK-TEAMS-003", KnightSecurityDomain.Collaboration,
            "O acesso externo aceita qualquer organização que não esteja em uma lista de bloqueio.",
            "Uma lista de bloqueio só barra o que já foi identificado como indesejado. Qualquer domínio novo é aceito por padrão, "
            + "e criar um domínio e um locatário novos é barato.",
            "Acesso externo desligado, ou restrito a uma lista fechada de organizações aprovadas.",
            "Permitir uma organização não significa que exista conversa com ela; o controle lê a permissão, não o tráfego.",
            Docs(DocExternalAccess, DocFederation), Caps(KnightCapability.TeamsFederationConfiguration)),

        new KnightControlProfile("AK-TEAMS-004", KnightSecurityDomain.Collaboration,
            "A comunicação com contas do Teams que não são gerenciadas por nenhuma organização está habilitada.",
            "Uma conta não gerenciada não tem administrador, não tem política corporativa e não responde a nenhuma governança: "
            + "não há a quem pedir registro, bloqueio ou investigação.",
            "Comunicação com contas não gerenciadas desligada, ou restrita a cenários aprovados e monitorados.",
            "O controle lê a permissão do locatário; não afirma que houve conversa com alguma conta não gerenciada.",
            Docs(DocExternalAccess, DocFederation), Caps(KnightCapability.TeamsFederationConfiguration)),

        new KnightControlProfile("AK-TEAMS-005", KnightSecurityDomain.Collaboration,
            "Contas do Teams não gerenciadas podem localizar pessoas da organização e iniciar conversas com elas.",
            "Quem inicia a conversa escolhe o alvo e o momento. Uma abordagem por chat chega sem os filtros que existem para o e-mail "
            + "e com a aparência de uma mensagem interna.",
            "Somente pessoas da organização iniciam conversas com contas não gerenciadas.",
            "O controle lê a permissão; não afirma que alguma conta externa abordou alguém.",
            Docs(DocExternalAccess), Caps(KnightCapability.TeamsFederationConfiguration)),

        new KnightControlProfile("AK-TEAMS-006", KnightSecurityDomain.Collaboration,
            "O acesso externo aceita locatários que só têm licenças de avaliação.",
            "Um locatário de avaliação é criado em minutos, com qualquer nome de organização e sem vínculo comercial. É a origem "
            + "mais barata para uma mensagem que aparenta vir de um parceiro.",
            "Acesso externo com locatários de avaliação bloqueado; parceiros legítimos nessa situação liberados um a um por exceção.",
            "O bloqueio não impede abordagens vindas de locatários pagos nem de domínios já permitidos.",
            Docs(DocTrialTenants), Caps(KnightCapability.TeamsFederationConfiguration)),

        new KnightControlProfile("AK-TEAMS-007", KnightSecurityDomain.Governance,
            "Os catálogos de aplicativos do Teams não estão restritos a listas de permitidos.",
            "Permitir tudo e bloquear caso a caso só alcança aplicativos que alguém já identificou como indesejados. Um aplicativo "
            + "adicionado a uma equipe passa a ler o conteúdo dos canais a que tem acesso.",
            "Políticas de permissão com lista de PERMITIDOS nos três catálogos: aplicativos da Microsoft, de terceiros e personalizados.",
            "O comando oficial de leitura só se aplica a locatários NÃO migrados para o gerenciamento centrado em aplicativos "
            + "(ACM/UAM). Por isso o controle só conclui quando a coleta demonstra que as políticas de permissão ainda governam o "
            + "acesso a aplicativos: em locatário migrado — e quando o modelo em vigor não pode ser determinado — a configuração "
            + "legada é preservada como evidência, sem veredito. " + ReachCaveat,
            Docs(DocAppPermission, DocAppCentric, DocGroupAssignment),
            Caps(KnightCapability.TeamsAppPermissionPolicies, KnightCapability.TeamsAppAvailability,
                 KnightCapability.TeamsPolicyAssignments)),

        new KnightControlProfile("AK-TEAMS-008", KnightSecurityDomain.Collaboration,
            "Participantes anônimos podem entrar em reuniões da organização.",
            "Um participante anônimo não tem conta, não tem identificação verificável e não deixa registro de quem é — apenas o nome "
            + "que digitou ao entrar.",
            "Entrada de participantes anônimos desligada em todas as políticas de reunião.",
            "O controle lê a política; não afirma que algum anônimo entrou em alguma reunião. " + ReachCaveat,
            Docs(DocMeetingPolicy), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-009", KnightSecurityDomain.Collaboration,
            "A reunião pode começar sem nenhum participante verificado presente.",
            "Quando a reunião começa sem ninguém verificado, o lobby deixa de cumprir sua função: a sala fica aberta antes de qualquer "
            + "controle humano.",
            "Início de reunião sem participante verificado desligado em todas as políticas de reunião.",
            "A documentação oficial condiciona o efeito deste ajuste: para participantes ANÔNIMOS ele só vale quando a entrada de "
            + "anônimos está habilitada e “quem entra sem passar pelo lobby” está em “todos”; fora dessas condições, ele vale para "
            + "quem entra por discagem telefônica. O controle lê a política; não afirma que as condições estejam presentes nem que "
            + "alguma reunião tenha sido iniciada assim. " + ReachCaveat,
            Docs(DocMeetingPolicy), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-010", KnightSecurityDomain.Collaboration,
            "A admissão automática deixa entrar direto na reunião pessoas que não são da organização.",
            "O lobby é o único ponto em que alguém de dentro decide quem entra. Admissão automática ampla transfere essa decisão para "
            + "a configuração padrão.",
            "Admissão automática restrita a pessoas da organização, excluindo convidados — ou mais restritiva.",
            "Convidados do diretório contam como externos para este critério, ainda que tenham conta no locatário. " + ReachCaveat,
            Docs(DocLobby), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-011", KnightSecurityDomain.Collaboration,
            "Quem entra pelo telefone é admitido sem passar pelo lobby.",
            "Um número de telefone não identifica a pessoa, e o código de acesso da reunião pode ser repassado ou encaminhado a qualquer um.",
            "Chamadores por telefone submetidos ao lobby, como qualquer participante externo.",
            "O controle lê a política; não afirma que alguém entrou por telefone. " + ReachCaveat,
            Docs(DocLobby), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-012", KnightSecurityDomain.Collaboration,
            "O chat da reunião aceita mensagens de participantes anônimos.",
            "O chat da reunião entrega links e arquivos a todos os participantes e fica registrado na conversa — inclusive o que foi "
            + "escrito por quem não está identificado.",
            "Chat da reunião habilitado exceto para anônimos, ou desabilitado.",
            "O controle lê a política; não afirma que algum anônimo escreveu no chat. " + ReachCaveat,
            Docs(DocMeetingPolicy), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-013", KnightSecurityDomain.Collaboration,
            "Pessoas que não organizaram a reunião entram nela já com o papel de apresentador.",
            "Quem apresenta compartilha a tela, exibe conteúdo para todos e pode remover outras pessoas da sala.",
            "Papel de apresentador restrito ao organizador (e coorganizadores) por padrão, com promoção durante a reunião quando necessário.",
            "O CONJUNTO que recebe o papel depende do valor encontrado — todos os participantes, só as pessoas da organização, ou a "
            + "organização mais as confiáveis — e está dito em cada política. O controle lê o padrão; o organizador pode alterar o "
            + "papel em cada reunião. " + ReachCaveat,
            Docs(DocPresenter), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-014", KnightSecurityDomain.Collaboration,
            "Participantes externos podem dar ou pedir o controle da tela compartilhada.",
            "Quem recebe o controle age no computador de quem compartilha, com o teclado e o mouse dessa pessoa.",
            "Transferência de controle para participantes externos desligada em todas as políticas de reunião.",
            "O controle lê a política; não afirma que algum controle foi cedido. " + ReachCaveat,
            Docs(DocPresenter), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-015", KnightSecurityDomain.Collaboration,
            "Pessoas da organização participam do chat de reuniões hospedadas por organizações não confiáveis.",
            "O chat de uma reunião externa não confiável entrega mensagens, links e arquivos num canal que a organização não administra: "
            + "não define quem participa, não retém a conversa e não a audita.",
            "Chat em reuniões externas não confiáveis desligado em todas as políticas de reunião.",
            "O achado NÃO afirma que a mensagem chegue sem controle algum: proteção do ponto final, do navegador e do próprio Teams "
            + "continuam valendo. O que a configuração encontrada não oferece é controle sobre o canal. “Não confiável” é o que a "
            + "configuração de acesso externo define; o controle não reclassifica organizações. " + ReachCaveat,
            Docs(DocMeetingPolicy, DocExternalAccess), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-016", KnightSecurityDomain.DataProtection,
            "A gravação de reuniões na nuvem está habilitada por padrão.",
            "A gravação guarda o que foi dito e mostrado, fica armazenada e pode ser compartilhada depois — inclusive quando a conversa "
            + "tratou de assunto que não deveria ser retido.",
            "Gravação desligada por padrão, habilitada por política apenas para quem tem necessidade demonstrada e com retenção definida.",
            "O controle lê a política; não afirma que alguma reunião foi gravada. " + ReachCaveat,
            Docs(DocRecording), PolicyCaps(KnightCapability.TeamsMeetingPolicies)),

        new KnightControlProfile("AK-TEAMS-017", KnightSecurityDomain.Collaboration,
            "As pessoas não têm no Teams um caminho para relatar uma mensagem suspeita.",
            "A denúncia pelo próprio destinatário é a via mais rápida — e às vezes a única — para identificar uma abordagem dirigida por "
            + "chat. Sem ela, a suspeita não vira registro por esse caminho.",
            "Denúncia de problemas de segurança habilitada nas políticas de mensagens, com destino definido no Microsoft Defender para Office 365.",
            "O achado NÃO afirma que a equipe de segurança só descobrirá o problema depois de alguém agir sobre a mensagem: a detecção "
            + "pode vir da proteção de mensagens, de sinais do ponto final ou de relato por outro canal. Ele afirma a ausência DESTE "
            + "caminho. A metade do critério que fica no Microsoft Defender para Office 365 não é lida neste bloco. " + ReachCaveat,
            Docs(DocReporting), PolicyCaps(KnightCapability.TeamsMessagingPolicies)),
    };
}
