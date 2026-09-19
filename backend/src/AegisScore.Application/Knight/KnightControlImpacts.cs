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
        ["AK-ENTRA-001"] = "Se a senha de uma dessas contas vazar, nada além dela protege o acesso administrativo: quem a usar age com os papéis da conta sobre o diretório.",
        ["AK-ENTRA-002"] = "Cada conta ou aplicação a mais com papel administrativo é mais um ponto cujo comprometimento concede alterações no diretório; o alcance de cada uma é o dos papéis que ela tem.",
        ["AK-ENTRA-003"] = "Uma mensagem maliciosa aberta pelo administrador é executada com a sessão de quem pode alterar o ambiente.",
        ["AK-ENTRA-004"] = "Uma conta de convidado sem dono pode continuar lendo o que foi compartilhado com ela depois do fim da relação que justificou o convite.",
        ["AK-ENTRA-005"] = "Um segredo de conta de serviço vazado dá acesso contínuo, sem segundo fator, ao que essa conta alcança.",
        ["AK-ENTRA-006"] = "Os usuários sem método registrado dependem só da senha: um vazamento de credencial desses usuários dá acesso aos dados e aplicações de cada um.",
        ["AK-ENTRA-007"] = "Ataques de senha em massa podem entrar por protocolos que não pedem segundo fator, alcançando as caixas de correio e dados das contas atingidas.",
        ["AK-ENTRA-008"] = "Um administrador sem exigência de MFA pode ser usado com a senha apenas, com o alcance dos papéis que ocupa.",
        ["AK-ENTRA-009"] = "Credenciais vencidas interrompem integrações; credenciais esquecidas e válidas por longo prazo permitem acesso da aplicação sem ninguém perceber.",
        ["AK-ENTRA-010"] = "Quem obtiver a credencial de uma dessas aplicações lê ou altera dados do diretório com as permissões concedidas, sem usuário e sem MFA.",
        ["AK-ENTRA-011"] = "Uma conta privilegiada sem uso pode ser usada por quem roubar a credencial, e o uso anômalo tende a passar despercebido.",
        ["AK-ENTRA-012"] = "A segurança dessas contas administrativas depende de outra organização; um comprometimento lá alcança o seu diretório com os papéis concedidos.",
        ["AK-ENTRA-013"] = "Essas aplicações podem ler dados de qualquer usuário no limite das permissões delegadas concedidas.",
        ["AK-ENTRA-014"] = "Sem base mínima, a proteção de cada conta depende só da senha, e credenciais vazadas dão acesso direto aos dados de quem foi atingido.",
        ["AK-ENTRA-015"] = "A organização pode ficar sem acesso administrativo durante um incidente ou uma falha de configuração, sem como corrigir políticas até o suporte do fornecedor intervir.",
        ["AK-ENTRA-016"] = "Aplicações criadas por usuários podem receber credenciais e pedir permissões fora de qualquer revisão, criando acessos persistentes que a TI não conhece.",
        ["AK-ENTRA-017"] = "Dados e identidades da organização podem acabar em locatários sem a retenção e a auditoria corporativas, e esse acesso não aparece nos inventários.",
        ["AK-ENTRA-018"] = "Grupos criados sem governança passam a conceder acesso a recursos sem dono nem revisão, dificultando saber quem acessa o quê.",
        ["AK-ENTRA-019"] = "Com o dispositivo em mãos (perdido, furtado ou de ex-colaborador), os dados do disco ficam legíveis: a criptografia deixa de proteger a informação guardada nele.",
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
        ["AK-ENTRA-042"] = "Uma conta comprometida registra vários dispositivos indevidos antes de encontrar o limite.",
        ["AK-ENTRA-043"] = "Credenciais de administrador global passam a ser usadas em estações de usuários, onde podem ser capturadas.",
        ["AK-ENTRA-044"] = "Um dispositivo pode ficar sem as proteções que a organização configurou, e credenciais guardadas nele podem ser extraídas.",
        ["AK-ENTRA-045"] = "Uma senha de administrador local repetida permite que o comprometimento de uma máquina se espalhe para outras.",
        ["AK-ENTRA-046"] = "Conteúdo de áreas que deveriam ser restritas pode ser lido por qualquer pessoa da organização.",
        ["AK-ENTRA-047"] = "Quem comprometer o diretório local controla essas contas e, com elas, os papéis administrativos na nuvem.",
        ["AK-ENTRA-048"] = "Um único e-mail malicioso aberto por um administrador pode resultar em alterações administrativas, no alcance dos papéis da conta.",
        ["AK-ENTRA-049"] = "Com um único administrador global, a perda dessa conta trava a administração; com muitos, amplia-se o número de contas cujo comprometimento dá controle do locatário.",
        ["AK-ENTRA-050"] = "Quem obtiver a credencial de uma dessas identidades age com as permissões do papel na hora, sem etapa de ativação, justificativa ou aprovação que limite ou registre o uso.",
        ["AK-ENTRA-051"] = "Um elegível comprometido ativa o papel mais amplo do locatário sem que outra pessoa precise aprovar.",
        ["AK-ENTRA-052"] = "O controle administrativo do locatário pode passar para quem comprometer uma única conta elegível, sem uma segunda pessoa no caminho.",
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
        ["AK-ENTRA-064"] = "O invasor mantém o acesso à conta mesmo depois da troca de senha, prolongando a exposição dos dados daquela conta.",
        ["AK-ENTRA-065"] = "Um dispositivo controlado por terceiros pode receber as políticas e o acesso de um dispositivo gerenciado da organização.",
        ["AK-ENTRA-066"] = "O atacante acessa os dados e aplicações da vítima sem precisar da senha nem do segundo fator.",
        ["AK-ENTRA-067"] = "Um acesso obtido por roubo de sessão pode durar muito tempo, ampliando o que pode ser lido ou alterado na conta.",
        ["AK-ENTRA-068"] = "Os dados acessados a partir de um dispositivo sem as proteções da organização ficam fora do controle corporativo.",
        ["AK-ENTRA-069"] = "E-mail e documentos da conta ficam acessíveis a quem usar a máquina depois, sem nova autenticação.",
        ["AK-GWS-001"] = "Se a senha de um superadministrador vazar, nada além dela protege o controle administrativo do Workspace.",
        ["AK-GWS-002"] = "Os usuários sem 2SV dependem só da senha: um vazamento dá acesso ao e-mail e aos arquivos de cada um.",
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
