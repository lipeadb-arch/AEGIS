import { Component } from '@angular/core';
import { PillarDashboardComponent } from './pillar-dashboard.component';

/** Detect (DE) — wrapper de rota; a orquestração vive no PillarDashboardComponent (DRY). */
@Component({
  selector: 'app-detect-dashboard',
  standalone: true,
  imports: [PillarDashboardComponent],
  template: `<app-pillar-dashboard [pillar]="'DE'" />`,
})
export class DetectDashboardComponent {}
