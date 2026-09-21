using System.Collections.Generic;

namespace AegisScore.Application.Knight.Catalog;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Perfis dos controles de configuração do Exchange Online: O PROBLEMA → POR QUE
/// IMPORTA → O QUE SE ESPERA → O QUE O ACHADO NÃO COMPROVA. Textos autorais; referências só a documentação
/// oficial conferida.
///
/// A coluna "o que o achado NÃO comprova" carrega, neste bloco, três distinções que o Exchange torna fáceis de
/// confundir — e que uma leitura descuidada transformaria em afirmação falsa:
///   • configuração ≠ ocorrência (ler que o encaminhamento existe não é ler que alguém leu a correspondência);
///   • padrão da organização ≠ ambiente (uma substituição por caixa ignora o padrão);
///   • um mecanismo ≠ todos os mecanismos (encaminhamento tem três caminhos independentes).
/// </summary>
public static class ExchangeConfigurationProfiles
{
    private static KnightControlReference Doc(string title, string url) => new("Microsoft Learn", null, title, url);

    private static readonly KnightControlReference DocSharedMailbox = Doc(
        "Sobre caixas de correio compartilhadas",
        "https://learn.microsoft.com/en-us/microsoft-365/admin/email/about-shared-mailboxes");
    private static readonly KnightControlReference DocGetUser = Doc(
        "Get-User (estado de entrada da conta no Exchange Online)",
        "https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-user");
    private static readonly KnightControlReference DocSharingPolicy = Doc(
        "Políticas de compartilhamento no Exchange Online",
        "https://learn.microsoft.com/en-us/exchange/sharing/sharing-policies/sharing-policies");
    private static readonly KnightControlReference DocLockbox = Doc(
        "Customer Lockbox no Microsoft 365",
        "https://learn.microsoft.com/en-us/purview/customer-lockbox-requests");
    private static readonly KnightControlReference DocBookings = Doc(
        "Microsoft Bookings — controlar quem pode criar páginas de agendamento",
        "https://learn.microsoft.com/en-us/microsoft-365/bookings/turn-bookings-on-or-off");
    private static readonly KnightControlReference DocMailboxAudit = Doc(
        "Auditoria de caixa de correio no Microsoft 365",
        "https://learn.microsoft.com/en-us/purview/audit-mailboxes");
    private static readonly KnightControlReference DocAuditBypass = Doc(
        "Get-MailboxAuditBypassAssociation (desvio de auditoria)",
        "https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-mailboxauditbypassassociation");
    private static readonly KnightControlReference DocForwarding = Doc(
        "Controlar o encaminhamento automático de e-mail no Microsoft 365",
        "https://learn.microsoft.com/en-us/defender-office-365/outbound-spam-policies-external-email-forwarding");
    private static readonly KnightControlReference DocTransportRules = Doc(
        "Regras de fluxo de emails (regras de transporte) no Exchange Online",
        "https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rules");
    private static readonly KnightControlReference DocExternalTag = Doc(
        "Identificar mensagens de remetentes externos no Outlook",
        "https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/set-externalinoutlook");
    private static readonly KnightControlReference DocAddIns = Doc(
        "Políticas de atribuição de função e suplementos do Outlook",
        "https://learn.microsoft.com/en-us/exchange/permissions-exo/role-assignment-policies");
    private static readonly KnightControlReference DocOwaPolicy = Doc(
        "Políticas de caixa de correio do Outlook na web",
        "https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/set-owamailboxpolicy");
    private static readonly KnightControlReference DocModernAuth = Doc(
        "Autenticação moderna no Exchange Online",
        "https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/enable-or-disable-modern-authentication-in-exchange-online");
    private static readonly KnightControlReference DocLegacyBlock = Doc(
        "Bloquear autenticação legada com acesso condicional",
        "https://learn.microsoft.com/en-us/entra/identity/conditional-access/policy-block-legacy-authentication");
    private static readonly KnightControlReference DocMailTips = Doc(
        "Dicas de e-mail (MailTips) no Exchange Online",
        "https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/mailtips/mailtips");
    private static readonly KnightControlReference DocSmtpAuth = Doc(
        "Habilitar ou desabilitar o envio SMTP AUTH autenticado",
        "https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission");
    private static readonly KnightControlReference DocDirectSend = Doc(
        "Envio direto (Direct Send) no Exchange Online",
        "https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/how-to-set-up-a-multifunction-device-or-application-to-send-email-using-microsoft-365");

    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;
    private static KnightCapability[] Caps(params KnightCapability[] c) => c;

