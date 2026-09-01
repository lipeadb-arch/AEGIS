import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DetectionCoverageService } from '../../services/detection-coverage.service';
import {
  DetectionCoverageTechnique,
  DetectionCoverageView,
} from '../../models/detection-coverage.models';

/**
 * [AEGIS-MVP-GOOGLE-SECOPS-02] Seção "Cobertura de detecção" da área Detect — mostra como as regras do SIEM
 * estão mapeadas ao MITRE ATT&CK. CONSULTIVA e DETERMINÍSTICA: todos os títulos/rótulos são fixos (não dependem
 * de IA); o backend entrega os nomes claros e as táticas em pt-BR. Não altera nem exibe o AEGIS Score.
 *
 * Estados explícitos: sem integração · nunca sincronizado · carregando · disponível · parcial/degradado · erro ·
 * snapshot anterior preservado após falha · zero regras em coleta completa. Nunca exibe nome/texto de regra.
 */
@Component({
  selector: 'app-detection-coverage',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="dc">
      <header class="dc-head">
        <p class="eyebrow">SIEM · MITRE ATT&CK</p>
        <h2>Cobertura de detecção</h2>
        <p class="lede">
          Mostra como as regras do SIEM estão mapeadas ao MITRE ATT&amp;CK. Regras configuradas ajudam a
          enxergar a capacidade de detecção, mas não comprovam eficácia e não alteram o AEGIS Score.
        </p>
      </header>

      @switch (uiState()) {
        @case ('loading') {
          <div class="panel state"><span class="pulse">Carregando a cobertura de detecção…</span></div>
        }
        @case ('error') {
          <div class="panel state err">
            <b>Não foi possível carregar a cobertura de detecção.</b>
            <span>{{ error() }}</span>
            <button type="button" class="retry" (click)="load()">Tentar novamente</button>
          </div>
        }
        @case ('notConfigured') {
          <div class="panel state muted">
            <b>Nenhum SIEM conectado</b>
            <span>Conecte um SIEM (ex.: Google SecOps) para enxergar a cobertura de detecção por técnica MITRE.</span>
          </div>
        }
        @case ('neverSynced') {
          <div class="panel state muted">
            <b>Ainda não sincronizado</b>
            <span>O conector de SIEM existe, mas as regras ainda não foram coletadas. Rode uma sincronização.</span>
          </div>
        }
        @case ('unavailableEmpty') {
          <div class="panel state warn">
            <b>Coleta de regras indisponível</b>
            <span>A última tentativa de leitura das regras falhou. Verifique a permissão <code>chronicle.rules.list</code> e tente sincronizar de novo.</span>
          </div>
        }
        @case ('data') {
          <!-- Aviso de NÃO pontuação — sempre visível junto dos números. -->
          <p class="banner">{{ view()!.scoreDisclaimer }}</p>

          @if (view()!.state === 'Unavailable') {
            <div class="notice warn">
              Mostrando o último inventário coletado — a tentativa mais recente falhou. Os números podem estar defasados.
            </div>
          } @else if (view()!.state === 'Partial') {
            <div class="notice warn">
              Coleta parcial: os totais são um piso (a leitura das regras foi truncada). Podem existir mais regras.
            </div>
          }

          <!-- Resumo em linguagem clara. -->
          <div class="summary">
            <div class="chip"><span class="n">{{ view()!.summary.activeRules }}</span><span class="l">Regras ativas</span></div>
            <div class="chip ok"><span class="n">{{ view()!.summary.rulesWithMitre }}</span><span class="l">Com técnica MITRE</span></div>
            <div class="chip"><span class="n">{{ view()!.summary.rulesInLiveMode }}</span><span class="l">Em execução</span></div>
            <div class="chip"><span class="n">{{ view()!.summary.rulesWithAlerting }}</span><span class="l">Gerando alertas</span></div>
            <div class="chip" [class.warn]="view()!.summary.rulesWithoutMitre > 0">
              <span class="n">{{ view()!.summary.rulesWithoutMitre }}</span><span class="l">Sem mapeamento MITRE</span>
            </div>
          </div>

          @if (view()!.techniques.length === 0) {
            <div class="panel state muted">
              @if (view()!.summary.activeRules === 0) {
                <b>Nenhuma regra ativa</b>
                <span>A coleta foi concluída, mas o SIEM não tem regras ativas configuradas.</span>
              } @else {
                <b>Nenhuma técnica MITRE mapeada</b>
                <span>Há regras ativas, mas nenhuma declara uma técnica MITRE válida. Adicione o metadado <code>technique</code> às regras.</span>
              }
            </div>
          } @else {
            <ul class="techs" role="list">
              @for (t of visibleTechniques(); track t.techniqueId) {
                <li class="tech" [class.attn]="t.needsAttention">
                  <div class="tech-main">
                    <span class="tname">{{ t.name }}</span>
                    <code class="tcode">{{ t.techniqueId }}</code>
                    @for (tac of t.tactics; track tac.id) {
                      <span class="tactic">{{ tac.name }}</span>
                    }
                  </div>
                  <div class="tech-meta">
                    <span class="count" title="Regras configuradas">{{ t.ruleCount }} config.</span>
                    <span class="count" title="Regras em execução">{{ t.liveRuleCount }} em execução</span>
                    <span class="count" title="Regras gerando alertas">{{ t.alertingRuleCount }} c/ alerta</span>
                    <span class="status" [class.attn]="t.needsAttention">{{ t.statusLabel }}</span>
                  </div>
                </li>
              }
            </ul>

            @if (view()!.techniques.length > visibleTechniques().length) {
              <button type="button" class="more" (click)="showAll.set(true)">
                Mostrar todas as {{ view()!.techniques.length }} técnicas
              </button>
            }
          }

          <footer class="dc-foot">
            <span>Fonte: {{ view()!.source }}</span>
            <span>{{ view()!.attackLabel }}</span>
            @if (view()!.lastCollectionAt) {
              <span>Última coleta: {{ view()!.lastCollectionAt | date: 'short' }}</span>
            }
          </footer>
        }
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 0 32px 40px;
      }
      .dc {
        border: 1px solid var(--line, #26304a);
        border-radius: 14px;
        padding: 22px 22px 18px;
        background: rgba(122, 145, 190, 0.03);
      }
      .eyebrow {
        font-family: var(--mono, monospace);
        font-size: 10px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--cyan, #26e0ff);
        margin: 0 0 4px;
      }
      .dc-head h2 {
        font-family: var(--sans, sans-serif);
        font-size: 19px;
        color: var(--text, #e6ecf5);
        margin: 0 0 6px;
      }
      .lede {
        color: var(--muted, #8a97ad);
        font-family: var(--sans, sans-serif);
        font-size: 13px;
        line-height: 1.6;
        margin: 0;
        max-width: 780px;
      }
      .banner {
        margin: 16px 0 12px;
        padding: 9px 13px;
        border: 1px solid rgba(38, 224, 255, 0.28);
        border-left: 3px solid var(--cyan, #26e0ff);
        border-radius: 8px;
        background: rgba(38, 224, 255, 0.05);
        font-family: var(--mono, monospace);
        font-size: 11.5px;
        color: var(--text, #e6ecf5);
      }
      .notice {
        margin: 0 0 12px;
        padding: 8px 12px;
        border-radius: 8px;
        font-family: var(--mono, monospace);
        font-size: 11.5px;
      }
      .notice.warn {
        border: 1px solid rgba(255, 176, 32, 0.4);
        background: rgba(255, 176, 32, 0.06);
        color: var(--amber, #ffb020);
      }
      .summary {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        margin: 4px 0 16px;
      }
      .chip {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 120px;
        padding: 10px 13px;
        border: 1px solid var(--line, #26304a);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.04);
      }
      .chip .n {
        font-family: var(--display, sans-serif);
        font-weight: 700;
        font-size: 22px;
        color: var(--text, #e6ecf5);
      }
      .chip .l {
        font-family: var(--mono, monospace);
        font-size: 9.5px;
        text-transform: uppercase;
        letter-spacing: 0.1em;
        color: var(--muted, #8a97ad);
      }
      .chip.ok .n {
        color: var(--cyan, #26e0ff);
      }
      .chip.warn {
        border-color: rgba(255, 176, 32, 0.4);
        background: rgba(255, 176, 32, 0.05);
      }
      .chip.warn .n {
        color: var(--amber, #ffb020);
      }
      .techs {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .tech {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        justify-content: space-between;
        gap: 8px 14px;
        padding: 11px 13px;
        border: 1px solid var(--line, #26304a);
        border-left: 3px solid var(--line, #26304a);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.02);
      }
      .tech.attn {
        border-left-color: var(--amber, #ffb020);
      }
      .tech-main {
        display: flex;
        align-items: baseline;
        flex-wrap: wrap;
        gap: 8px;
      }
      .tname {
        font-family: var(--sans, sans-serif);
        font-size: 14px;
        font-weight: 600;
        color: var(--text, #e6ecf5);
      }
      .tcode {
        font-family: var(--mono, monospace);
        font-size: 11px;
        color: var(--muted, #8a97ad);
        background: rgba(255, 255, 255, 0.05);
        padding: 1px 6px;
        border-radius: 5px;
      }
      .tactic {
        font-family: var(--mono, monospace);
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.06em;
        color: var(--cyan, #26e0ff);
        border: 1px solid rgba(38, 224, 255, 0.28);
        border-radius: 999px;
        padding: 1px 8px;
      }
      .tech-meta {
        display: flex;
        align-items: center;
        flex-wrap: wrap;
        gap: 12px;
      }
      .count {
        font-family: var(--mono, monospace);
        font-size: 11px;
        color: var(--muted, #8a97ad);
      }
      .status {
        font-family: var(--mono, monospace);
        font-size: 10.5px;
        color: var(--cyan, #26e0ff);
      }
      .status.attn {
        color: var(--amber, #ffb020);
      }
      .more {
        margin: 12px 0 0;
        cursor: pointer;
        font-family: var(--mono, monospace);
        font-size: 11px;
        letter-spacing: 0.04em;
        color: var(--cyan, #26e0ff);
        background: rgba(38, 224, 255, 0.06);
        border: 1px solid rgba(38, 224, 255, 0.35);
        border-radius: 8px;
        padding: 7px 14px;
      }
      .more:hover {
        background: rgba(38, 224, 255, 0.12);
      }
      .dc-foot {
        display: flex;
        flex-wrap: wrap;
        gap: 6px 18px;
        margin-top: 16px;
        padding-top: 12px;
        border-top: 1px solid var(--line-2, rgba(122, 145, 190, 0.15));
        font-family: var(--mono, monospace);
        font-size: 10.5px;
        color: var(--muted, #8a97ad);
      }
      .panel.state {
        display: flex;
        flex-direction: column;
        gap: 6px;
        margin-top: 16px;
        padding: 16px 18px;
        border: 1px solid var(--line, #26304a);
        border-radius: 10px;
      }
      .state b {
        color: var(--text, #e6ecf5);
        font-size: 14px;
      }
      .state span {
        font-family: var(--mono, monospace);
        font-size: 12px;
        color: var(--muted, #8a97ad);
      }
      .state code {
        color: var(--text, #e6ecf5);
        background: rgba(255, 255, 255, 0.06);
        padding: 1px 5px;
        border-radius: 4px;
      }
      .state.err {
        border-color: rgba(255, 45, 111, 0.4);
      }
      .state.warn {
        border-color: rgba(255, 176, 32, 0.4);
      }
      .retry {
        align-self: flex-start;
        cursor: pointer;
        margin-top: 4px;
        font-family: var(--mono, monospace);
        font-size: 11px;
        color: var(--cyan, #26e0ff);
        background: rgba(38, 224, 255, 0.06);
        border: 1px solid rgba(38, 224, 255, 0.35);
        border-radius: 8px;
        padding: 5px 12px;
      }
      .pulse {
        font-family: var(--mono, monospace);
        font-size: 12px;
        color: var(--muted, #8a97ad);
        animation: pulse 1.4s ease-in-out infinite;
      }
      @keyframes pulse {
        0%,
        100% {
          opacity: 0.35;
        }
        50% {
          opacity: 0.75;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .pulse {
          animation: none;
        }
      }
    `,
  ],
})
export class DetectionCoverageComponent implements OnInit {
  private readonly svc = inject(DetectionCoverageService);

  /** Limite seguro inicial de técnicas exibidas (o resto abre sob demanda). */
  private static readonly InitialLimit = 25;

  readonly view = signal<DetectionCoverageView | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly showAll = signal(false);

  /** Estado da UI derivado — uma única fonte para o @switch do template. */
  readonly uiState = computed<
    'loading' | 'error' | 'notConfigured' | 'neverSynced' | 'unavailableEmpty' | 'data'
  >(() => {
    if (this.loading()) return 'loading';
    if (this.error()) return 'error';
    const v = this.view();
    if (!v) return 'error';
    if (v.state === 'NotConfigured') return 'notConfigured';
    if (v.state === 'NeverSynced') return 'neverSynced';
    // Falha total sem inventário preservado: nada a mostrar além do aviso.
    if (v.state === 'Unavailable' && v.storedCollectionState === null) return 'unavailableEmpty';
    return 'data';
  });

  readonly visibleTechniques = computed<DetectionCoverageTechnique[]>(() => {
    const all = this.view()?.techniques ?? [];
    return this.showAll() ? all : all.slice(0, DetectionCoverageComponent.InitialLimit);
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.showAll.set(false);
    this.svc.get().subscribe({
      next: (v) => {
        this.view.set(v);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }
}
