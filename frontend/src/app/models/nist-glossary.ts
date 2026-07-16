// Camada de HUMANIZAÇÃO das siglas NIST CSF 2.0: traduz o código técnico (ex.: "PR.AA-01") num rótulo
// amigável em PT-BR para o usuário final — a sigla é essencial ao motor interno, mas hostil na tela.
// Constantes + funções PURAS (sem Angular), reutilizáveis em qualquer componente que renderize controles.

/**
 * Nome amigável por CATEGORIA NIST (o prefixo do código, ex.: "PR.AA"). Cobre todas as categorias que a
 * plataforma avalia hoje; ampliar é só adicionar uma entrada. A tradução é por categoria (não por
 * subcategoria) de propósito: "PR.AA-01" e um futuro "PR.AA-05" compartilham o mesmo rótulo de família.
 */
export const NIST_CATEGORY_NAMES: Record<string, string> = {
  // Govern
  'GV.SC': 'Cadeia de Suprimentos',
  'GV.RR': 'Papéis e Responsabilidades',
  'GV.PO': 'Políticas de Segurança',
  // Identify
  'ID.AM': 'Gestão de Ativos',
  'ID.RA': 'Avaliação de Riscos',
  // Protect
  'PR.AA': 'Identidade e Acesso',
  'PR.DS': 'Proteção de Dados',
  'PR.PS': 'Segurança de Plataforma',
  'PR.IR': 'Rede e Infraestrutura',
  // Detect
  'DE.AE': 'Análise de Eventos',
  'DE.CM': 'Monitoramento Contínuo',
  // Respond
  'RS.MA': 'Gestão de Incidentes',
  'RS.MI': 'Mitigação de Incidentes',
  // Recover
  'RC.RP': 'Plano de Recuperação',
};

/** Extrai a categoria de um código de subcategoria: "PR.AA-01" → "PR.AA". */
export function categoryOf(code: string): string {
  const dash = code.lastIndexOf('-');
  return dash > 0 ? code.slice(0, dash) : code;
}

/** Nome amigável da categoria de um controle: "PR.AA-01" → "Identidade e Acesso". Fallback: a própria categoria. */
export function categoryName(code: string): string {
  const cat = categoryOf(code);
  return NIST_CATEGORY_NAMES[cat] ?? cat;
}

/** Rótulo humanizado completo: "PR.AA-01" → "Identidade e Acesso (PR.AA-01)". Sem tradução, devolve só o código. */
export function friendlyControlLabel(code: string): string {
  const name = NIST_CATEGORY_NAMES[categoryOf(code)];
  return name ? `${name} (${code})` : code;
}

/**
 * Código das 6 Funções NIST CSF 2.0. É o vocabulário do dicionário de descrições abaixo — superconjunto
 * do PillarKey de scoring.models (que exclui ID, pois Identify tem tela própria de inventário).
 */
export type NistFunctionCode = 'GV' | 'ID' | 'PR' | 'DE' | 'RS' | 'RC';

/**
 * Subtítulo TÁTICO por Função NIST (as 6) — a FONTE ÚNICA desse texto (DRY). PILLARS (scoring.models)
 * deriva daqui para os painéis de pilar e o Govern; a tela de inventário de ativos (Identify/ID.AM)
 * consome a entrada ID diretamente. Centralizar na camada de humanização das siglas faz uma revisão de
 * redação tocar UM só lugar, em vez de descrições soltas por template.
 */
export const NIST_FUNCTION_DESCRIPTIONS: Record<NistFunctionCode, string> = {
  GV: 'Estabelece a estratégia de gestão de riscos cibernéticos, políticas e responsabilidades. O Aegis conecta a segurança à governança da organização, avaliando ativamente desde privilégios de acesso até a cadeia de suprimentos.',
  ID: 'A base para entender os riscos aos ativos, dados, pessoas e capacidades do negócio. O Aegis rastreia o inventário contínuo e calcula o Raio de Explosão, medindo o impacto real caso peças fundamentais sejam comprometidas.',
  PR: 'Implementação de barreiras para garantir a entrega de serviços críticos e limitar o impacto de eventos cibernéticos. O Aegis mede a força das defesas construídas em identidades, redes e dados para manter invasores isolados.',
  DE: 'Desenvolvimento e implementação de atividades para identificar a ocorrência de um evento de segurança cibernética. O Aegis valida o radar da operação, eliminando pontos cegos e medindo a capacidade de enxergar ameaças reais a tempo.',
  RS: 'Tomada de medidas apropriadas para conter e mitigar os danos de um incidente detectado. O Aegis quantifica a velocidade e a precisão da resposta (MTTA/MTTR) para garantir o controle absoluto sob pressão.',
  RC: 'Planejamento de atividades de resiliência e restauração de serviços afetados. O Aegis garante a capacidade da organização de se reerguer rapidamente após crises, validando a integridade de backups e as metas operacionais (RTO/RPO).',
};