    /// <summary>
    /// Todo controle que avalia uma POLÍTICA consome também a enumeração que demonstra o alcance dela. Sem a
    /// enumeração o controle continua avaliável — a política padrão vale por omissão —, mas o alcance das demais
    /// fica declarado como não demonstrado, e é isso que o texto do achado diz.
    /// </summary>
    private const string ReachCaveat =
        "O alcance de cada política é o número de caixas de correio que a declaram na enumeração desta coleta; "
        + "quando a enumeração atinge o teto, esse número não cobre a organização inteira e o achado o diz.";

    private const string ConfigNotOccurrence =
        "O controle lê a CONFIGURAÇÃO do ambiente. Ele não afirma que alguém tenha usado o caminho que ela abre, "
        + "nem que dados de alguma pessoa tenham sido expostos.";

    public static IReadOnlyList<KnightControlProfile> All { get; } = new[]
    {
        new KnightControlProfile("AK-EXO-001", KnightSecurityDomain.Identity,
            "Caixas de correio compartilhadas cuja conta correspondente pode entrar no ambiente.",
            "Uma caixa compartilhada não tem dono: a senha não é de ninguém, não entra na revisão de contas de pessoas e não costuma "
            + "ter segundo fator. Uma conta assim que possa entrar é um acesso sem responsável — e sem responsável, sem quem perceba.",
            "Toda caixa de correio compartilhada com a conta correspondente impedida de entrar; o acesso à caixa continua pela "
            + "permissão delegada da conta de cada pessoa.",
            "O vínculo entre a caixa e a conta é feito pelo identificador do objeto de diretório, na MESMA aquisição — nunca por nome "
            + "parecido. Quando a conta não é encontrada na leitura de contas, o estado fica DESCONHECIDO e a caixa não é aprovada: "
            + "ser compartilhada não implica estar bloqueada. " + ConfigNotOccurrence,
            Docs(DocSharedMailbox, DocGetUser),
            Caps(KnightCapability.ExchangeMailboxes, KnightCapability.ExchangeMailboxSignIn)),

        new KnightControlProfile("AK-EXO-002", KnightSecurityDomain.Collaboration,
            "Políticas de compartilhamento concedem compartilhamento de calendário a domínios externos.",
            "Um calendário diz com quem a pessoa se reúne, quando ela está fora e, pelo título das reuniões, o que a organização está "
            + "fazendo. É reconhecimento pronto para quem prepara uma abordagem dirigida.",
            "Nenhuma política em vigor concedendo compartilhamento de calendário a domínios externos; havendo necessidade, domínios "
            + "nomeados e nível mínimo de detalhe.",
            "Uma política DESLIGADA não concede nada e é registrada como evidência, sem sustentar aprovação nem exposição. "
            + ConfigNotOccurrence + " " + ReachCaveat,
            Docs(DocSharingPolicy),
            Caps(KnightCapability.ExchangeSharingPolicies, KnightCapability.ExchangeMailboxes)),

        new KnightControlProfile("AK-EXO-003", KnightSecurityDomain.Governance,
            "O Customer Lockbox está desabilitado na organização.",
            "Sem ele, um pedido de acesso da engenharia do fornecedor ao conteúdo da organização não depende de aprovação explícita de "
            + "alguém do cliente. O recurso não impede o acesso: define quem autoriza e deixa registro de cada aprovação.",
            "Customer Lockbox habilitado, com responsáveis definidos para aprovar ou recusar os pedidos.",
            "O controle NÃO afirma que houve acesso da engenharia do fornecedor ao conteúdo da organização. Ele lê quem autorizaria, "
            + "caso um pedido aconteça.",
            Docs(DocLockbox), Caps(KnightCapability.ExchangeOrganizationConfig)),

        new KnightControlProfile("AK-EXO-004", KnightSecurityDomain.Governance,
            "O Bookings está ligado na organização e políticas do Outlook na web permitem criar caixas de agendamento.",
            "Uma página de agendamento é publicada na internet sem autenticação: quem tem o endereço vê nomes, funções e a "
            + "disponibilidade da equipe, e pode marcar horários sem pertencer à organização.",
            "Bookings desligado quando não for usado; quando for, criação de caixas de agendamento restrita a quem tem necessidade "
            + "demonstrada.",
            "O controle lê a PERMISSÃO de criar páginas; não afirma que alguma página exista nem que alguma tenha sido acessada de "
            + "fora. A referência verifica a política padrão do Outlook na web — o AEGIS verifica todas as instâncias. " + ReachCaveat,
            Docs(DocBookings, DocOwaPolicy),
            Caps(KnightCapability.ExchangeOrganizationConfig, KnightCapability.ExchangeOwaMailboxPolicies, KnightCapability.ExchangeCasMailboxes)),

        new KnightControlProfile("AK-EXO-005", KnightSecurityDomain.Logging,
            "A auditoria de caixas de correio está desabilitada no nível da organização.",
            "Sem registro, o acesso a mensagens, a exclusão de itens e a criação de regras não deixam rastro. Uma investigação "
            + "posterior não encontra o que examinar — e a ausência de rastro não é a mesma coisa que ausência de evento.",
            "Auditoria de caixas de correio habilitada na organização, de modo que as ações padrão sejam registradas em toda caixa, "
            + "inclusive nas criadas depois.",
            "Este é o interruptor da ORGANIZAÇÃO. Ele não responde sozinho quais ações estão sendo registradas (AK-EXO-006) nem se "
            + "alguma conta está dispensada da auditoria (AK-EXO-007): um indicador isolado não comprova o conjunto.",
            Docs(DocMailboxAudit), Caps(KnightCapability.ExchangeOrganizationConfig)),

        new KnightControlProfile("AK-EXO-006", KnightSecurityDomain.Logging,
            "Caixas de correio sem as ações de auditoria configuradas em algum dos três tipos de acesso.",
            "Os três tipos são registros independentes: auditar o acesso do próprio dono não audita o acesso delegado, e auditar o "
            + "delegado não audita o administrativo. Uma investigação que precise de um dos três não encontra o que não foi registrado.",
            "Ações de auditoria configuradas nos três tipos de acesso em todas as caixas de usuário e compartilhadas.",
            "O achado descreve o que está CONFIGURADO em cada caixa. Quando a auditoria padrão da organização está ligada "
            + "(AK-EXO-005), ações não listadas na caixa ainda podem ser registradas pelo conjunto padrão do serviço — por isso o "
            + "controle não afirma que a ação não é registrada em hipótese alguma. Uma enumeração truncada não aprova a organização.",
            Docs(DocMailboxAudit), Caps(KnightCapability.ExchangeMailboxes)),

        new KnightControlProfile("AK-EXO-007", KnightSecurityDomain.Logging,
            "Contas com desvio de auditoria habilitado: nenhuma ação delas sobre caixas de correio é registrada.",
            "O desvio existe para reduzir ruído de contas de serviço confiáveis. Onde ele existe, o rastro de um uso indevido daquela "
            + "conta também desaparece — e é exatamente a conta de serviço que um invasor procura.",
            "Nenhuma conta com desvio de auditoria, salvo exceção escrita, nominal e revisada periodicamente.",
            "O controle NÃO afirma que a conta com desvio tenha feito algo indevido. Ele afirma que, se fizer, não haverá registro.",
            Docs(DocAuditBypass), Caps(KnightCapability.ExchangeAuditBypassAssociations)),

        new KnightControlProfile("AK-EXO-008", KnightSecurityDomain.DataProtection,
            "Mensagens da organização podem sair automaticamente para endereços de fora.",
            "O encaminhamento automático é o passo seguinte típico de uma conta comprometida: ele mantém o acesso à correspondência "
            + "depois que a senha é trocada, sem exigir nova entrada.",
            "Os três mecanismos fechados: encaminhamento automático desligado nas políticas de filtro de spam de saída, nenhuma caixa "
            + "com encaminhamento configurado e nenhuma regra de transporte em vigor desviando mensagens.",
            "“Todas as formas bloqueadas” é uma afirmação sobre um CONJUNTO de mecanismos. Com um deles não lido, o controle fica NÃO "
            + "AVALIADO e nomeia o mecanismo que falta — ainda que os outros estejam corretos. " + ConfigNotOccurrence,
            Docs(DocForwarding, DocTransportRules),
            Caps(KnightCapability.ExchangeOutboundSpamFilterPolicies, KnightCapability.ExchangeMailboxes, KnightCapability.ExchangeTransportRules)),

        new KnightControlProfile("AK-EXO-009", KnightSecurityDomain.Collaboration,
            "Regras de transporte dispensam remetentes da filtragem de spam a partir do domínio ou endereço declarado.",
            "Domínio e endereço de remetente são campos que qualquer pessoa escreve na mensagem. Uma isenção por esses campos vale "
            + "para quem os imitar — e a mensagem imitada chega sem passar por filtro nenhum.",
            "Nenhuma regra em vigor isentando remetentes por domínio ou endereço; parceiros legítimos tratados por autenticação do "
            + "remetente.",
            "Uma regra DESABILITADA, ou em modo de auditoria, não age sobre mensagem alguma: ela é registrada como evidência, não "
            + "como exposição, com o estado e o modo ditos. Quando a fonte não informa estado ou modo, o controle não conclui.",
            Docs(DocTransportRules), Caps(KnightCapability.ExchangeTransportRules)),

        new KnightControlProfile("AK-EXO-010", KnightSecurityDomain.Collaboration,
            "Mensagens vindas de fora chegam sem marca que as distinga das internas.",
            "A marca de externo é o aviso que aparece no momento em que a pessoa decide se confia na mensagem. Ela não bloqueia nada; "
            + "dá contexto no único instante em que o erro ainda pode ser evitado.",
            "Identificação de remetentes externos habilitada e sem isenções.",
            "Uma isenção vale por DOMÍNIO DECLARADO, não por remetente comprovado: o endereço isento pode ser imitado, e a mensagem "
            + "imitada chegará sem a marca. O controle lê a configuração; não afirma que alguma mensagem tenha enganado alguém.",
            Docs(DocExternalTag), Caps(KnightCapability.ExchangeExternalSenderIdentification)),

        new KnightControlProfile("AK-EXO-011", KnightSecurityDomain.Governance,
            "Políticas de atribuição de função permitem instalar suplementos do Outlook por conta própria.",
            "Um suplemento roda dentro do cliente de e-mail, com acesso ao conteúdo das mensagens que a pessoa abre. A instalação por "
            + "conta própria não passa por avaliação da organização.",
            "Nenhuma política com as funções de instalação de suplemento; a distribuição de suplementos fica com a administração.",
            "O controle lê a PERMISSÃO de instalar; não afirma que algum suplemento tenha sido instalado nem que algum seja malicioso. "
            + ReachCaveat,
            Docs(DocAddIns),
            Caps(KnightCapability.ExchangeRoleAssignmentPolicies, KnightCapability.ExchangeMailboxes)),

        new KnightControlProfile("AK-EXO-012", KnightSecurityDomain.DataProtection,
            "O Outlook na web permite adicionar contas de e-mail e calendários pessoais.",
            "Isso cria um caminho entre a correspondência da organização e uma conta que ela não administra — dentro do próprio cliente, "
            + "sem passar por controle de saída de dados.",
            "Contas de e-mail pessoais e calendários pessoais desabilitados em todas as políticas do Outlook na web.",
            "Com um dos dois valores não informado pela fonte, o controle não conclui: aprovar exigiria supor o ausente. " + ReachCaveat,
            Docs(DocOwaPolicy),
            Caps(KnightCapability.ExchangeOwaMailboxPolicies, KnightCapability.ExchangeCasMailboxes)),

        new KnightControlProfile("AK-EXO-013", KnightSecurityDomain.Identity,
            "A autenticação moderna está desabilitada no Exchange Online.",
            "Sem ela, os clientes de e-mail autenticam pelo caminho legado, em que a senha basta: não há segundo fator, e o acesso "
            + "condicional não é aplicado.",
            "Autenticação moderna habilitada e, à parte, autenticação legada bloqueada por acesso condicional ou por política de "
            + "autenticação.",
            "Habilitar a autenticação moderna NÃO elimina a autenticação legada: permite o fluxo novo, sem bloquear o antigo. O "
            + "bloqueio é uma decisão distinta e não é verificado por este controle.",
            Docs(DocModernAuth, DocLegacyBlock), Caps(KnightCapability.ExchangeOrganizationConfig)),

        new KnightControlProfile("AK-EXO-014", KnightSecurityDomain.Collaboration,
            "As dicas de e-mail não estão completas na organização.",
            "As dicas avisam quem está escrevendo — antes de enviar — que a mensagem vai para fora ou para um número grande de "
            + "pessoas. É o único aviso que aparece enquanto o erro ainda pode ser evitado.",
            "Dicas de e-mail habilitadas, inclusive a de destinatário externo e as métricas de grupo, com limite de público amplo de "
            + "no máximo 25 destinatários.",
            "As dicas são um aviso, não um controle: elas não impedem o envio. O controle não conclui quando a fonte não informa "
            + "algum dos quatro valores.",
            Docs(DocMailTips), Caps(KnightCapability.ExchangeOrganizationConfig)),

        new KnightControlProfile("AK-EXO-015", KnightSecurityDomain.DataProtection,
            "O Outlook na web oferece provedores de armazenamento de terceiros para anexar arquivos.",
            "Um anexo enviado por um serviço de terceiros sai do armazenamento da organização: deixa de ter a retenção, a "
            + "classificação e a auditoria dela, e continua acessível por uma conta pessoal depois que a pessoa sai.",
            "Provedores de armazenamento adicionais desabilitados em todas as políticas do Outlook na web.",
            "O controle lê o que o cliente OFERECE; não afirma que algum arquivo tenha sido compartilhado por esses serviços. " + ReachCaveat,
            Docs(DocOwaPolicy),
            Caps(KnightCapability.ExchangeOwaMailboxPolicies, KnightCapability.ExchangeCasMailboxes)),

        new KnightControlProfile("AK-EXO-016", KnightSecurityDomain.Identity,
            "O SMTP AUTH está habilitado — na organização, ou por substituição em caixas de correio.",
            "O SMTP AUTH autentica com usuário e senha e não passa por segundo fator. Uma senha vazada basta para enviar mensagens em "
            + "nome da caixa, e o envio sai pelo serviço legítimo da organização.",
            "SMTP AUTH desabilitado na organização e nenhuma caixa o habilitando por substituição; dispositivos legados migrados para "
            + "um conector autenticado.",
            "No Exchange, a AUSÊNCIA do valor na caixa significa HERANÇA da organização — não desabilitação. Por isso o controle não "
            + "conclui a partir do valor da organização sozinho: sem a leitura das substituições, ele descreveria o padrão e não o "
            + "ambiente. Uma enumeração truncada não aprova a organização.",
            Docs(DocSmtpAuth),
            Caps(KnightCapability.ExchangeTransportConfig, KnightCapability.ExchangeCasMailboxes)),

        new KnightControlProfile("AK-EXO-017", KnightSecurityDomain.Collaboration,
            "A organização aceita mensagens entregues ao seu ponto de extremidade sem autenticação.",
            "Esse caminho existe para dispositivos e aplicativos internos e não exige credencial: qualquer remetente pode usá-lo "
            + "declarando um endereço da organização, e a mensagem chega às caixas internas com aparência de interna.",
            "Rejeição de envios diretos habilitada, com os dispositivos e aplicativos legados migrados para um conector autenticado "
            + "ANTES da mudança.",
            "A configuração é recente no serviço. Quando a fonte não a devolve, o controle fica NÃO AVALIADO com esse motivo — "
            + "ausência do valor indica locatário ou versão de módulo que ainda não o expõe, e não configuração desligada.",
            Docs(DocDirectSend), Caps(KnightCapability.ExchangeOrganizationConfig)),
    };
}
