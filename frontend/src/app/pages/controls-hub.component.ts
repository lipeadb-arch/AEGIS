import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PILLARS, PillarKey } from '../models/scoring.models';
import { FunctionPosture, WorkspacePosture, postureLabel } from '../models/workspace.models';
import { AegisScoreService } from '../services/aegis-score.service';

/**
 * [AEGIS-MVP-PRODUCT-01] GOVERNANÇA E CONTROLES — a porta ÚNICA das seis Funções NIST CSF 2.0.
 *
 * O menu principal mantinha seis entradas permanentes (GV/ID/PR/DE/RS/RC) competindo com as páginas
 * operacionais; a navegação por Função passa a ser INTERNA a esta tela. Nada foi duplicado: cada cartão leva
 * à MESMA rota que já existia, e todas continuam acessíveis por link direto.
 *
 * A tela não recalcula nada. Consome a projeção ÚNICA do workspace (/scoring/workspace) — a mesma autoridade
 * do Dashboard e das próprias telas de Função — e mostra, por Função, o score (nulo = "Não avaliado", NUNCA
 * 0%) e a cobertura, que são eixos DISTINTOS. Sem seletor de outro framework: só existe avaliação para o
 * catálogo ativo, e oferecer CIS/ISO aqui sugeriria avaliações que não existem.
 */
