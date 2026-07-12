import { Component } from '@angular/core';
import { PillarDashboardComponent } from './pillar-dashboard.component';

/**
 * Protect (PR) — wrapper de rota. Toda a orquestração (dados, estado, UI) vive no
 * PillarDashboardComponent; aqui só injetamos a Função NIST. Os 4 pilares seguem este mesmo padrão (DRY).
 */
@Component({
  selector: 'app-protect-dashboard',
  standalone: true,
  imports: [PillarDashboardComponent],
  template: `<app-pillar-dashboard [pillar]="'PR'" />`,
})
export class ProtectDashboardComponent {}
