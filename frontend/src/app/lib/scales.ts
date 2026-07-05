export const C = {
  violet: '#8b5cff',
  violet2: '#a488ff',
  red: '#ff2d6f',
  orange: '#ff7a3d',
  amber: '#ffb020',
  teal: '#26e0ff',
  muted: '#8791a8',
  line: 'rgba(122,145,190,0.14)',
  panel2: '#0f1524',
};

/** Mapa único banda → cor, compartilhado por risco e ICR (rótulos sinônimos apontam para a mesma cor). */
const BAND_COLOR: Record<string, string> = {
  baixo: C.teal,
  controlado: C.teal,
  medio: C.amber,
  moderado: C.amber,
  alto: C.orange,
  critico: C.red,
};

function bandColor(key: string): string {
  return BAND_COLOR[key.toLowerCase()] ?? C.muted;
}

/** Cor por nível de risco (Baixo | Medio | Alto | Critico). */
export function riskColor(level: string): string {
  return bandColor(level);
}

/** Cor da célula do mapa de calor a partir da magnitude (2·prob + impacto, faixa 3–12). */
export function heatColor(probability: number, impact: number): string {
  const v = 2 * probability + impact;
  if (v <= 4) return C.teal;
  if (v <= 7) return C.amber;
  if (v <= 9) return C.orange;
  return C.red;
}

/** Cor por banda do ICR (Controlado | Moderado | Alto | Critico). */
export function icrColor(band: string): string {
  return bandColor(band);
}
