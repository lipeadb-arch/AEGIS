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
          <p class="page-eyebrow">NIST CSF 2.0</p>
          <h1>Governança e controles</h1>
          <p class="page-desc">
            As seis funções do NIST CSF 2.0 e a evidência que sustenta cada uma. <strong>Score e cobertura são
            eixos distintos</strong>: 100% de cobertura pode conter controles não conformes, e cobertura zero
            significa "ainda não avaliado" — nunca reprovação.
          </p>
        </div>
        <div class="page-actions">
          <a class="ghost" routerLink="/governance">Evidências e documentos</a>
        </div>
      </header>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a postura por função…</p></div></div>
      } @else if (error()) {
        <div class="panel">
          <div class="state error" role="alert">
            <p class="err">Não foi possível carregar a postura por função agora.</p>
            <button type="button" class="ghost" (click)="reload()">Tentar novamente</button>
          </div>
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
      /* Página, cabeçalho, painéis, botões e estados: sistema visual global (styles.css). */

      /* Seis Funções: 3 colunas em 1366 px, 2 em telas médias, 1 no celular — sem rolagem lateral. */
      .fn-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 300px), 1fr));
        gap: var(--sp-4);
      }
      .fn {
        position: relative;
        display: grid;
        grid-template-columns: auto 1fr;
        grid-template-areas:
          'code name'
          'score score'
          'meta meta'
          'blurb blurb';
        gap: 6px 10px;
        align-items: center;
        min-width: 0;
        padding: var(--sp-5);
        border: 1px solid var(--line);
        border-radius: var(--radius-lg);
        background: var(--panel);
        box-shadow: var(--shadow-panel);
        color: inherit;
        text-decoration: none;
        overflow: hidden;
        transition: border-color var(--ease), background var(--ease);
      }
      /* Filete neon = há avaliação; neutro = ainda não avaliado (nunca "reprovado"). */
      .fn::before {
        content: '';
        position: absolute;
        inset: 0 0 auto;
        height: 2px;
        background: var(--neon-h);
        opacity: 0.75;
      }
      .fn.is-na::before {
        background: var(--line-strong);
        opacity: 1;
      }
      .fn:hover {
        border-color: rgba(38, 224, 255, 0.4);
        background: linear-gradient(180deg, rgba(38, 224, 255, 0.05), transparent 90px), var(--panel);
      }
      .fn:focus-visible {
        outline: none;
        box-shadow: var(--focus);
      }
      .fn-code {
        grid-area: code;
        padding: 2px var(--sp-2);
        border-radius: 6px;
        background: var(--tint-cyan);
        font-family: var(--mono);
        font-size: var(--fs-meta);
        font-weight: 600;
        color: var(--cyan);
      }
      .fn-name {
        grid-area: name;
        font-size: var(--fs-panel);
        font-weight: 600;
      }
      .fn-score {
        grid-area: score;
        margin-top: var(--sp-2);
        font-size: var(--fs-value);
        font-weight: 700;
        line-height: 1.1;
        letter-spacing: -0.02em;
      }
      .fn.is-na .fn-score {
        font-size: 20px;
        font-weight: 600;
        letter-spacing: 0;
        color: var(--text-2);
      }
      .fn-meta {
        grid-area: meta;
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
      .fn-blurb {
        grid-area: blurb;
        margin-top: var(--sp-2);
        padding-top: 10px;
        border-top: 1px solid var(--line-2);
        font-size: var(--fs-sm);
        line-height: var(--lh);
        color: var(--muted);
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
