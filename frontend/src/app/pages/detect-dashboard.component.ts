import { Component } from '@angular/core';
import { DetectionCoverageComponent } from '../components/scoring/detection-coverage.component';
import { PillarDashboardComponent } from './pillar-dashboard.component';

/**
 * Detect (DE) — wrapper de rota. A matriz de controles NIST vive no PillarDashboardComponent compartilhado (DRY);
 * [AEGIS-MVP-GOOGLE-SECOPS-02] a seção "Cobertura de detecção" (regras do SIEM × MITRE) é uma EXTENSÃO específica
 * de Detect, montada ABAIXO do dashboard — sem tocar o componente compartilhado (nenhuma regressão nos demais pilares).
 */
@Component({
  selector: 'app-detect-dashboard',
  standalone: true,
  imports: [PillarDashboardComponent, DetectionCoverageComponent],
  template: `
    <app-pillar-dashboard [pillar]="'DE'" />
    <app-detection-coverage />
  `,
})
export class DetectDashboardComponent {}
