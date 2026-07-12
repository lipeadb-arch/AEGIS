import { Component } from '@angular/core';
import { PillarDashboardComponent } from './pillar-dashboard.component';

/** Recover (RC) — wrapper de rota; a orquestração vive no PillarDashboardComponent (DRY). */
@Component({
  selector: 'app-recover-dashboard',
  standalone: true,
  imports: [PillarDashboardComponent],
  template: `<app-pillar-dashboard [pillar]="'RC'" />`,
})
export class RecoverDashboardComponent {}
