using System;
using System.Collections.Generic;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] IMPACTO POTENCIAL de cada controle
// ============================================================================
// O perfil já diz O PROBLEMA (descrição) e o RISCO (o caminho que a condição abre). O impacto responde a outra
// pergunta: se esse caminho for usado, O QUE a organização pode perder — no limite do acesso que a própria
// condição concede. Três regras de redação, verificadas em revisão:
//   • impacto é POTENCIAL: nunca afirma que houve incidente, comprometimento ou acesso indevido;
//   • o alcance é o da condição (uma conta, um papel, o locatário), sem "todos os dados" ou "controle total" que
//     a condição não sustenta;
//   • o texto é do controle — não um parágrafo genérico repetido.
// O alcance COMPROVADO na coleta (quantas contas, quais aplicações) é composto à parte, a partir dos objetos
// congelados na fotografia; este texto não carrega números.

public static class KnightControlImpacts
{
    private static readonly IReadOnlyDictionary<string, string> ById = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AK-ENTRA-001"] = "Sem método registrado, a conta não tem segundo fator para apresentar: onde a entrada for aceita só com a senha, quem tiver essa senha age com os papéis administrativos da conta.",
        ["AK-ENTRA-002"] = "Cada conta ou aplicação a mais com papel administrativo é mais um ponto cujo comprometimento concede alterações no diretório; o alcance de cada uma é o dos papéis que ela tem.",
        ["AK-ENTRA-003"] = "A conta administrativa recebe mensagens diretamente; se o administrador abrir um anexo, seguir um link ou digitar a senha numa página falsa, o alvo do golpe é uma identidade com papéis administrativos, não um usuário comum.",
        ["AK-ENTRA-004"] = "Uma conta de convidado sem dono pode continuar lendo o que foi compartilhado com ela depois do fim da relação que justificou o convite.",
        ["AK-ENTRA-005"] = "Um segredo de conta de serviço vazado dá acesso contínuo, sem segundo fator, ao que essa conta alcança.",
        ["AK-ENTRA-006"] = "Cada usuário sem método registrado depende da senha nos acessos que não exigirem segundo fator; uma credencial vazada leva ao e-mail, aos arquivos e às aplicações daquele usuário.",
        ["AK-ENTRA-007"] = "Ataques de senha em massa podem entrar por protocolos que não pedem segundo fator, alcançando as caixas de correio e dados das contas atingidas.",
        ["AK-ENTRA-008"] = "Um administrador sem exigência de MFA pode ser usado com a senha apenas, com o alcance dos papéis que ocupa.",
        ["AK-ENTRA-009"] = "Credenciais vencidas interrompem integrações; credenciais esquecidas e válidas por longo prazo permitem acesso da aplicação sem ninguém perceber.",
        ["AK-ENTRA-010"] = "Quem obtiver a credencial de uma dessas aplicações lê ou altera dados do diretório com as permissões concedidas, sem usuário e sem MFA.",
        ["AK-ENTRA-011"] = "Uma conta privilegiada sem uso pode ser usada por quem roubar a credencial, e o uso anômalo tende a passar despercebido.",
        ["AK-ENTRA-012"] = "A segurança dessas contas administrativas depende de outra organização; um comprometimento lá alcança o seu diretório com os papéis concedidos.",
        ["AK-ENTRA-013"] = "Com o consentimento concedido para todo o diretório, a aplicação lê dados em nome de cada usuário que entrar nela, sem nova autorização; se ela for comprometida, o alcance é o das permissões já concedidas.",
        ["AK-ENTRA-014"] = "Sem base mínima, a proteção de cada conta depende só da senha, e credenciais vazadas dão acesso direto aos dados de quem foi atingido.",
        ["AK-ENTRA-015"] = "A organização pode ficar sem acesso administrativo durante um incidente ou uma falha de configuração, sem como corrigir políticas até o suporte do fornecedor intervir.",
        ["AK-ENTRA-016"] = "Aplicações criadas por usuários podem receber credenciais e pedir permissões fora de qualquer revisão, criando acessos persistentes que a TI não conhece.",
        ["AK-ENTRA-017"] = "Dados e identidades da organização podem acabar em locatários sem a retenção e a auditoria corporativas, e esse acesso não aparece nos inventários.",
        ["AK-ENTRA-018"] = "Grupos criados sem governança passam a conceder acesso a recursos sem dono nem revisão, dificultando saber quem acessa o quê.",
        ["AK-ENTRA-019"] = "Quem entrar na conta do proprietário consegue ler a chave de recuperação; de posse também do dispositivo, desbloqueia o disco fora do controle da organização e lê o que estiver guardado nele.",
        ["AK-ENTRA-020"] = "E-mail, arquivos ou agenda do usuário — conforme as permissões consentidas — podem ser lidos por terceiros de forma contínua, sem depender da senha.",
        ["AK-ENTRA-021"] = "Nomes, cargos e estrutura da organização ficam acessíveis a terceiros, facilitando engenharia social contra pessoas específicas.",
        ["AK-ENTRA-022"] = "Informações compartilhadas em equipes, sites e arquivos podem chegar a pessoas de fora que ninguém autorizou formalmente.",
        ["AK-ENTRA-023"] = "Aplicações podem operar com permissões que ninguém analisou, ou equipes podem ficar bloqueadas sem canal para pedir o acesso de que precisam.",
        ["AK-ENTRA-024"] = "Segredos novos podem ser criados em aplicações existentes, inclusive por quem tiver comprometido um proprietário, garantindo acesso da aplicação.",
        ["AK-ENTRA-025"] = "Os dados que a aplicação alcança — conforme as permissões dela — ficam expostos por mais tempo a quem tiver o segredo.",
        ["AK-ENTRA-026"] = "Segredos previsíveis ou reaproveitados são mais fáceis de adivinhar ou reutilizar em outras aplicações.",
        ["AK-ENTRA-027"] = "Os dados e sistemas que a aplicação alcança ficam expostos por toda a vigência do certificado, e a rotação tende a ser esquecida.",
        ["AK-ENTRA-028"] = "Sem contexto na notificação, o usuário tem menos elementos para recusar uma aprovação falsa, e um pedido aceito por engano completa a entrada do atacante.",
        ["AK-ENTRA-029"] = "Um código interceptado por troca de chip ou engenharia social completa a autenticação da conta atingida.",
        ["AK-ENTRA-030"] = "Atribuições do papel que cria locatários permanecem sem revisão, mantendo a possibilidade de criar ambientes fora da governança.",
        ["AK-ENTRA-031"] = "Contas cuja caixa de correio for comprometida podem ter a entrada concluída por terceiros, com acesso aos dados dessas contas.",
        ["AK-ENTRA-033"] = "Contas com senhas previsíveis podem ser tomadas por tentativa, com acesso aos dados e sistemas de cada uma.",
        ["AK-ENTRA-034"] = "Contas do domínio local com senhas fracas podem ser tomadas e usadas também nos serviços em nuvem da organização.",
        ["AK-ENTRA-035"] = "Contas com senhas fracas ficam mais expostas a serem tomadas por tentativa e erro, com acesso aos dados de cada uma.",
        ["AK-ENTRA-036"] = "Aumenta a chance de uma conta ser tomada por tentativa e erro dentro do mesmo período de ataque.",
        ["AK-ENTRA-037"] = "Caixas de correio, sites e equipes criados sem dono podem guardar informação sensível sem retenção, classificação ou revisão de acesso.",
        ["AK-ENTRA-038"] = "A troca forçada tende a produzir senhas previsíveis, mais fáceis de adivinhar a partir das anteriores.",
        ["AK-ENTRA-039"] = "Uma falha da infraestrutura local pode impedir a entrada nos serviços em nuvem, e credenciais já vazadas continuam sem alerta.",
        ["AK-ENTRA-040"] = "Um dispositivo controlado por terceiros pode passar a acessar aplicações que exigem dispositivo da organização, com os dados da conta usada.",
        ["AK-ENTRA-041"] = "Dispositivos não autorizados podem ser ingressados e ganhar a confiança que o diretório concede a dispositivos da organização.",
        ["AK-ENTRA-042"] = "Uma conta comprometida registra vários dispositivos antes de esbarrar no limite, e cada registro passa a ser tratado como dispositivo da organização nas decisões de acesso.",
        ["AK-ENTRA-043"] = "Uma conta global usada numa estação de trabalho deixa credenciais nessa máquina; quem comprometer a estação pode capturá-las e agir com o papel mais amplo do locatário.",
        ["AK-ENTRA-044"] = "Um dispositivo pode ficar sem as proteções que a organização configurou, e credenciais guardadas nele podem ser extraídas.",
        ["AK-ENTRA-045"] = "Uma senha de administrador local repetida permite que o comprometimento de uma máquina se espalhe para outras.",
        ["AK-ENTRA-046"] = "Conteúdo de áreas que deveriam ser restritas pode ser lido por qualquer pessoa da organização.",
        ["AK-ENTRA-047"] = "Quem comprometer o diretório local pode assumir essas contas — por redefinição de senha ou por bilhete forjado — e usá-las com os papéis administrativos que elas têm na nuvem.",
        ["AK-ENTRA-048"] = "Cada serviço de produtividade licenciado é mais uma superfície da conta administrativa: e-mail, arquivos compartilhados e aplicativos do Office servem para entregar conteúdo malicioso a quem administra o ambiente.",
        ["AK-ENTRA-049"] = "Com um único administrador global, a perda dessa conta trava a administração; com muitos, amplia-se o número de contas cujo comprometimento dá controle do locatário.",
        ["AK-ENTRA-050"] = "Quem obtiver a credencial de uma dessas identidades age com as permissões do papel na hora, sem etapa de ativação, justificativa ou aprovação que limite ou registre o uso.",
        ["AK-ENTRA-051"] = "Se uma conta elegível for comprometida, o papel mais amplo do locatário é ativado sem que outra pessoa precise aprovar — e sem o registro que a aprovação deixaria.",
        ["AK-ENTRA-052"] = "Quem comprometer uma conta elegível ativa o papel e, com ele, concede a si mesmo ou a terceiros outros papéis administrativos, sem uma segunda pessoa no caminho.",
        ["AK-ENTRA-053"] = "Terceiros podem continuar lendo arquivos, equipes e sistemas da organização depois de encerrada a relação que justificou o acesso.",
        ["AK-ENTRA-054"] = "Atribuições nos papéis mais amplos permanecem depois de mudanças de função, sem confirmação periódica de necessidade.",
        ["AK-ENTRA-055"] = "Bloqueios e exigências extras ficam menos precisos: podem incomodar usuários legítimos ou deixar passar acessos de origem suspeita.",
        ["AK-ENTRA-056"] = "Entradas de países onde a organização não opera são tratadas como qualquer outra, sem barreira adicional.",
        ["AK-ENTRA-057"] = "Documentos da organização podem ser gravados em contas pessoais em outros serviços, onde não há como aplicar retenção, bloqueio ou investigação.",
        ["AK-ENTRA-058"] = "Um token de sessão administrativa roubado continua válido por mais tempo e pode ser reutilizado a partir de outro navegador.",
        ["AK-ENTRA-059"] = "Uma sessão administrativa capturada permite alterar configurações no alcance dos papéis do administrador, sem precisar da senha de novo.",
        ["AK-ENTRA-060"] = "Quem já tenha a senha continua acessando os dados da conta até que alguém intervenha manualmente.",
        ["AK-ENTRA-061"] = "Uma entrada anômala é aceita só com a senha, sem prova adicional de identidade.",
        ["AK-ENTRA-062"] = "Entradas de risco elevado continuam sendo aceitas enquanto a suspeita não é investigada.",
        ["AK-ENTRA-063"] = "Dados corporativos podem ser acessados e baixados em computadores sem as proteções da organização (criptografia, antivírus, gerenciamento).",
        ["AK-ENTRA-064"] = "Se uma conta já estiver nas mãos de outra pessoa, ela registra o próprio método de MFA e mantém o acesso mesmo depois da troca de senha.",
        ["AK-ENTRA-065"] = "Um dispositivo controlado por terceiros pode receber as políticas e o acesso de um dispositivo gerenciado da organização.",
        ["AK-ENTRA-066"] = "O usuário convencido a digitar um código entrega a sessão a quem pediu: o acesso segue com os dados e aplicações daquela conta, sem que a senha nem o segundo fator mudem de mãos.",
        ["AK-ENTRA-067"] = "Um acesso obtido por roubo de sessão pode durar muito tempo, ampliando o que pode ser lido ou alterado na conta.",
        ["AK-ENTRA-068"] = "Uma sessão já autenticada pode ser levada para um dispositivo fora do controle da organização, e o acesso continua valendo lá, com os dados da conta.",
        ["AK-ENTRA-069"] = "Uma sessão deixada aberta num computador compartilhado ou não gerenciado continua válida: quem sentar depois abre e-mail e documentos da conta sem autenticar.",