@Component({
  selector: 'app-controls-hub',
  standalone: true,
  imports: [RouterLink],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <h1>Governança e controles</h1>
          <p class="sub">
            As seis funções do NIST CSF 2.0 e a evidência que sustenta cada uma. <strong>Score e cobertura são
            eixos distintos</strong>: 100% de cobertura pode conter controles não conformes, e cobertura zero
            significa "ainda não avaliado" — nunca reprovação.
          </p>
        </div>
        <a class="ghost" routerLink="/governance">Evidências e documentos</a>
      </header>

      @if (loading()) {
        <div class="panel"><p class="muted">Carregando a postura por função…</p></div>
      } @else if (error()) {
        <div class="panel">
          <p class="muted">Não foi possível carregar a postura por função agora.</p>
          <button type="button" class="ghost" (click)="reload()">Tentar novamente</button>
        </div>
      } @else {
        <div class="fn-grid">
          @for (f of functions(); track f.code) {
            <a class="fn" [routerLink]="f.route" [class.is-na]="f.posture === null || f.posture.evaluationState === 'NotEvaluated'">
              <span class="fn-code">{{ f.code }}</span>
              <span class="fn-name">{{ f.label }}</span>
              <span class="fn-score">{{ f.scoreText }}</span>
              <span class="fn-meta">{{ f.metaText }}</span>
              <span class="fn-blurb">{{ f.blurb }}</span>
            </a>
          }
        </div>
      }
    </section>
  `,
  styles: [
    `
      .page {
        /* Folga inferior: reserva o canto do FAB flutuante do Auditor (ver a Visão geral). */
        padding: 20px 26px 104px;
        max-width: 1320px;
      }
      .page-head {
        display: flex;
        flex-wrap: wrap;
        gap: 14px;
        align-items: flex-start;
        justify-content: space-between;
        margin-bottom: 22px;
      }
      .page-head h1 {
        margin: 0 0 6px;
        font-family: var(--display);
        font-size: 22px;
        font-weight: 700;
        color: var(--text);
      }
      .sub {
        margin: 0;
        max-width: 74ch;
        font-family: var(--sans);
        font-size: 13px;
        line-height: 1.6;
        color: var(--muted);
      }
      .sub strong {
        color: var(--text);
        font-weight: 600;
      }
      .ghost {
        flex: none;
        font-family: var(--mono);
        font-size: 11.5px;
        text-decoration: none;
        padding: 8px 14px;
        border-radius: 8px;
        border: 1px solid var(--line);
        background: rgba(122, 145, 190, 0.06);
        color: var(--text);
        cursor: pointer;
      }
      .ghost:hover {
        border-color: color-mix(in srgb, var(--cyan) 40%, var(--line));
      }
      .panel {
        border: 1px solid var(--line);
        border-radius: 12px;
        background: var(--panel);
        padding: 18px;
      }
      .muted {
        margin: 0 0 10px;
        font-family: var(--sans);
        font-size: 13px;
        color: var(--muted);
      }

      /* 3 colunas em 1366px, 2 em telas médias, 1 no celular — sem rolagem lateral em nenhuma. */
      .fn-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
        gap: 14px;
      }
      .fn {
        display: grid;
        grid-template-columns: auto 1fr;
        grid-template-areas:
          'code name'
          'score score'
          'meta meta'
          'blurb blurb';
        gap: 4px 10px;
        align-items: baseline;
        padding: 16px 18px;
        border: 1px solid var(--line);
        border-radius: 12px;
        background: var(--panel);
        text-decoration: none;
        transition: 0.15s;
        min-width: 0;
      }
      .fn:hover {
        border-color: color-mix(in srgb, var(--cyan) 35%, var(--line));
      }
      .fn-code {
        grid-area: code;
        font-family: var(--mono);
        font-size: 10px;
        letter-spacing: 0.14em;
        color: var(--cyan);
      }
      .fn-name {
        grid-area: name;
        font-family: var(--sans);
        font-size: 14px;
        font-weight: 600;
        color: var(--text);
      }
      .fn-score {
        grid-area: score;
        margin-top: 6px;
        font-family: var(--display);
        font-weight: 700;
        font-size: 24px;
        color: var(--text);
      }
      .fn.is-na .fn-score {
        font-size: 16px;
        color: var(--muted);
      }
      .fn-meta {
        grid-area: meta;
        font-family: var(--mono);
        font-size: 10.5px;
        color: var(--muted);
      }
      .fn-blurb {
        grid-area: blurb;
        margin-top: 8px;
        padding-top: 8px;
        border-top: 1px solid var(--line-2);
        font-family: var(--sans);
        font-size: 12px;
        line-height: 1.5;
        color: var(--muted);
      }
      @media (max-width: 720px) {
        .page {
          padding: 16px 14px 48px;
        }
      }
    `,
  ],
})
export class ControlsHubComponent implements OnInit {
  private readonly scoreSvc = inject(AegisScoreService);

  readonly workspace = signal<WorkspacePosture | null>(null);
  readonly loading = signal(true);
  readonly error = signal(false);

  /**
   * Ordem do NIST CSF 2.0 (GV primeiro) e a rota que JÁ existia para cada Função — a navegação interna
   * substitui as seis entradas de menu sem criar tela nova nem estado paralelo.
   */
  private static readonly ROUTES: Record<PillarKey, string> = {
    GV: '/governance',
    ID: '/assets',
    PR: '/protect',
    DE: '/detect',
    RS: '/respond',
    RC: '/recover',
  };

  private static readonly ORDER: PillarKey[] = ['GV', 'ID', 'PR', 'DE', 'RS', 'RC'];

  readonly functions = computed(() => {
    const w = this.workspace();
    return ControlsHubComponent.ORDER.map((code) => {
      const meta = PILLARS[code];
      const posture: FunctionPosture | null = w?.functions.find((f) => f.code === code) ?? null;
      return {
        code,
        label: meta.label,
        blurb: meta.blurb,
        route: ControlsHubComponent.ROUTES[code],
        posture,
        // "Não avaliado" NUNCA vira 0% — a régua é a mesma do resto do produto.
        scoreText: posture ? postureLabel(posture.percentage) : 'Não avaliado',
        metaText: posture
          ? `cobertura ${posture.coveragePercentage.toFixed(1)}% · ${posture.evaluatedControls}/${posture.eligibleControls} controles`
          : 'sem catálogo ativo para esta função',
      };
    });
  });

  ngOnInit(): void {
    this.load();
  }

  reload(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.workspace.set(null);

    this.scoreSvc.fetchWorkspace().subscribe({
      next: (w) => {
        this.workspace.set(w);
        this.loading.set(false);
      },
      error: (err) => {
        console.warn('Postura por função indisponível:', err);
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}
