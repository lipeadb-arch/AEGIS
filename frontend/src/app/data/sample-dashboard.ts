import { ExecutiveDashboard } from '../models/dashboard.models';

/**
 * Dados representativos para o dashboard renderizar antes de o backend estar conectado.
 * Os valores ecoam os prints de onboarding (Secure Score 53.77%, exposição do Defender, etc.).
 */
export const sampleDashboard: ExecutiveDashboard = {
  clientName: 'Cliente demonstração',
  generatedAt: new Date().toISOString(),
  exposure: {
    criticalProcessesExposed: 3,
    ineffectiveControls: 27,
    overdueActionPlans: 5,
    overallMaturity: 2.4,
    targetMaturity: 4.0,
  },
  maturityByFunction: [
    { function: 'GV', functionName: 'GOVERN (GV)', current: 2.6, target: 4 },
    { function: 'ID', functionName: 'IDENTIFY (ID)', current: 2.9, target: 4 },
    { function: 'PR', functionName: 'PROTECT (PR)', current: 3.1, target: 4 },
    { function: 'DE', functionName: 'DETECT (DE)', current: 2.2, target: 4 },
    { function: 'RS', functionName: 'RESPOND (RS)', current: 2.0, target: 4 },
    { function: 'RC', functionName: 'RECOVER (RC)', current: 1.7, target: 4 },
  ],
  topGaps: [
    { code: 'RC.RP', name: 'RC.RP', current: 1.5, target: 4, gap: 2.5 },
    { code: 'RS.MA', name: 'RS.MA', current: 1.8, target: 4, gap: 2.2 },
    { code: 'DE.AE', name: 'DE.AE', current: 2.0, target: 4, gap: 2.0 },
    { code: 'GV.SC', name: 'GV.SC', current: 2.1, target: 4, gap: 1.9 },
    { code: 'PR.DS', name: 'PR.DS', current: 2.3, target: 4, gap: 1.7 },
    { code: 'ID.RA', name: 'ID.RA', current: 2.4, target: 4, gap: 1.6 },
    { code: 'DE.CM', name: 'DE.CM', current: 2.5, target: 4, gap: 1.5 },
    { code: 'PR.AA', name: 'PR.AA', current: 2.7, target: 4, gap: 1.3 },
  ],
  riskHeatmap: [
    { probability: 4, impact: 4, count: 2 },
    { probability: 3, impact: 4, count: 1 },
    { probability: 3, impact: 3, count: 3 },
    { probability: 2, impact: 3, count: 4 },
    { probability: 2, impact: 2, count: 5 },
    { probability: 1, impact: 3, count: 1 },
    { probability: 1, impact: 1, count: 3 },
  ],
  riskByLevel: [
    { level: 'Baixo', count: 6 },
    { level: 'Medio', count: 8 },
    { level: 'Alto', count: 4 },
    { level: 'Critico', count: 2 },
  ],
  icr: { score: 63, band: 'Alto' },
};
