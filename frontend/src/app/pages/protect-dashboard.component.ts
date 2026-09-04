import { Component } from '@angular/core';
import { DevicePostureComponent } from '../components/scoring/device-posture.component';
import { PillarDashboardComponent } from './pillar-dashboard.component';

/**
 * Protect (PR) — wrapper de rota. Toda a orquestração (dados, estado, UI) vive no
 * PillarDashboardComponent; aqui só injetamos a Função NIST. Os 4 pilares seguem este mesmo padrão (DRY).
 *
 * [AEGIS-MVP-MICROSOFT-COVERAGE-02] A seção "Dispositivos gerenciados" (postura de configuração e conformidade
 * de dispositivos) é uma EXTENSÃO específica de Protect, montada ABAIXO do dashboard — sem tocar o componente
 * compartilhado (nenhuma regressão nos demais pilares), no mesmo idioma da "Cobertura de detecção" em Detect.
 * É CONSULTIVA: não altera o AEGIS Score nem a avaliação NIST dos controles exibidos acima.
 */
@Component({
  selector: 'app-protect-dashboard',
  standalone: true,
  imports: [PillarDashboardComponent, DevicePostureComponent],
  template: `
    <app-pillar-dashboard [pillar]="'PR'" />
    <app-device-posture />
  `,
})
export class ProtectDashboardComponent {}
