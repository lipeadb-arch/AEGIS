import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { environment } from '../../environments/environment';
import { ScoreGaugeComponent } from '../components/scoring/score-gauge.component';
import {
  KnightAssessment,
  KnightSourceType,
  KnightSources,
  capabilityOutcomeLabel,
  categoryLabel,
  connectionBadgeLabel,
  connectionStateOf,
  isProblemState,
  problemCapabilities,
  severityLabel,
  sortIndicatorsByRisk,
  sourceStateLabel,
  sourceTypeLabel,
  statusLabel,
} from '../models/knight.models';
import { IdentityRiskPanelComponent } from '../components/identity/identity-risk-panel.component';
import { IdentityEvidenceProjection } from '../models/identity-risk.models';
import { IdentityRiskService } from '../services/identity-risk.service';
import { KnightService } from '../services/knight.service';

/**
 * AegisKnightComponent — SMART. Tela MULTICOLETOR do AEGIS KNIGHT.
 *
 * Ao abrir, lê as FONTES disponíveis e o ÚLTIMO assessment (somente leitura — NÃO executa análise). O botão
 * "Executar assessment demo" roda a fonte Demo (sintética); quando uma fonte real (Entra ID) está configurada,
 * um botão dedicado dispara a COLETA REAL. A tela identifica a fonte usada e o estado da coleta (concluída,
 * parcial, permissão insuficiente…), lista as limitações de cobertura e nunca confunde Demo com coleta real —
 * uma coleta real que falha mostra o estado real, jamais o resultado Demo.
 */