        // ---- [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams ----
        ["AK-TEAMS-001"] = "Documentos de trabalho podem ficar guardados fora do ambiente da organização, acessíveis por contas pessoais que a organização não administra e não consegue revogar.",
        ["AK-TEAMS-002"] = "Conteúdo enviado por qualquer remetente pode aparecer dentro de um canal de trabalho com a aparência de uma publicação legítima da equipe.",
        ["AK-TEAMS-003"] = "Pessoas da organização podem ser abordadas por chat por qualquer organização externa ainda não bloqueada, com o grau de confiança que uma mensagem no Teams costuma ter.",
        ["AK-TEAMS-004"] = "Conversas de trabalho podem ocorrer com interlocutores que nenhuma organização identifica, sem caminho de apuração se algo der errado.",
        ["AK-TEAMS-005"] = "Qualquer pessoa com uma conta gratuita do Teams pode abordar diretamente colaboradores específicos — inclusive por nome e cargo — para tentar um golpe.",
        ["AK-TEAMS-006"] = "Mensagens podem chegar com o nome de uma organização inventada em um locatário recém-criado, e a pessoa abordada as vê como um contato externo comum.",
        ["AK-TEAMS-007"] = "Um aplicativo de terceiro adicionado por um usuário comum pode passar a ler as conversas e os arquivos dos canais em que for instalado, com o consentimento de quem o instalou.",
        ["AK-TEAMS-008"] = "Alguém sem identificação pode assistir a uma reunião de trabalho do início ao fim, ouvindo o que for dito e vendo o que for apresentado, sem que reste registro de quem era.",
        ["AK-TEAMS-009"] = "Onde as condições documentadas estiverem presentes, uma reunião recorrente pode ser aberta sem nenhum participante verificado dentro e usada como sala de encontro fora do horário previsto; onde não estiverem, o efeito alcança quem entra por discagem telefônica.",
        ["AK-TEAMS-010"] = "Pessoas de fora podem entrar em uma reunião de trabalho sem que ninguém as admita, inclusive antes de o organizador perceber que a sala está aberta.",
        ["AK-TEAMS-011"] = "Qualquer pessoa de posse do número e do código pode ouvir a reunião inteira, e a lista de participantes mostra apenas um número de telefone.",
        ["AK-TEAMS-012"] = "Um link de golpe pode ser publicado no chat de uma reunião de trabalho por alguém não identificado e permanecer na conversa depois do encontro.",
        ["AK-TEAMS-013"] = "Uma pessoa do conjunto que a política promove a apresentador pode exibir conteúdo indevido ou interromper a reunião removendo outras pessoas, sem que o organizador tenha concedido esse papel.",
        ["AK-TEAMS-014"] = "Um participante de fora pode operar a máquina de um colaborador durante a reunião — abrindo arquivos, navegando e executando o que estiver ao alcance daquela sessão.",
        ["AK-TEAMS-015"] = "Um colaborador convidado para uma reunião externa pode receber, pelo chat dela, links e arquivos de participantes que a organização não conhece, num canal cuja composição, retenção e auditoria pertencem a quem hospeda.",
        ["AK-TEAMS-016"] = "Conversas internas podem ficar guardadas em arquivos de vídeo compartilháveis, sujeitos a repasse e a pedidos de apresentação em disputas, sem que quem falou tenha decidido reter aquilo.",
        ["AK-TEAMS-017"] = "Uma campanha de golpe por chat pode atingir várias pessoas sem que nenhuma consiga transformar a suspeita em alerta pelo próprio Teams; o que chegar à equipe de segurança dependerá de outras fontes de detecção ou de relato por fora da ferramenta.",

