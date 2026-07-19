import { Component, computed, effect, inject, input, signal } from '@angular/core';
import {
  PILLARS,
  PillarGapAnalysis,
  PillarKey,
  TenantControlStateDto,
  buildPillarGapAnalysis,
} from '../../models/scoring.models';
import { categoryName } from '../../models/nist-glossary';
import { ScoringService } from '../../services/scoring.service';
import { MissingRequirementsComponent } from './missing-requirements.component';

/**
 * AegisPillarChecklistComponent — os PONTOS CEGOS de uma Função NIST: os controles que o Aegis não
 * consegue avaliar por falta de prova, com a ação correspondente (ligar conector × subir documento).
 *
 * Só os cegos, de propósito. A lista dos controles MONITORADOS já é o card de compliance do painel —
 * repeti-la aqui numa aba interna criaria aba dentro de aba e duas listas do mesmo dado. Este componente
 * responde a UMA pergunta: onde o Aegis está sem visão?
 *
 * A partição vem de `buildPillarGapAnalysis` (função pura) e usa a EXISTÊNCIA de lacunas, não o status:
 * um controle reprovado com telemetria presente NÃO é cego — o Aegis o enxerga, ele é que está mal.
 */
@Component({
  selector: 'app-aegis-pillar-checklist',
  standalone: true,
  imports: [MissingRequirementsComponent],
  template: `
    <section class="chk-panel" [class.bare]="!heading()">
      @if (heading(); as title) {
        <header class="chk-hd">
          <div>
            <span class="chk-eyebrow">PONTOS CEGOS · {{ meta().code }}</span>
            <h3>{{ title }}</h3>
          </div>
          @if (!loading() && !error()) {
            <div class="chk-tally" role="status">
              <span class="tally"><b>{{ gap().covered.length }}</b> monitorados</span>
              <span class="sep">·</span>
              <span class="tally blind"><b>{{ gap().blindSpots.length }}</b> cegos</span>
            </div>
          }
        </header>
      }

      @if (loading()) {
        <div class="chk-state" aria-live="polite">
          <span class="scan" aria-hidden="true"></span>
          <span class="pulse">Varrendo cobertura de evidência…</span>
        </div>
      } @else if (error()) {
        <div class="chk-state err" role="alert">
          <p>{{ error() }}</p>
          <button type="button" (click)="load()">Tentar novamente</button>
        </div>
      } @else {
        @if (gap().blindSpots.length > 0) {
          <p class="blind-lede">
            O Aegis não consegue avaliar
            <b>{{ gap().blindSpots.length }}</b>
            {{ gap().blindSpots.length === 1 ? 'controle' : 'controles' }} por falta de prova —
            <b>{{ gap().telemetryGaps }}</b> de telemetria e
            <b>{{ gap().documentationGaps }}</b> de documentação.
          </p>
        }

        <ul class="rows">
          @for (c of gap().blindSpots; track c.code) {
            <li class="row">
              <div class="blind-hd">
                <span class="names">
                  <span class="name">{{ categoryName(c.code) }}</span>
                  <span class="code">{{ c.code }}</span>
                </span>
                <span class="pts">{{ c.scorePoints }}<i>/{{ c.maxScorePoints }}</i></span>
              </div>
              <app-missing-requirements [groups]="c.missingGroups" />
            </li>
          } @empty {
            <li class="empty ok-empty">
              Nenhum ponto cego — toda não-conformidade aqui tem evidência por trás.
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      /* Com cabeçalho, é um painel próprio (Govern). Sem, é o conteúdo de uma aba — nada de moldura
         dentro de moldura. */
      .chk-panel {
        border: 1px solid var(--line);
        border-radius: 12px;
        background: rgba(122, 145, 190, 0.03);
        padding: 15px 16px 16px;
      }
      .chk-panel.bare {
        border: 0;
        border-radius: 0;
        background: none;
        padding: 0;
      }
      .chk-hd {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 14px;
        flex-wrap: wrap;
        margin-bottom: 13px;
      }
      .chk-eyebrow {
        display: block;
        font-family: var(--mono);
        font-size: 9.5px;
        letter-spacing: 0.16em;
        color: var(--muted);
      }
      .chk-hd h3 {
        margin: 3px 0 0;
        font-family: var(--display);
        font-size: 16px;
        font-weight: 600;
        color: var(--text);
      }
      .chk-tally {
        display: flex;
        align-items: baseline;
        gap: 7px;
        font-family: var(--mono);
        font-size: 11px;
        color: var(--muted);
      }
      .chk-tally b {
        font-family: var(--display);
        font-size: 14px;
        color: var(--cyan);
      }
      .chk-tally .blind b {
        color: var(--red);
      }
      .chk-tally .sep {
        opacity: 0.5;
      }

      .blind-lede {
        margin: 0 0 11px;
        font-family: var(--sans);
        font-size: 12.5px;
        line-height: 1.55;
        color: var(--text);
        opacity: 0.9;
      }
      .blind-lede b {
        color: var(--red);
        font-weight: 600;
      }

      .rows {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .row {
        border: 1px solid var(--line);
        border-left: 3px solid var(--amber);
        border-radius: 9px;
        padding: 10px 13px 12px;
        background: rgba(122, 145, 190, 0.02);
      }
      .blind-hd {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
      }
      .names {
        display: flex;
        flex-direction: column;
        gap: 1px;
        min-width: 0;
      }
      .name {
        font-family: var(--sans);
        font-size: 12.5px;
        color: var(--text);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .code {
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.03em;
        color: var(--muted);
      }
      .pts {
        font-family: var(--display);
        font-weight: 600;
        font-size: 12.5px;
        color: var(--muted);
      }
      .pts i {
        font-style: normal;
        opacity: 0.6;
        font-size: 10.5px;
      }

      .chk-state {
        display: flex;
        align-items: center;
        gap: 11px;
        padding: 18px 2px;
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
      }
      .chk-state .scan {
        width: 13px;
        height: 13px;
        border-radius: 50%;
        border: 2px solid rgba(38, 224, 255, 0.25);
        border-top-color: var(--cyan);
        animation: chk-spin 0.75s linear infinite;
      }
      .chk-state .pulse {
        animation: chk-pulse 1.5s ease-in-out infinite;
      }
      .chk-state.err {
        flex-direction: column;
        align-items: flex-start;
        gap: 9px;
        color: var(--red);
      }
      .chk-state.err p {
        margin: 0;
      }
      .chk-state.err button {
        font-family: var(--mono);
        font-size: 11px;
        color: var(--cyan);
        background: none;
        border: 1px solid rgba(38, 224, 255, 0.35);
        border-radius: 7px;
        padding: 6px 12px;
        cursor: pointer;
      }
      .chk-state.err button:hover {
        border-color: var(--cyan);
      }

      .empty {
        font-family: var(--mono);
        font-size: 12px;
        color: var(--muted);
        padding: 14px 4px;
      }
      .empty.ok-empty {
        color: var(--cyan);
        opacity: 0.8;
      }

      @keyframes chk-spin {
        to {
          transform: rotate(360deg);
        }
      }
      @keyframes chk-pulse {
        0%,
        100% {
          opacity: 0.45;
        }
        50% {
          opacity: 0.95;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .chk-state .scan,
        .chk-state .pulse {
          animation: none;
        }
      }
    `,
  ],
})
export class AegisPillarChecklistComponent {
  /** Função NIST a auditar ('GV', 'PR', 'DE', 'RS', 'RC'). */
  readonly pillar = input.required<PillarKey>();

  /**
   * Título do painel. Vazio (padrão) quando o componente é o conteúdo de uma ABA que já tem rótulo —
   * caso do painel de pilar, onde um cabeçalho próprio seria moldura dentro de moldura.
   */
  readonly heading = input<string>('');

  private readonly svc = inject(ScoringService);

  private readonly controls = signal<TenantControlStateDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly meta = computed(() => PILLARS[this.pillar()]);
  readonly gap = computed<PillarGapAnalysis>(() => buildPillarGapAnalysis(this.meta(), this.controls()));

  protected readonly categoryName = categoryName;

  constructor() {
    // O pilar é input: trocá-lo tem de recarregar a matriz, senão a tela mostraria o pilar anterior
    // sob o rótulo novo. O effect cobre a carga inicial e as trocas.
    effect(() => {
      this.pillar();
      this.load();
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.svc.getPillarControls(this.pillar()).subscribe({
      next: (rows) => {
        this.controls.set(rows);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }
}