@Component({
  selector: 'app-aegis-knight',
  standalone: true,
  imports: [ScoreGaugeComponent, DatePipe, RouterLink, IdentityRiskPanelComponent],
  template: `
    <section class="knight">
      <p class="eyebrow">AEGIS KNIGHT · Postura de Identidade e Exposição · Multicoletor</p>

      <header class="head">
        <div class="titles">
          <h1>
            AEGIS KNIGHT
            <span class="badge" [class]="badgeState()">{{ badgeLabel() }}</span>
          </h1>
          <p class="blurb">
            Avaliação determinística de exposição de identidade. Vereditos por regras; interpretação
            assistida por IA.
          </p>
        </div>
        <div class="actions">
          <a class="btn ghost" routerLink="/history">Histórico auditável</a>
          <button type="button" class="btn run" (click)="runDemo()" [disabled]="busy()">
            {{ running() ? 'Executando…' : 'Executar avaliação demo' }}
          </button>
          @if (entraConfigured()) {
            <button type="button" class="btn real" (click)="runSource('MicrosoftEntraId')" [disabled]="busy()">
              {{ running() ? 'Coletando…' : 'Coletar do Entra ID' }}
            </button>
          }
          @if (googleConfigured()) {
            <button type="button" class="btn real" (click)="runSource('GoogleWorkspace')" [disabled]="busy()">
              {{ running() ? 'Coletando…' : 'Coletar do Google Workspace' }}
            </button>
          }
        </div>
      </header>

      @if (loading()) {
        <div class="panel state"><span class="pulse">Carregando fontes e última avaliação…</span></div>
      } @else if (error() && !assessment()) {
        <div class="panel state err">
          <b>{{ error() }}</b>
          <span>Verifique se a API está no ar em <code>{{ apiBase }}</code> e tente novamente.</span>
          <button type="button" class="btn ghost" (click)="reload()">Tentar novamente</button>
        </div>
      } @else {
        @if (error()) {
          <div class="banner err">
            <span>{{ error() }}</span>
            <button type="button" class="btn ghost" (click)="clearError()">Fechar</button>
          </div>
        }

        @if (assessment(); as a) {
          <!-- Linha de fonte + estado da coleta: distingue Demo de coleta real, sempre. -->
          <div class="source-line" [class.problem]="isProblemState(a.sourceState)">
            <span class="src-tag" [class.demo]="a.isDemo">{{ sourceTypeLabel(a.sourceType) }}</span>
            <span class="sep">·</span>
            <span>Fonte: <b>{{ a.source }}</b></span>
            <span class="sep">·</span>
            <span>Estado da coleta: <b>{{ sourceStateLabel(a.sourceState) }}</b></span>
            <span class="sep">·</span>
            <span>Última coleta: <b>{{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }}</b></span>
          </div>

          @if (a.isDemo) {
            <p class="demo-note">
              <b>Modo demonstração.</b> Dados 100% sintéticos (domínio <code>demo.example.com</code>). O Aegis
              <b>não</b> está conectado ao Entra ID, AD local ou Okta.
            </p>
          } @else if (isProblemState(a.sourceState)) {
            <p class="demo-note real-problem">
              <b>Coleta real com limitação.</b> Esta é uma coleta da fonte <b>{{ a.source }}</b> (não é Demo) —
              o estado <b>{{ sourceStateLabel(a.sourceState) }}</b> reduz a cobertura; itens sem dado ficam
              "Não avaliado", nunca "Conforme".
            </p>
          }

          @if (limitations().length) {
            <div class="panel limits">
              <h4>Limitações de coleta</h4>
              <ul>
                @for (c of limitations(); track c.capability) {
                  <li><b>{{ c.capability }}</b> — {{ capabilityOutcomeLabel(c.outcome) }}@if (c.detail) { · {{ c.detail }} }</li>
                }
              </ul>
            </div>
          }

          <div class="grid">
            <div class="panel summary">
              @if (a.score !== null) {
                <app-score-gauge [percent]="a.score" caption="AEGIS KNIGHT" />
              } @else {
                <div class="no-score"><span class="dash">—</span><span class="l">sem avaliação</span></div>
              }
              <p class="score-note">Score AEGIS KNIGHT — distinto do AEGIS Score geral.</p>

              <div class="meta">
                <div class="mrow"><span class="k">Cobertura</span><span class="v">{{ a.coverage }}%</span></div>
                <div class="mrow"><span class="k">Catálogo</span><span class="v mono">{{ a.catalogVersion }}</span></div>
                <div class="mrow"><span class="k">Fórmula</span><span class="v mono">{{ a.scoreFormulaVersion }}</span></div>
              </div>

              <div class="counts">
                <div class="count ok"><span class="n">{{ a.counts.passed }}</span><span class="l">Conformes</span></div>
                <div class="count fail" [class.hot]="a.counts.exposed > 0"><span class="n">{{ a.counts.exposed }}</span><span class="l">Expostos</span></div>
                <div class="count comp"><span class="n">{{ a.counts.mitigated }}</span><span class="l">Mitigados</span></div>
                <div class="count mute"><span class="n">{{ a.counts.notEvaluated }}</span><span class="l">Não avaliados</span></div>
                <div class="count mute" [class.hot]="a.counts.error > 0"><span class="n">{{ a.counts.error }}</span><span class="l">Erros</span></div>
              </div>
            </div>

            <div class="panel list">
              <div class="hd">
                <h3>Indicadores</h3>
                <span class="hint">Veredito por regras determinísticas · expostos no topo</span>
              </div>
              <div class="tbl-wrap">
                <table class="tbl">
                  <thead>
                    <tr>
                      <th>Indicador</th><th>Categoria</th><th>Severidade</th><th>Status</th>
                      <th>Evidência</th><th class="num">Afetados</th><th>NIST / MITRE</th><th>Recomendação</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (ind of sortedIndicators(); track ind.indicatorId) {
                      <tr>
                        <td><span class="code">{{ ind.indicatorId }}</span><span class="tt">{{ ind.title }}</span></td>
                        <td>{{ categoryLabel(ind.category) }}</td>
                        <td><span class="sev" [class]="ind.severity">{{ severityLabel(ind.severity) }}</span></td>
                        <td><span class="st" [class]="ind.status">{{ statusLabel(ind.status) }}</span></td>
                        <td class="ev">
                          {{ ind.evidence }}
                          @if (ind.notEvaluatedReason) { <span class="ne-reason">Motivo: {{ ind.notEvaluatedReason }}</span> }
                        </td>
                        <td class="num">{{ ind.affectedObjectCount }}</td>
                        <td class="map">
                          <span class="nist">{{ ind.nistCodes.join(', ') || '—' }}</span>
                          <span class="mitre">{{ ind.mitreTechniques.length ? ind.mitreTechniques.join(' · ') : '—' }}</span>
                        </td>
                        <td class="rec">{{ ind.recommendation }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </div>
          </div>

          <app-identity-risk-panel [projection]="riskProjection()" />

          <div class="panel ai">
            <div class="hd">
              <h3>Interpretação e priorização assistidas por IA</h3>
              <span class="src" [class.fallback]="!a.advisoryFromAi">
                {{ a.advisoryFromAi ? 'Gerado por IA' : 'Fallback determinístico (IA indisponível)' }}
              </span>
            </div>
            @if (a.advisory; as ai) {
              <p class="summary-txt">{{ ai.executiveSummary }}</p>
              @if (ai.priorityRisks.length) {
                <div class="ai-block"><h4>Riscos prioritários</h4><ol>
                  @for (r of ai.priorityRisks; track $index) {
                    <li><b>{{ r.title }}</b> — {{ r.rationale }} <span class="ids">[{{ r.indicatorIds.join(', ') }}]</span></li>
                  }
                </ol></div>
              }
              @if (ai.recommendedActions.length) {
                <div class="ai-block"><h4>Ações recomendadas</h4><ol>
                  @for (act of ai.recommendedActions; track act.order) {
                    <li>{{ act.action }} <span class="ids">[{{ act.indicatorIds.join(', ') }}]</span></li>
                  }
                </ol></div>
              }
              @if (ai.correlations.length) {
                <div class="ai-block"><h4>Correlações</h4><ul>
                  @for (c of ai.correlations; track $index) {
                    <li>{{ c.description }} <span class="ids">[{{ c.indicatorIds.join(', ') }}]</span></li>
                  }
                </ul></div>
              }
              @if (ai.collectionGaps.length) {
                <div class="ai-block"><h4>Lacunas de coleta</h4><ul>
                  @for (g of ai.collectionGaps; track $index) { <li>{{ g }}</li> }
                </ul></div>
              }
            } @else {
              <p class="summary-txt muted">Sem interpretação disponível para esta avaliação.</p>
            }
          </div>
        } @else {
          <div class="panel state empty">
            <b>Nenhuma avaliação executada ainda.</b>
            <span>
              Este módulo é MULTICOLETOR. Em <code>example.com</code> use a DEMONSTRAÇÃO; conecte o
              Microsoft Entra ID (somente leitura) para executar uma coleta real.
            </span>
            @if (entraConfigured()) {
              <span>Fonte real configurada: <b>Microsoft Entra ID</b>.</span>
            }
            @if (googleConfigured()) {
              <span>Fonte real configurada: <b>Google Workspace</b>.</span>
            }
            @if (!entraConfigured() && !googleConfigured()) {
              <span>Nenhuma fonte real configurada — apenas a demonstração está disponível.</span>
            }
            <div class="empty-actions">
              <button type="button" class="btn run" (click)="runDemo()" [disabled]="busy()">
                {{ running() ? 'Executando…' : 'Executar avaliação demo' }}
              </button>
              @if (entraConfigured()) {
                <button type="button" class="btn real" (click)="runSource('MicrosoftEntraId')" [disabled]="busy()">
                  {{ running() ? 'Coletando…' : 'Coletar do Entra ID' }}
                </button>
              }
              @if (googleConfigured()) {
                <button type="button" class="btn real" (click)="runSource('GoogleWorkspace')" [disabled]="busy()">
                  {{ running() ? 'Coletando…' : 'Coletar do Google Workspace' }}
                </button>
              }
            </div>
          </div>
        }
      }
    </section>
  `,
  styles: [
    `
      :host { display: block; padding: 28px 32px 60px; }
      .eyebrow { font-family: var(--mono); font-size: 11px; letter-spacing: 0.14em; color: var(--muted); text-transform: uppercase; margin: 0 0 8px; }
      .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 18px; flex-wrap: wrap; margin: 0 0 20px; }
      .head h1 { font-family: var(--sans); font-size: 24px; color: var(--text); margin: 0 0 4px; display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
      .head .blurb { color: var(--muted); font-size: 13px; margin: 0; font-family: var(--mono); letter-spacing: 0.02em; }
      .badge { font-family: var(--mono); font-size: 10px; font-weight: 700; letter-spacing: 0.1em; padding: 4px 10px; border-radius: 999px; border: 1px solid var(--line); }
      .badge.Demo { color: var(--amber); border-color: rgba(255, 176, 32, 0.5); background: rgba(255, 176, 32, 0.08); }
      .badge.Connected { color: var(--cyan); border-color: rgba(38, 224, 255, 0.5); background: rgba(38, 224, 255, 0.08); }
      .badge.NotConfigured { color: var(--muted); background: rgba(122, 145, 190, 0.08); }
      .actions { display: flex; gap: 10px; flex-wrap: wrap; }
      .btn { cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600; border-radius: 11px; padding: 9px 16px; transition: 0.15s; border: 1px solid transparent; }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .btn.run { color: #05070f; background: var(--neon-h); box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6); }
      .btn.real { color: var(--cyan); background: rgba(38, 224, 255, 0.08); border-color: rgba(38, 224, 255, 0.45); }
      .btn.ghost { color: var(--text); background: rgba(122, 145, 190, 0.08); border-color: var(--line); }
      a.btn { text-decoration: none; display: inline-flex; align-items: center; }
      .panel { border: 1px solid var(--line); border-radius: 14px; background: rgba(122, 145, 190, 0.03); padding: 18px; }

      .source-line { font-family: var(--mono); font-size: 11.5px; color: var(--muted); display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 12px; }
      .source-line b { color: var(--text); }
      .source-line.problem b { color: var(--amber); }
      .src-tag { font-weight: 700; letter-spacing: 0.06em; color: var(--cyan); border: 1px solid rgba(38, 224, 255, 0.4); border-radius: 6px; padding: 2px 8px; }
      .src-tag.demo { color: var(--amber); border-color: rgba(255, 176, 32, 0.4); }
      .source-line .sep { opacity: 0.4; }

      .demo-note { margin: 0 0 16px; font-family: var(--mono); font-size: 12px; line-height: 1.5; color: var(--muted); border-left: 2px solid var(--amber); padding: 8px 12px; background: rgba(255, 176, 32, 0.05); border-radius: 0 8px 8px 0; }
      .demo-note b { color: var(--amber); }
      .demo-note.real-problem { border-left-color: var(--cyan); background: rgba(38, 224, 255, 0.05); }
      .demo-note.real-problem b { color: var(--cyan); }
      code { color: var(--text); background: rgba(255, 255, 255, 0.06); padding: 1px 5px; border-radius: 4px; }

      .limits { margin-bottom: 16px; }
      .limits h4 { margin: 0 0 8px; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--amber); }
      .limits ul { margin: 0; padding-left: 18px; }
      .limits li { font-family: var(--mono); font-size: 11.5px; color: var(--muted); line-height: 1.5; }
      .limits li b { color: var(--text); }

      .banner { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 16px; padding: 12px 16px; border-radius: 10px; }
      .banner.err { border: 1px solid rgba(255, 45, 111, 0.4); background: rgba(255, 45, 111, 0.06); color: #ffe3ee; font-family: var(--mono); font-size: 12px; }

      .grid { display: grid; grid-template-columns: 300px 1fr; gap: 18px; align-items: start; }
      .summary { display: flex; flex-direction: column; gap: 14px; }
      .no-score { display: flex; flex-direction: column; align-items: center; gap: 4px; padding: 24px 0; }
      .no-score .dash { font-family: var(--display); font-size: 44px; color: var(--muted); }
      .no-score .l { font-family: var(--mono); font-size: 11px; letter-spacing: 0.1em; color: var(--muted); text-transform: uppercase; }
      .score-note { margin: 0; font-family: var(--mono); font-size: 10.5px; color: var(--muted); text-align: center; }
      .meta { display: flex; flex-direction: column; gap: 6px; border-top: 1px solid var(--line); padding-top: 12px; }
      .mrow { display: flex; justify-content: space-between; gap: 10px; font-family: var(--mono); font-size: 11.5px; }
      .mrow .k { color: var(--muted); } .mrow .v { color: var(--text); } .mrow .v.mono { color: var(--cyan); }
      .counts { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
      .count { display: flex; flex-direction: column; gap: 2px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 9px; background: rgba(122, 145, 190, 0.03); }
      .count .n { font-family: var(--display); font-weight: 700; font-size: 19px; color: var(--text); }
      .count .l { font-family: var(--mono); font-size: 9px; text-transform: uppercase; letter-spacing: 0.1em; color: var(--muted); }
      .count.ok .n { color: var(--cyan); } .count.comp .n { color: var(--amber); }
      .count.fail.hot { border-color: rgba(255, 45, 111, 0.45); background: rgba(255, 45, 111, 0.06); }
      .count.fail.hot .n { color: var(--red); } .count.mute.hot .n { color: var(--red); }

      .list .hd, .ai .hd { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin-bottom: 14px; flex-wrap: wrap; }
      .list h3, .ai h3 { margin: 0; font-size: 14px; font-weight: 600; color: var(--text); }
      .list .hint { font-family: var(--mono); font-size: 11px; color: var(--muted); }
      .tbl-wrap { overflow-x: auto; }
      .tbl { width: 100%; border-collapse: collapse; font-size: 12px; }
      .tbl th { text-align: left; font-family: var(--mono); font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); padding: 8px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
      .tbl th.num, .tbl td.num { text-align: right; }
      .tbl td { padding: 10px; border-bottom: 1px solid rgba(122, 145, 190, 0.12); color: var(--text); vertical-align: top; }
      .tbl .code { display: block; font-family: var(--mono); font-size: 11px; color: var(--cyan); }
      .tbl .tt { display: block; font-size: 11.5px; color: var(--text); max-width: 220px; }
      .tbl .ev { max-width: 260px; color: var(--muted); }
      .tbl .ev .ne-reason { display: block; margin-top: 3px; font-size: 10.5px; color: var(--amber); }
      .tbl .rec { max-width: 240px; color: var(--muted); }
      .tbl .map { font-family: var(--mono); font-size: 10.5px; }
      .tbl .map .nist { display: block; color: var(--text); } .tbl .map .mitre { display: block; color: var(--muted); margin-top: 2px; }
      .sev { font-family: var(--mono); font-size: 10px; font-weight: 700; padding: 2px 7px; border-radius: 6px; white-space: nowrap; }
      .sev.Critical { color: var(--red); background: rgba(255, 45, 111, 0.12); }
      .sev.High { color: #ff9a3d; background: rgba(255, 154, 61, 0.12); }
      .sev.Medium { color: var(--amber); background: rgba(255, 176, 32, 0.12); }
      .sev.Low { color: var(--cyan); background: rgba(38, 224, 255, 0.1); }
      .sev.Informational { color: var(--muted); background: rgba(122, 145, 190, 0.1); }
      .st { font-family: var(--mono); font-size: 10.5px; font-weight: 700; padding: 2px 8px; border-radius: 6px; white-space: nowrap; }
      .st.Passed { color: var(--cyan); background: rgba(38, 224, 255, 0.1); }
      .st.Exposed { color: var(--red); background: rgba(255, 45, 111, 0.12); }
      .st.Mitigated { color: var(--amber); background: rgba(255, 176, 32, 0.12); }
      .st.NotEvaluated, .st.NotApplicable { color: var(--muted); background: rgba(122, 145, 190, 0.1); }
      .st.Error { color: #ff9a3d; background: rgba(255, 154, 61, 0.12); }

      .ai { margin-top: 18px; }
      .ai .src { font-family: var(--mono); font-size: 10px; font-weight: 700; letter-spacing: 0.06em; padding: 3px 9px; border-radius: 999px; color: var(--cyan); border: 1px solid rgba(38, 224, 255, 0.4); }
      .ai .src.fallback { color: var(--amber); border-color: rgba(255, 176, 32, 0.4); }
      .summary-txt { font-size: 13px; line-height: 1.6; color: var(--text); margin: 0 0 14px; }
      .summary-txt.muted { color: var(--muted); }
      .ai-block { margin-bottom: 14px; }
      .ai-block h4 { margin: 0 0 6px; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--cyan); }
      .ai-block ol, .ai-block ul { margin: 0; padding-left: 20px; }
      .ai-block li { font-size: 12.5px; line-height: 1.55; color: var(--text); margin-bottom: 5px; }
      .ai-block .ids { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }

      .state { display: flex; flex-direction: column; gap: 10px; align-items: flex-start; }
      .state b { color: var(--text); font-size: 14px; }
      .state span { font-family: var(--mono); font-size: 12px; color: var(--muted); line-height: 1.5; }
      .empty-actions { display: flex; gap: 10px; flex-wrap: wrap; margin-top: 4px; }
      .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); letter-spacing: 0.08em; animation: pulse 1.4s ease-in-out infinite; }
      .state.err { border-color: rgba(255, 45, 111, 0.4); } .state.err b { color: #ffe3ee; }
      @keyframes pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 0.75; } }
      @media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }
      @media (prefers-reduced-motion: reduce) { .pulse { animation: none; } .btn { transition: none; } }
    `,
  ],
})
export class AegisKnightComponent implements OnInit {
  private readonly knight = inject(KnightService);
  private readonly identityRisk = inject(IdentityRiskService);

