export const C = {
  violet: '#7c5cff',
  violet2: '#9f86ff',
  red: '#ff4d6d',
  orange: '#ff8a4d',
  amber: '#f5a623',
  teal: '#2dd4bf',
  muted: '#9a9ab4',
  line: '#262636',
  panel2: '#1a1a28',
};

export function riskColor(level: string): string {
  switch (level.toLowerCase()) {
    case 'baixo':
      return C.teal;
    case 'medio':
      return C.amber;
    case 'alto':
      return C.orange;
    case 'critico':
      return C.red;
    default:
      return C.muted;
  }
}

/** Cor da célula do mapa de calor a partir da magnitude (2·prob + impacto, faixa 3–12). */
export function heatColor(probability: number, impact: number): string {
  const v = 2 * probability + impact;
  if (v <= 4) return C.teal;
  if (v <= 7) return C.amber;
  if (v <= 9) return C.orange;
  return C.red;
}

export function icrColor(band: string): string {
  switch (band.toLowerCase()) {
    case 'controlado':
      return C.teal;
    case 'moderado':
      return C.amber;
    case 'alto':
      return C.orange;
    case 'critico':
      return C.red;
    default:
      return C.muted;
  }
}
