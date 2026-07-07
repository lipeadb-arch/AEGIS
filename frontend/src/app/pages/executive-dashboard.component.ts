import { Component, OnInit, inject, signal } from '@angular/core';
import { ExecutiveDashboard } from '../models/dashboard.models';
import { DashboardService } from '../services/dashboard.service';
import { sampleDashboard } from '../data/sample-dashboard';
import { icrColor } from '../lib/scales';
import { environment } from '../../environments/environment';
import { IcrGaugeComponent } from '../components/icr-gauge.component';
import { RiskHeatmapComponent } from '../components/risk-heatmap.component';
import { GapChartComponent } from '../components/gap-chart.component';
import { RiskLevelsComponent } from '../components/risk-levels.component';
import { ExposureCardComponent } from '../components/exposure-card.component';
import { MaturityGaugeComponent } from '../components/maturity-gauge.component';
import { MaturityBarsComponent, FunctionScore } from '../components/maturity-bars.component';

@Component({
  selector: 'app-executive-dashboard',
  standalone: true,
  imports: [
    IcrGaugeComponent,
    MaturityGaugeComponent,
    MaturityBarsComponent,
    RiskHeatmapComponent,
    GapChartComponent,
    RiskLevelsComponent,
    ExposureCardComponent,
  ],
  template: `
    <div class="app">
      <header class="topbar">
        <div class="brand">
          <span class="mark">Aegis <b>Score</b></span>
          <span class="sub">Auditoria de Maturidade Cibernética · Synapse OS</span>
        </div>
        <div class="client">
          <span class="label">Cliente</span>
          <span class="name">{{ data().clientName }}</span>
          <span class="icr-pill" title="Índice de Criticidade de Risco Cibernético">
            <span class="dot" [style.background]="icrColor(data().icr.band)"></span>
            <span class="v" [style.color]="icrColor(data().icr.band)">
              {{ data().icr.score.toFixed(0) }}
            </span>
            <span class="b">ICR · {{ data().icr.band }}</span>
          </span>
        </div>
      </header>

      @if (!live()) {
        <div class="notice">
          <b>Dados de exemplo.</b>
          @if (loadError()) {
            A postura real não respondeu em <code>{{ apiBase }}</code> — confira o endereço
            da API e o <code>tenantId</code> do cliente. O console traz o erro completo.
          } @else {
            Defina <code>apiBase</code> e <code>tenantId</code> em
            <code>src/environments/environment.ts</code> para carregar a postura real do cliente.
          }
        </div>
      }

      <p class="eyebrow">Exposição do negócio</p>
      <section class="cards">
        <app-exposure-card
          label="Processos críticos expostos"
          [value]="data().exposure.criticalProcessesExposed"
          tone="danger"
        />
        <app-exposure-card
          label="Controles inefetivos"
          [value]="data().exposure.ineffectiveControls"
          tone="warn"
        />
        <app-exposure-card
          label="Planos de ação vencidos"
          [value]="data().exposure.overdueActionPlans"
          tone="danger"
        />
      </section>

      <!-- Painéis principais (Cenário Hollywood): gauge de maturidade + histograma por função. -->
      <div class="grid main">
        <div class="panel">
          <div class="hd">
            <h3>Maturidade Geral</h3>
            <span class="hint">CMMI 1–5 · alvo {{ maturityTarget().toFixed(1) }}</span>
          </div>
          <app-maturity-gauge [value]="overallMaturity()" [max]="maturityTarget()" />
        </div>

        <div class="panel">
          <div class="hd">
            <h3>Maturidade por Função</h3>
            <span class="hint">escala 0–{{ maturityTarget().toFixed(0) }}</span>
          </div>
          <app-maturity-bars [data]="maturityBars()" [max]="maturityTarget()" />
        </div>
      </div>

      <div class="grid trio">
        <div class="panel">
          <div class="hd"><h3>Índice de Criticidade (ICR)</h3></div>
          <app-icr-gauge [icr]="data().icr" />
          <div class="hd" style="margin-top:18px">
            <h3 style="font-size:13px;color:var(--muted)">Riscos por nível</h3>
          </div>
          <app-risk-levels [data]="data().riskByLevel" />
        </div>

        <div class="panel">
          <div class="hd">
            <h3>Maiores gaps por categoria</h3>
            <span class="hint">distância até o alvo</span>
          </div>
          <app-gap-chart [data]="data().topGaps" />
        </div>

        <div class="panel">
          <div class="hd"><h3>Matriz de risco</h3></div>
          <app-risk-heatmap [data]="data().riskHeatmap" />
        </div>
      </div>
    </div>
  `,
})
export class ExecutiveDashboardComponent implements OnInit {
  private readonly svc = inject(DashboardService);

  data = signal<ExecutiveDashboard>(sampleDashboard);
  live = signal(false);
  loadError = signal(false);

  // ---- Mock visual (Cenário Hollywood) — Signals de apresentação ----
  /** Maturidade geral apontada pelo gauge (escala CMMI 0 → alvo). */
  overallMaturity = signal(3.4);
  maturityTarget = signal(4);
  /** Maturidade por Função NIST — ordem e valores mockados (escala 0–4). */
  maturityBars = signal<FunctionScore[]>([
    { code: 'DE', label: 'Detect', value: 1.0 },
    { code: 'GV', label: 'Govern', value: 1.1 },
    { code: 'ID', label: 'Identify', value: 1.0 },
    { code: 'PR', label: 'Protect', value: 1.0 },
    { code: 'RC', label: 'Recover', value: 1.0 },
    { code: 'RS', label: 'Respond', value: 1.0 },
  ]);

  // Exposto ao template para colorir a pílula do ICR.
  protected readonly icrColor = icrColor;
  // Exposto ao template para orientar o diagnóstico quando a carga falha.
  protected readonly apiBase = environment.apiBase;

  ngOnInit(): void {
    this.svc.fetchExecutive().subscribe({
      next: (d) => {
        this.data.set(d);
        this.live.set(true);
        this.loadError.set(false);
      },
      error: (err) => {
        // Mantém os dados de exemplo, mas registra a falha e sinaliza um aviso distinto.
        console.error('Falha ao carregar o dashboard executivo:', err);
        this.loadError.set(true);
      },
    });
  }
}