  readonly assessment = signal<KnightAssessment | null>(null);
  readonly sources = signal<KnightSources | null>(null);
  readonly loading = signal(true); // 1ª carga (fontes + último)
  readonly running = signal(false); // execução (demo ou real)
  readonly error = signal<string | null>(null);

  protected readonly apiBase = environment.apiBase;

  // Helpers de apresentação (funções puras do modelo) expostos ao template.
  protected readonly categoryLabel = categoryLabel;
  protected readonly severityLabel = severityLabel;
  protected readonly statusLabel = statusLabel;
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly sourceStateLabel = sourceStateLabel;
  protected readonly capabilityOutcomeLabel = capabilityOutcomeLabel;
  protected readonly isProblemState = isProblemState;

  readonly badgeState = computed(() => connectionStateOf(this.assessment()));
  readonly badgeLabel = computed(() => connectionBadgeLabel(this.badgeState()));
  readonly sortedIndicators = computed(() => sortIndicatorsByRisk(this.assessment()?.indicators ?? []));
  readonly limitations = computed(() => problemCapabilities(this.assessment()?.capabilities ?? []));
  readonly busy = computed(() => this.running() || this.loading());
  readonly entraConfigured = computed(
    () => this.sources()?.realSources.some((s) => s.source === 'MicrosoftEntraId' && s.configured) ?? false,
  );
  readonly googleConfigured = computed(
    () => this.sources()?.realSources.some((s) => s.source === 'GoogleWorkspace' && s.configured) ?? false,
  );

  // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade -------------------------------------
  // Lê o snapshot JÁ persistido pela Evidence Fabric: a MESMA fotografia que o KNIGHT avalia, sem uma
  // segunda consulta ao Microsoft Graph.
  readonly riskProjection = signal<IdentityEvidenceProjection | null>(null);



  ngOnInit(): void {
    // Somente LEITURA ao abrir — fontes + último assessment. NÃO executa análise automaticamente.
    this.reload();
  }

  /** Recarrega fontes + último assessment (read-only). */
  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.knight.getSources().subscribe({
      next: (s) => this.sources.set(s),
      error: () => this.sources.set(null), // fontes é secundário; não bloqueia a tela
    });
    this.knight.getLatest().subscribe({
      next: (a) => {
        this.assessment.set(a);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
    this.reloadRisk();
  }

  /**
   * Recarrega a fotografia de risco de identidade (somente leitura do snapshot compartilhado). Uma falha
   * aqui NÃO bloqueia a tela nem zera a seção: mantemos a última projeção carregada, se houver.
   */
  private reloadRisk(): void {
    this.identityRisk.get().subscribe({
      next: (p) => this.riskProjection.set(p),
      error: () => {
        /* seção secundária: preserva a projeção anterior em vez de exibir zeros */
      },
    });
  }

  clearError(): void {
    this.error.set(null);
  }

  /** Executa o assessment de demonstração. */
  runDemo(): void {
    this.execute(() => this.knight.runDemo());
  }

  /** Executa a coleta real da fonte indicada (ex.: Entra ID). Falha real NÃO cai para Demo. */
  runSource(source: KnightSourceType): void {
    this.execute(() => this.knight.runSource(source));
  }

  private execute(run: () => ReturnType<KnightService['runDemo']>): void {
    this.running.set(true);
    this.error.set(null);
    run().subscribe({
      next: (a) => {
        this.assessment.set(a);
        this.running.set(false);
        // A coleta acabou de reescrever o snapshot compartilhado — relê a MESMA fotografia (sem novo Graph).
        this.reloadRisk();
      },
      error: (e: Error) => {
        // Mantém o assessment anterior visível; NUNCA substitui por Demo numa falha real.
        this.error.set(e.message);
        this.running.set(false);
      },
    });
  }
}
