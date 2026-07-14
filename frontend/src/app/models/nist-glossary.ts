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