        ["AK-GWS-001"] = "Sem 2SV inscrita, a conta não tem segundo fator para apresentar: onde a entrada for aceita só com a senha, quem a tiver administra o Workspace.",
        ["AK-GWS-002"] = "Cada usuário sem 2SV depende da senha nos acessos que não exigirem segundo fator; uma credencial vazada leva ao e-mail e aos arquivos daquele usuário.",
        ["AK-GWS-003"] = "Um superadministrador sem uso pode ser usado por quem roubar a credencial, e o uso anômalo tende a passar despercebido.",
        ["AK-GWS-004"] = "Mensagens e arquivos destinados ao grupo podem chegar a pessoas de fora da organização.",
        ["AK-GWS-005"] = "Os arquivos expostos podem ser acessados fora da organização enquanto o compartilhamento permanecer.",
        ["AK-GWS-006"] = "E-mail, arquivos ou agenda do usuário podem ser lidos ou alterados por um terceiro, no limite dos escopos concedidos.",
    };

    /// <summary>Impacto potencial do controle, ou <c>null</c> quando o controle não tem texto catalogado.</summary>
    public static string? For(string indicatorId) =>
        ById.TryGetValue((indicatorId ?? "").Trim(), out var t) ? t : null;

    public static IReadOnlyCollection<string> Ids => (IReadOnlyCollection<string>)ById.Keys;
}
