// [AEGIS-NIST-RECOVER-GUIDE-01] Roteiro editorial de entrevista e maturidade do AEGIS NIST, por subcategoria.
// Conteúdo AUTORAL (redação do Codex para o AEGIS; RC.RP-06 e RC.CO-03 preservam os exemplos de Felipe) — não é tradução
// oficial nem requisito do NIST. Estático e empacotado com a aplicação: não depende de IA, de arquivo externo nem do banco.
// As situações ilustrativas orientam o julgamento do analista; não atribuem nota nem mudam a escala, os pesos ou a agregação.
// O nome de cada nível vem de MATURITY_LEVEL_LABELS (catálogo único). Primeira entrega: as 8 subcategorias de RECOVER.

import type { NistFunctionCode } from './nist.models';

export interface NistInterviewFollowUp {
  /** Pergunta complementar (orienta a conversa; não é campo obrigatório nem item de checklist). */
  question: string;
  /** O que investigar com a pergunta. */
  focus: string;
}

export interface NistInterviewGuide {
  code: string;
  categoryCode: string;
  /** Título curto em português (redação do AEGIS). */
  title: string;
  mainQuestion: string;
  followUps: readonly NistInterviewFollowUp[];
  /** Situação ilustrativa de cada nível 1–5 da escala do AEGIS, na ordem. */
  levelExamples: readonly [string, string, string, string, string];
  /** Atenção na interpretação (ajuda contextual). */
  interpretationNote: string;
  /** Resultado esperado oficial (inglês), para conferência com o catálogo. */
  officialOutcome: string;
}

export const NIST_INTERVIEW_GUIDE_VERSION = 'recover-pt-BR-v1.0';

/** Orientações de uso do roteiro (ajuda recolhível na tela). */
export const NIST_INTERVIEW_GUIDE_NOTES: readonly string[] = [
  'Perguntas, títulos em português e situações ilustrativas são redação autoral para condução da entrevista; não são tradução oficial nem requisitos adicionais do NIST.',
  'Os cinco níveis pertencem à metodologia autoral do AEGIS; não são os Implementation Tiers do NIST.',
  'As situações ilustrativas orientam o julgamento do analista, sem atribuição automática de nota ou criação de novos critérios de cálculo.',
  'Não selecionar o nível 1 apenas porque faltam informações. Sem nível e Não se aplica continuam sendo opções distintas, conforme as regras existentes.',
  'Uma política ou um plano demonstra o que foi definido; a execução deve ser investigada por registros de incidentes ou de exercícios, identificando sua origem.',
  'Não ter ocorrido incidente real não torna a subcategoria automaticamente não aplicável. Investigue a preparação e os exercícios disponíveis, registrando os limites da evidência.',
];

