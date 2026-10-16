/**
 * [AEGIS-CROSS-SOURCE-01] Estado entre fontes de um ativo do inventário e as notas congeladas nos planos históricos.
 * O backend é a AUTORIDADE do estado e do texto; aqui não se infere nada.
 */

export type CrossSourceState =
  | 'identified'
  | 'notIdentified'
  | 'insufficientEvidence'
  | 'linkConflict'
  | 'contradictoryEvidence'
  | 'notEvaluated';

export interface CrossSourceNote {
  code: string;
  text: string;
}
