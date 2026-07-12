import { Component } from '@angular/core';
import { PillarDashboardComponent } from './pillar-dashboard.component';

/** Respond (RS) — wrapper de rota; a orquestração vive no PillarDashboardComponent (DRY). */
@Component({
  selector: 'app-respond-dashboard',
  standalone: true,
  imports: [PillarDashboardComponent],
  template: `<app-pillar-dashboard [pillar]="'RS'" />`,
})
export class RespondDashboardComponent {}