export const NIST_INTERVIEW_GUIDES: readonly NistInterviewGuide[] = [
  {
    code: 'RC.RP-01',
    categoryCode: 'RC.RP',
    title: 'Início e execução do plano de recuperação',
    mainQuestion: 'Quando o processo de resposta a um incidente aciona a recuperação, como vocês colocam em prática os procedimentos previstos no plano? Quem autoriza o início, como os responsáveis são mobilizados e como a execução é acompanhada?',
    followUps: [
      { question: 'Como a equipe de resposta comunica que a recuperação deve começar?', focus: 'Acionamento da recuperação a partir da resposta ao incidente' },
      { question: 'Quais procedimentos de recuperação são executados após esse acionamento?', focus: 'Aplicação do plano na recuperação' },
      { question: 'Quem pode autorizar a execução de cada procedimento?', focus: 'Autorizações e responsabilidades' },
      { question: 'Como as pessoas envolvidas tomam conhecimento das tarefas e das autorizações necessárias?', focus: 'Mobilização e entendimento do plano' },
      { question: 'Como é registrada a execução dos procedimentos e o tratamento dos impedimentos?', focus: 'Acompanhamento e rastreabilidade da execução' },
      { question: 'Podem demonstrar como o plano foi acionado e executado em um incidente ou exercício?', focus: 'Evidência de aplicação, distinguindo incidente de exercício' },
    ],
    levelExamples: [
      'A recuperação é iniciada e executada quando acionada pela resposta ao incidente, mas depende de decisões pontuais e do conhecimento das pessoas, sem procedimento consistente.',
      'O plano define como iniciar e executar a recuperação, os procedimentos, os responsáveis e as autorizações; há registros de sua utilização.',
      'A gestão acompanha a execução do plano, confirma a mobilização das equipes e dos recursos e trata desvios ou impedimentos em relação ao previsto.',
      'São acompanhados indicadores, como tempo entre acionamento e início, cumprimento dos procedimentos e desvios de execução, para orientar correções e informar a gestão.',
      'Lições de incidentes, exercícios e indicadores são usadas para melhorar continuamente o acionamento e a execução do plano, verificando o efeito das melhorias.',
    ],
    interpretationNote: 'O foco é a execução da recuperação após seu acionamento pela resposta. A definição e aplicação dos critérios para iniciar a recuperação também é investigada em RS.MA-05; não substituir um resultado pelo outro.',
    officialOutcome: 'The recovery portion of the incident response plan is executed once initiated from the incident response process',
  },
  {
    code: 'RC.RP-02',
    categoryCode: 'RC.RP',
    title: 'Seleção e execução das ações de recuperação',
    mainQuestion: 'Durante a recuperação de um incidente, como vocês escolhem o que será recuperado, definem o alcance e a ordem das ações e garantem sua execução? Como essas decisões são ajustadas quando as necessidades ou os recursos mudam?',
    followUps: [
      { question: 'Como identificam as ações necessárias e o que cada uma deve recuperar?', focus: 'Seleção das ações e definição do alcance' },
      { question: 'Quais critérios determinam a ordem de execução das ações?', focus: 'Priorização conforme as necessidades da organização' },
      { question: 'Como dependências entre sistemas, equipes e fornecedores entram nessa decisão?', focus: 'Viabilidade e coordenação das ações' },
      { question: 'Como verificam se há pessoas, acessos e recursos para executar as ações escolhidas?', focus: 'Disponibilidade de recursos e atribuição de tarefas' },
      { question: 'O que acontece quando uma ação não funciona ou as prioridades precisam mudar?', focus: 'Reavaliação e ajuste das ações de recuperação' },
      { question: 'Podem apresentar um caso com as ações escolhidas, sua ordem, eventuais alterações e o registro de execução?', focus: 'Rastreabilidade das decisões e da aplicação' },
    ],
    levelExamples: [
      'As ações de recuperação são escolhidas e executadas conforme a necessidade imediata, com alcance e ordem definidos informalmente durante o incidente.',
      'Há critérios e procedimentos documentados para escolher, delimitar, priorizar e executar ações de recuperação, com registro das decisões e tarefas realizadas.',
      'Responsáveis acompanham a execução, coordenam dependências e recursos e reavaliam as ações quando as condições ou as necessidades da organização mudam.',
      'Indicadores de prazo, cumprimento da sequência, retrabalho e atendimento às prioridades são usados para avaliar as ações e corrigir desvios.',
      'Resultados de recuperações, exercícios e indicadores aprimoram continuamente os critérios de escolha e priorização e a forma de executar as ações.',
    ],
    interpretationNote: 'O foco são as decisões e ações concretas durante a recuperação. Uma lista de sistemas críticos ou um plano sem registros de aplicação não demonstra, isoladamente, que essas ações foram executadas.',
    officialOutcome: 'Recovery actions are selected, scoped, prioritized, and performed',
  },
  {
    code: 'RC.RP-03',
    categoryCode: 'RC.RP',
    title: 'Verificação dos recursos antes da restauração',
    mainQuestion: 'Antes de usar backups, imagens ou outros recursos para restaurar o ambiente após um incidente, como vocês verificam sua integridade e se há sinais de corrupção ou comprometimento? Como decidem quais recursos podem ser utilizados?',
    followUps: [
      { question: 'Quais backups e outros recursos precisam ser verificados antes da restauração?', focus: 'Abrangência da verificação prévia' },
      { question: 'Como verificam corrupção, alterações indevidas ou sinais de comprometimento nesses recursos?', focus: 'Métodos de verificação da integridade' },
      { question: 'Como confirmam que a verificação ocorreu antes de utilizar o recurso?', focus: 'Momento da verificação e prevenção de uso indevido' },
      { question: 'Quem avalia os resultados e autoriza ou impede o uso de um recurso?', focus: 'Responsabilidade pela decisão' },
      { question: 'O que fazem quando um backup ou outro recurso apresenta falhas ou suspeitas?', focus: 'Tratamento de recursos inadequados' },
      { question: 'Podem apresentar o resultado de uma verificação anterior e a decisão sobre o uso do recurso?', focus: 'Evidência da verificação e de seu resultado' },
    ],
    levelExamples: [
      'Alguns backups ou recursos são verificados antes da restauração, mas os métodos e as decisões dependem de iniciativa individual e não seguem um padrão consistente.',
      'Procedimentos definem os recursos a verificar, os métodos, os critérios e o registro dos resultados antes do uso na restauração.',
      'Responsáveis acompanham as verificações, disponibilizam os recursos necessários e tratam falhas ou suspeitas antes de autorizar o uso na restauração.',
      'Indicadores, como proporção de recursos verificados antes do uso, falhas encontradas e tempo de verificação, orientam correções e acompanhamento pela gestão.',
      'As verificações são continuamente aprimoradas a partir de testes, incidentes e falhas detectadas, avaliando se as melhorias aumentam a confiabilidade dos recursos de restauração.',
    ],
    interpretationNote: 'Ter backup ou registrar que sua criação terminou com sucesso não comprova a verificação de integridade antes da restauração. Não confundir os recursos de origem, avaliados aqui, com os ativos já restaurados de RC.RP-05.',
    officialOutcome: 'The integrity of backups and other restoration assets is verified before using them for restoration',
  },
  {
    code: 'RC.RP-04',
    categoryCode: 'RC.RP',
    title: 'Definição das condições de operação após o incidente',
    mainQuestion: 'Como vocês definem em quais condições a organização pode operar após um incidente, considerando os serviços essenciais à sua missão e os riscos de segurança ainda existentes? Como essas condições são acordadas com os responsáveis pelo negócio e pelos sistemas?',
    followUps: [
      { question: 'Quais funções e serviços são essenciais para a missão e precisam orientar a retomada?', focus: 'Necessidades críticas do negócio e da missão' },
      { question: 'Como os impactos ao negócio e os objetivos de serviço influenciam as condições e a ordem de retomada?', focus: 'Relação entre criticidade, impacto e operação pós-incidente' },
      { question: 'Quais riscos de segurança precisam ser considerados antes e durante a operação retomada?', focus: 'Gestão dos riscos na definição das condições operacionais' },
      { question: 'Se a operação precisar ser temporariamente limitada, como definem as restrições, os controles e os responsáveis?', focus: 'Condições operacionais ajustadas ao risco' },
      { question: 'Como os responsáveis pelo negócio e pelos sistemas confirmam que as condições estabelecidas atendem às necessidades?', focus: 'Coordenação e aceitação das condições de operação' },
      { question: 'Como acompanham o desempenho do ambiente retomado e revisam essas condições quando necessário?', focus: 'Adequação da operação e reavaliação' },
      { question: 'Podem mostrar uma decisão anterior sobre as condições de operação após um incidente ou exercício?', focus: 'Evidência da decisão e de sua fundamentação' },
    ],
    levelExamples: [
      'As condições para operar após o incidente são definidas caso a caso, considerando necessidades imediatas, mas sem uma abordagem consistente para os serviços críticos e os riscos de segurança.',
      'Há critérios documentados para estabelecer as condições de operação pós-incidente, considerando serviços críticos, impactos ao negócio e riscos de segurança, com registro das decisões.',
      'A gestão e os responsáveis pelo negócio e pelos sistemas coordenam as condições de retomada, acompanham restrições e riscos e disponibilizam recursos para manter a operação acordada.',
      'Indicadores de atendimento aos serviços essenciais, desempenho do ambiente retomado e cumprimento das condições de segurança são usados para avaliar e ajustar a operação pós-incidente.',
      'As condições e os critérios de operação pós-incidente são aprimorados continuamente com base em resultados, mudanças no negócio e análises de riscos, verificando o efeito dos ajustes.',
    ],
    interpretationNote: 'O foco é definir a operação pós-incidente considerando missão e risco. A priorização de ações de RC.RP-02 e a validação técnica de RC.RP-05 podem apoiar a análise, mas não substituem essa decisão.',
    officialOutcome: 'Critical mission functions and cybersecurity risk management are considered to establish post-incident operational norms',
  },
  {
    code: 'RC.RP-05',
    categoryCode: 'RC.RP',
    title: 'Validação dos ativos restaurados e do retorno à operação',
    mainQuestion: 'Depois de restaurar os ativos afetados por um incidente, como vocês verificam sua integridade, restabelecem os sistemas e serviços e confirmam que a operação normal foi recuperada? Quem valida os resultados e autoriza o retorno ao uso?',
    followUps: [
      { question: 'Quais verificações são realizadas nos ativos depois da restauração?', focus: 'Integridade e adequação dos ativos restaurados' },
      { question: 'Como investigam sinais de comprometimento ainda presentes e verificam o tratamento das causas do incidente?', focus: 'Condições de segurança antes do retorno ao uso' },
      { question: 'Como verificam se sistemas, serviços e dados voltaram a funcionar como esperado?', focus: 'Restabelecimento e validação funcional' },
      { question: 'Quais critérios indicam que a condição normal de operação foi recuperada?', focus: 'Critérios de confirmação do retorno operacional' },
      { question: 'Quem valida os resultados e autoriza a utilização dos sistemas restaurados?', focus: 'Responsabilidade e autorização do retorno ao uso' },
      { question: 'O que acontece quando uma validação falha ou um serviço não atende às condições esperadas?', focus: 'Tratamento das falhas de restauração' },
      { question: 'Podem apresentar registros de validação e de confirmação da retomada de um sistema ou serviço?', focus: 'Rastreabilidade da restauração e da aceitação' },
    ],
    levelExamples: [
      'Os ativos restaurados e o funcionamento dos serviços são verificados de forma pontual, com a retomada baseada no julgamento das equipes, sem critérios ou registros consistentes.',
      'Há procedimentos e critérios documentados para verificar os ativos restaurados, validar os sistemas e serviços e registrar a confirmação do retorno à operação normal.',
      'Responsáveis técnicos e de negócio acompanham as validações, tratam as falhas encontradas e confirmam o retorno ao uso conforme as condições estabelecidas.',
      'Indicadores de falhas nas validações, reincidências, tempo até a confirmação e atendimento aos critérios de retorno orientam correções e decisões da gestão.',
      'Os métodos de validação e os critérios de retorno são continuamente aperfeiçoados com base nos resultados de incidentes, exercícios e acompanhamento da operação recuperada.',
    ],
    interpretationNote: 'Um sistema estar acessível não demonstra, isoladamente, integridade ou recuperação da operação normal. Esta subcategoria examina os ativos já restaurados; RC.RP-03 examina os recursos antes de serem utilizados na restauração.',
    officialOutcome: 'The integrity of restored assets is verified, systems and services are restored, and normal operating status is confirmed',
  },
  {
    code: 'RC.RP-06',
    categoryCode: 'RC.RP',
    title: 'Encerramento formal da recuperação',
    mainQuestion: 'Após a recuperação de um incidente de segurança, como vocês determinam que todas as atividades foram concluídas e que o processo pode ser formalmente encerrado? Existem critérios documentados e alguém responsável por aprovar esse encerramento?',
    followUps: [
      { question: 'Existem critérios para considerar a recuperação concluída?', focus: 'Definição do encerramento' },
      { question: 'Quem tem autoridade para declarar o encerramento?', focus: 'Responsabilidade' },
      { question: 'Como essa decisão é registrada?', focus: 'Rastreabilidade' },
      { question: 'Existe relatório ou checklist final?', focus: 'Documentação' },
      { question: 'As ações e validações realizadas são registradas?', focus: 'Histórico da recuperação' },
      { question: 'Há um processo para registrar pendências ou riscos residuais?', focus: 'Tratamento de condições remanescentes' },
      { question: 'Conseguem demonstrar um encerramento anterior?', focus: 'Evidência de aplicação' },
    ],
    levelExamples: [
      'O encerramento ocorre verbalmente, sem critérios definidos ou documentação consistente.',
      'Existem critérios e procedimentos formais para encerramento e documentação.',
      'A gestão acompanha o cumprimento do processo, com responsáveis e recursos definidos.',
      'Existem indicadores para avaliar a qualidade, o prazo e o cumprimento dos critérios de encerramento.',
      'O processo é continuamente aprimorado com base nos resultados e nas análises anteriores.',
    ],
    interpretationNote: 'Concluir a documentação do incidente faz parte do resultado esperado. Encerrar a recuperação não exige presumir a eliminação de todo risco residual: investigue como pendências são registradas, atribuídas e tratadas conforme os critérios de encerramento. Relatório e checklist são exemplos de registro, não formatos obrigatórios.',
    officialOutcome: 'The end of incident recovery is declared based on criteria, and incident-related documentation is completed',
  },
  {
    code: 'RC.CO-03',
    categoryCode: 'RC.CO',
    title: 'Comunicação da recuperação às partes interessadas',
    mainQuestion: 'Durante a recuperação de um incidente de segurança, como vocês comunicam o andamento das atividades e o restabelecimento dos serviços às áreas internas e às partes externas envolvidas? Existem responsáveis, canais e critérios definidos para essas atualizações?',
    followUps: [
      { question: 'Quem precisa ser informado durante a recuperação?', focus: 'Partes interessadas designadas' },
      { question: 'Quem é responsável por enviar as atualizações?', focus: 'Responsabilidades' },
      { question: 'Quais canais são utilizados?', focus: 'Meios de comunicação' },
      { question: 'Com qual frequência o status é atualizado?', focus: 'Regularidade' },
      { question: 'Existe um procedimento ou matriz de comunicação?', focus: 'Formalização' },
      { question: 'As comunicações ficam registradas?', focus: 'Rastreabilidade' },
      { question: 'Podem apresentar um exemplo de atualização enviada durante uma recuperação?', focus: 'Execução prática' },
    ],
    levelExamples: [
      'Atualizações sobre a recuperação são enviadas às partes envolvidas de forma pontual, sem padrão consistente de destinatários, responsáveis, canais ou registros.',
      'Procedimentos definem destinatários internos e externos, responsáveis, canais, conteúdo e critérios de atualização, com registros das comunicações realizadas.',
      'Responsáveis acompanham as atualizações, coordenam as partes envolvidas e verificam o cumprimento do fluxo e dos acordos de compartilhamento aplicáveis.',
      'Indicadores de prazo, alcance das partes designadas e cumprimento das atualizações previstas são usados para avaliar a comunicação e corrigir desvios.',
      'A comunicação da recuperação é aprimorada continuamente com base em resultados, retorno das partes interessadas e lições de incidentes ou exercícios.',
    ],
    interpretationNote: 'O foco é comunicar atividades e progresso às partes internas e externas designadas, com compartilhamento adequado da informação. Comunicação pública é tratada especificamente em RC.CO-04; notificação inicial do incidente é investigada em RS.CO-02.',
    officialOutcome: 'Recovery activities and progress in restoring operational capabilities are communicated to designated internal and external stakeholders',
  },
  {
    code: 'RC.CO-04',
    categoryCode: 'RC.CO',
    title: 'Atualizações públicas sobre a recuperação',
    mainQuestion: 'Quando são necessárias atualizações públicas sobre a recuperação de um incidente, como vocês definem o que será divulgado e garantem que a mensagem e os meios de publicação foram aprovados? Quem autoriza e acompanha essas divulgações?',
    followUps: [
      { question: 'Como determinam quando uma atualização pública sobre a recuperação é necessária?', focus: 'Condições para realizar a divulgação pública' },
      { question: 'Quem prepara a mensagem e quem pode aprová-la antes da publicação?', focus: 'Responsabilidade e aprovação do conteúdo' },
      { question: 'Quais meios e porta-vozes estão autorizados a divulgar as atualizações?', focus: 'Métodos e canais aprovados' },
      { question: 'Como confirmam que a mensagem reflete a situação real da recuperação e protege informações que não devem ser expostas?', focus: 'Exatidão e adequação da informação pública' },
      { question: 'Como as áreas responsáveis coordenam as divulgações com as regras e os procedimentos aplicáveis?', focus: 'Coordenação e aderência ao fluxo de comunicação' },
      { question: 'Como registram a aprovação, o conteúdo divulgado e eventuais correções ou novas atualizações?', focus: 'Rastreabilidade e atualização das mensagens públicas' },
      { question: 'Podem demonstrar uma atualização pública anterior ou um exercício do processo de aprovação e publicação?', focus: 'Evidência de aplicação, distinguindo publicação de exercício' },
    ],
    levelExamples: [
      'Quando há divulgação pública sobre a recuperação, a mensagem e o meio são aprovados pontualmente, sem fluxo ou registro consistente de preparação, aprovação e publicação.',
      'Há procedimentos documentados para preparar e aprovar as mensagens, definir os meios de publicação e registrar as atualizações públicas realizadas.',
      'As áreas e os responsáveis envolvidos coordenam as divulgações e acompanham o uso das mensagens e dos meios aprovados, tratando desvios e necessidades de atualização.',
      'Indicadores de tempo de aprovação e publicação, aderência ao fluxo e necessidade de correções são usados para avaliar a comunicação pública e orientar ajustes.',
      'O processo de atualização pública é aprimorado continuamente com base em exercícios, divulgações anteriores, indicadores e retorno das partes envolvidas.',
    ],
    interpretationNote: 'Uma comunicação interna ou restrita a um fornecedor não demonstra atualização pública. Não presumir que todo incidente exige divulgação pública; investigar as condições e os procedimentos aplicáveis ao contexto da organização.',
    officialOutcome: 'Public updates on incident recovery are shared using approved methods and messaging',
  },
];

const BY_CODE = new Map(NIST_INTERVIEW_GUIDES.map((g) => [g.code, g]));

/** Roteiro da subcategoria ("RC.RP-06"); nulo quando ainda não há conteúdo editorial — nada é gerado no lugar. */
export function interviewGuideFor(code: string | null | undefined): NistInterviewGuide | null {
  return BY_CODE.get((code ?? '').trim().toUpperCase()) ?? null;
}

/** A função já tem roteiro editorial (e, com ele, a área de trabalho com a lista contextual). */
export function hasInterviewGuides(fn: NistFunctionCode | null | undefined): boolean {
  return !!fn && NIST_INTERVIEW_GUIDES.some((g) => g.code.startsWith(fn + '.'));
}
