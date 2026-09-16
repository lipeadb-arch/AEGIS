import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, timer } from 'rxjs';
import { catchError, exhaustMap, filter } from 'rxjs/operators';
import { GovernanceService } from '../services/governance.service';
import {
  ANALYSIS_STATUSES,
  AiAnalysisStatus,
  DOCUMENT_TYPES,
  DocumentDisplayState,
  DocumentIntegrationAvailability,
  GovernCoverage,
  GovernanceDocument,
  GovernanceDocumentType,
  analysisErrorLabel,
  documentDisplayState,
  documentDisplayStateLabel,
  documentSourceLabel,
  documentTypeLabel,
  hasProbativeEvidence,
  isActiveAnalysisStatus,
} from '../models/governance.models';
import { environment } from '../../environments/environment';
import { AgentStateService } from '../services/agent-state.service';
import { AiModeBannerComponent } from '../components/ai-mode-banner.component';
import { PostureSummaryComponent } from '../components/scoring/posture-summary.component';
import { ControlComplianceCardComponent } from '../components/scoring/control-compliance-card.component';
import { AegisPillarChecklistComponent } from '../components/scoring/aegis-pillar-checklist.component';
import { ScoringService } from '../services/scoring.service';
import { AegisScoreService } from '../services/aegis-score.service';
import { PILLARS, TenantControlStateDto, buildPillarView } from '../models/scoring.models';
import { FunctionPosture, functionOf } from '../models/workspace.models';

type SyncState = 'idle' | 'loading' | 'done' | 'error';

/**
 * GOVERN (GV) → Central de Governança. Reúne as três faces do pilar numa só tela HUD:
 *   1) POSTURA por telemetria (GV.SC/GV.RR) — reusa o ScoreGauge + ControlComplianceCard dos painéis de
 *      pilar (DRY): mesma leitura de conformidade de Protect/Detect, só que com o prefixo GV.
 *   2) INGESTÃO — integração corporativa (sync das fontes externas via Provider Pattern) + upload manual.
 *   3) HUB — a lista de documentos ingeridos e a leitura da IA.
 *
 * Padrão da casa: standalone, Signals para TODO o estado (sem async pipe/RxJS na view), sem @angular/forms.
 */
@Component({
  selector: 'app-document-hub',
  standalone: true,
  imports: [
    DatePipe,
    PostureSummaryComponent,
    ControlComplianceCardComponent,
    AegisPillarChecklistComponent,
    AiModeBannerComponent,
  ],
  template: `
    <div class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Governança e controles</p>
          <h1>Central de governança</h1>
          <!-- Subtítulo tático da Função Govern (mesmo texto dos painéis de pilar) -->
          <p class="page-desc">{{ govMeta.description }}</p>
          <p class="page-meta">NIST CSF 2.0 · Govern (GV) · evidências e documentos</p>
        </div>
        <div class="head-stat" title="Categorias GV com documentação que cita a execução do controle — cobertura documental, não conformidade">
          <span class="head-stat-k">Cobertura documental GV</span>
          <span class="head-stat-v">
            @if (coverage(); as cov) {
              {{ cov.coveredPct }}%
            } @else {
              —
            }
          </span>
        </div>
      </header>

      <!-- Estado da IA + aviso do Free Tier (só dados sintéticos) — o Hub é onde documentos são enviados. -->
      <app-ai-mode-banner />

      <!-- ============ 1) POSTURA DE GOVERNANÇA (telemetria GV.SC / GV.RR) ============ -->
      <section class="panel gov-score">
        <div class="hd">
          <h3>Postura de Governança</h3>
          <span class="hint">GV.SC · GV.RR — avaliado por telemetria (autoritativo)</span>
        </div>

        @if (scoringLoading()) {
          <span class="pulse">Carregando a postura de governança…</span>
        } @else if (scoringError()) {
          <p class="score-err">
            Não foi possível carregar a postura de governança agora. Recarregue a página em alguns instantes.
          </p>
        } @else {
          <div class="gs-grid">
            <div class="gs-left">
              <!-- Cabeçalho de postura COMPARTILHADO (mesma projeção única do Dashboard e das demais Funções):
                   score anulável (Não avaliado ≠ 0%) + cobertura. Nunca recalculado aqui. -->
              @switch (govPostureState()) {
                @case ('loaded') {
                  <app-posture-summary [posture]="govPosture()!" label="Governança" code="GV" />
                }
                @case ('loading') { <span class="pulse">Carregando o resumo de postura…</span> }
                @case ('notFound') { <span class="pulse">Sem catálogo ativo para a Função Govern.</span> }
                @case ('error') {
                  <div class="posture-err">
                    <span>Não foi possível carregar o resumo de postura.</span>
                    <button type="button" class="ghost sm" (click)="loadWorkspacePosture()">Tentar novamente</button>
                  </div>
                }
              }
            </div>
            <app-control-compliance-card [controls]="govView().controls" />
          </div>
        }
      </section>

      <!-- ---- Cobertura documental (híbrida: documentos + entrevistas) ---- -->
      @if (coverage(); as cov) {
        <section class="panel coverage-strip">
          <div class="cov-metric">
            <span class="cov-k">Coberto (doc.)</span>
            <span class="cov-v ok">{{ cov.coveredPct }}%</span>
          </div>
          <div class="cov-metric">
            <span class="cov-k">Parcial</span>
            <span class="cov-v warn">{{ cov.partialPct }}%</span>
          </div>
          <div class="cov-metric">
            <span class="cov-k">Categorias GV</span>
            <span class="cov-v">{{ cov.categories.length }}</span>
          </div>
        </section>
      }

      <!-- ---- Pendências de Governança ----
           Posicionada de propósito ENTRE o diagnóstico e as ações: logo abaixo vêm a sincronização de
           políticas e o upload manual, que são exatamente como se fecham estas lacunas. Ler "falta a
           política X" e ter o botão de subir documento na sequência é o fluxo que a tela deve ter. -->
      <app-aegis-pillar-checklist pillar="GV" heading="Pendências de Governança" />

      <!-- ============ 2) INGESTÃO — Integração Corporativa ============ -->
      <!-- [AEGIS-MVP-PRODUCT-01] A tela anunciava a sincronização de políticas corporativas tendo por trás
           apenas um provedor SIMULADO, que ingeria documentos fictícios sob o nome do cliente. Agora ela
           mostra a disponibilidade REAL que o servidor reporta: sem fonte, o botão não existe — e o upload
           manual, que é real, segue como o caminho de governança. -->
      <section class="panel integration">
        <p class="eyebrow">Integração Corporativa</p>

        @if (integration(); as avail) {
          @if (avail.availableProviders.length > 0) {
            <div class="int-row">
              <p class="int-copy">
                @if (avail.hasOperationalProvider) {
                  Puxe as políticas das fontes corporativas conectadas para leitura automática pela IA —
                  sem upload manual.
                } @else {
                  <span class="int-demo">Modo demonstrativo.</span> As políticas sincronizadas aqui são de
                  exemplo, não são do seu ambiente, e servem apenas para demonstrar o fluxo de leitura.
                }
              </p>
              <button
                type="button"
                class="btn primary sync-btn"
                (click)="triggerSync()"
                [disabled]="syncState() === 'loading'"
              >
                @if (syncState() === 'loading') {
                  <span class="spin"></span> Sincronizando…
                } @else {
                  Sincronizar Políticas Corporativas
                }
              </button>
            </div>

            @if (syncState() === 'done') {
              <p class="int-ok">✓ {{ syncMessage() }}</p>
            } @else if (syncState() === 'error') {
              <p class="int-err">{{ syncMessage() }}</p>
            }
          } @else {
            <p class="int-copy">
              A sincronização automática de políticas <span class="int-demo">ainda não está disponível</span> neste ambiente.
              Envie os documentos de governança pelo upload abaixo — eles seguem o mesmo fluxo de leitura
              e mapeamento.
            </p>
          }
        } @else {
          <p class="int-copy">Verificando as fontes documentais disponíveis…</p>
        }
      </section>

      <!-- ---- Upload manual ---- -->
      <section class="panel uploader">
        <p class="eyebrow">Upload Manual</p>
        <div class="up-row">
          <label class="up-field">
            <span>Arquivo</span>
            <input
              type="file"
              accept=".pdf,.txt,.md,.csv,.json,.docx,application/pdf,text/plain,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
              (change)="onFileSelected($event)"
            />
          </label>

          <label class="up-field">
            <span>Título</span>
            <input
              type="text"
              placeholder="Ex.: Política de Segurança da Informação"
              [value]="uploadTitle()"
              (input)="uploadTitle.set($any($event.target).value)"
            />
          </label>

          <label class="up-field">
            <span>Tipo</span>
            <select [value]="uploadType()" (change)="uploadType.set($any($event.target).value)">
              @for (t of documentTypes; track t.value) {
                <option [value]="t.value">{{ t.label }}</option>
              }
            </select>
          </label>

          <button type="button" class="btn primary" (click)="submitUpload()" [disabled]="!canUpload()">
            {{ uploading() ? 'Enviando…' : 'Enviar para análise' }}
          </button>
        </div>

        @if (uploadError()) {
          <p class="up-error">{{ uploadError() }}</p>
        }
      </section>

      <!-- ---- Filtros ---- -->
      <section class="filter-bar" aria-label="Filtros de documentos">
        <div class="filter-row">
          <label class="ctl">
            <span>Tipo</span>
            <select [value]="typeFilter() ?? ''" (change)="setType($any($event.target).value)">
              <option value="">Todos</option>
              @for (t of documentTypes; track t.value) {
                <option [value]="t.value">{{ t.label }}</option>
              }
            </select>
          </label>

          <label class="ctl">
            <span>Status da análise</span>
            <select [value]="statusFilter() ?? ''" (change)="setStatus($any($event.target).value)">
              <option value="">Todos</option>
              @for (s of analysisStatuses; track s.value) {
                <option [value]="s.value">{{ s.label }}</option>
              }
            </select>
          </label>

          @if (typeFilter() || statusFilter()) {
            <button type="button" class="ghost sm" (click)="clearFilters()">Limpar filtros</button>
          }
        </div>
      </section>

      <!-- ---- Estado de erro ---- -->
      @if (loadError()) {
        <div class="notice error" role="alert">
          <b>Não foi possível carregar os documentos.</b> O serviço não respondeu agora — a lista fica
          vazia de propósito, para não apresentar um acervo desatualizado.
        </div>
      }

      <!-- ============ 3) HUB — documentos ingeridos ============ -->
      <section class="panel flush">
        <table class="doc-table">
          <thead>
            <tr>
              <th>Documento</th>
              <th>Tipo</th>
              <th>Origem</th>
              <th>Status da análise</th>
              <th class="num">Controles NIST associados</th>
              <th>Analisado em</th>
              <th class="num">Ações</th>
            </tr>
          </thead>
          <tbody>
            @for (d of docs(); track d.id) {
              <tr>
                <td>
                  <div class="doc-title">{{ d.title }}</div>
                  @if (d.fileName) {
                    <div class="doc-sub">{{ d.fileName }}</div>
                  }
                </td>
                <td><span class="tag">{{ typeLabel(d.type) }}</span></td>
                <td><span class="src">{{ sourceLabel(d.source) }}</span></td>
                <td>
                  <span class="ai-status st-{{ displayStateClass(d) }}">
                    @if (showsSpinner(d.analysisStatus)) {
                      <span
                        class="ai-spin"
                        role="status"
                        [attr.aria-label]="displayLabel(d)"
                      ></span>
                    }
                    {{ displayLabel(d) }}
                  </span>
                  @if (d.analysisStatus === 'Failed') {
                    <div class="ai-err" [title]="errorLabel(d.analysisError)">{{ errorLabel(d.analysisError) }}</div>
                  }
                </td>
                <td class="num">
                  <button
                    type="button"
                    class="map-toggle"
                    [class.on]="expandedId() === d.id"
                    [disabled]="d.analysisStatus !== 'Analyzed'"
                    (click)="toggleExpand(d.id)"
                    [attr.aria-expanded]="expandedId() === d.id"
                    title="Ver parecer e citações"
                  >
                    <span class="map-count">{{ d.mappings.length }}</span>
                    @if (d.analysisStatus === 'Analyzed') {
                      <span class="caret">{{ expandedId() === d.id ? '▾' : '▸' }}</span>
                    }
                  </button>
                </td>
                <td class="dim">{{ d.analyzedAt ? (d.analyzedAt | date: 'dd/MM/yy HH:mm') : '—' }}</td>
                <td class="num">
                  <div class="row-actions">
                    <button type="button" class="ghost xs" (click)="reanalyze(d.id)" [disabled]="busyId() === d.id">
                      Reanalisar
                    </button>
                    <button type="button" class="ghost xs danger" (click)="remove(d)" [disabled]="busyId() === d.id">
                      Excluir
                    </button>
                  </div>
                </td>
              </tr>

              <!-- Parecer expandível: resumo + citações literais (controle, confiança, trecho, justificativa) -->
              @if (expandedId() === d.id) {
                <tr class="detail-row">
                  <td colspan="7">
                    <div class="parecer">
                      <div class="parecer-head">
                        <span class="ev-badge {{ hasEvidence(d) ? 'ok' : 'none' }}">{{ displayLabel(d) }}</span>
                        @if (d.analysisSummary) {
                          <p class="summary">{{ d.analysisSummary }}</p>
                        }
                      </div>

                      @if (hasEvidence(d)) {
                        <ul class="cites">
                          @for (m of d.mappings; track m.subcategoryCode) {
                            @if (m.evidenceQuote) {
                              <li class="cite">
                                <div class="cite-head">
                                  <span class="ctrl">{{ m.subcategoryCode }}</span>
                                  <span class="conf">confiança {{ pct(m.confidence) }}%</span>
                                </div>
                                <blockquote class="quote">“{{ m.evidenceQuote }}”</blockquote>
                                @if (m.evidence) {
                                  <p class="rationale">{{ m.evidence }}</p>
                                }
                              </li>
                            }
                          }
                        </ul>
                      } @else {
                        <p class="no-evidence">
                          Documento analisado, mas sem trecho com valor probatório literal —
                          <b>não altera a postura de segurança</b>. Um documento só concede cobertura quando
                          cita explicitamente a execução do controle (responsável, periodicidade ou registro).
                        </p>
                      }
                    </div>
                  </td>
                </tr>
              }
            } @empty {
              <tr class="empty">
                <td colspan="7">
                  @if (loading()) {
                    Carregando documentos…
                  } @else if (loadError()) {
                    Documentos indisponíveis no momento — veja o aviso acima.
                  } @else if (typeFilter() || statusFilter()) {
                    Nenhum documento corresponde aos filtros atuais.
                  } @else {
                    Nenhum documento ingerido ainda. Envie o primeiro acima.
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    </div>
  `,
  styles: [
    `
      /* Página, cabeçalho, contador, filtros, painéis, avisos e botões: sistema visual global (styles.css). */

      /* ---- 1) Postura de Governança (telemetria GV) ---- */
      .gs-grid {
        display: grid;
        grid-template-columns: 300px minmax(0, 1fr);
        gap: var(--sp-5);
        align-items: start;
      }
      .gs-left {
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
      }
      .score-err {
        font-size: var(--fs-sm);
        color: var(--red-text);
      }
      .posture-err {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .pulse {
        font-size: var(--fs-sm);
        color: var(--text-2);
        animation: hub-pulse 1.4s ease-in-out infinite;
      }
      @keyframes hub-pulse {
        0%,
        100% {
          opacity: 0.55;
        }
        50% {
          opacity: 1;
        }
      }

      .coverage-strip {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-4) var(--sp-10);
      }
      .cov-metric {
        display: flex;
        flex-direction: column;
        gap: var(--sp-1);
      }
      .cov-k,
      .up-field,
      .ctl {
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .cov-v {
        font-size: 24px;
        font-weight: 700;
      }
      .cov-v.ok {
        color: var(--cyan);
      }
      .cov-v.warn {
        color: var(--amber);
      }

      /* ---- 2) Integração corporativa e upload ---- */
      .int-row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-5);
      }
      .int-copy {
        max-width: 72ch;
        font-size: var(--fs-sm);
        line-height: 1.55;
        color: var(--text-2);
      }
      .spin {
        width: 13px;
        height: 13px;
        border: 2px solid rgba(5, 7, 15, 0.35);
        border-top-color: #05070f;
        border-radius: 50%;
        animation: hub-spin 0.7s linear infinite;
      }
      @keyframes hub-spin {
        to {
          transform: rotate(360deg);
        }
      }
      .int-ok,
      .int-err {
        margin-top: var(--sp-3);
        font-size: var(--fs-sm);
      }
      .int-ok {
        color: var(--cyan);
      }
      .int-err {
        color: var(--red-text);
      }
      .int-demo {
        color: var(--amber);
        font-weight: 600;
      }
      .up-row {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: 14px;
      }
      .up-field {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .up-field input[type='text'],
      .up-field select {
        min-width: 240px;
        font-weight: 400;
        letter-spacing: 0;
        text-transform: none;
      }
      .up-field input[type='file'] {
        font-size: var(--fs-sm);
        font-weight: 400;
        letter-spacing: 0;
        text-transform: none;
        color: var(--text-2);
      }
      .up-error {
        margin-top: var(--sp-3);
        font-size: var(--fs-sm);
        color: var(--red-text);
      }
      .ctl {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
      }

      /* ---- 3) Hub de documentos ---- */
      table.doc-table {
        width: 100%;
        border-collapse: collapse;
        font-size: var(--fs-sm);
      }
      table.doc-table thead th {
        padding: 10px var(--sp-3);
        border-bottom: 1px solid var(--line);
        text-align: left;
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
        white-space: nowrap;
      }
      table.doc-table th.num,
      table.doc-table td.num {
        text-align: center;
      }
      table.doc-table tbody td {
        padding: var(--sp-3);
        border-bottom: 1px solid var(--line-2);
        vertical-align: middle;
      }
      table.doc-table tbody tr:hover td {
        background: rgba(38, 224, 255, 0.03);
      }
      .doc-title {
        font-weight: 600;
      }
      .doc-sub {
        margin-top: 2px;
        font-size: var(--fs-meta);
        color: var(--muted);
        overflow-wrap: anywhere;
      }
      .tag {
        font-size: var(--fs-meta);
        font-weight: 500;
        color: var(--cyan-2);
      }
      .src {
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
      .dim {
        font-size: var(--fs-meta);
        color: var(--muted);
        white-space: nowrap;
      }
      .map-count {
        font-size: var(--fs-sm);
        font-weight: 700;
      }
      .ai-status {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 3px 10px;
        border: 1px solid currentColor;
        border-radius: var(--radius-pill);
        font-size: var(--fs-meta);
        white-space: nowrap;
      }
      /* Spinner do status ATIVO (Na fila / Analisando); herda a cor via currentColor. */
      .ai-spin {
        flex: none;
        width: 9px;
        height: 9px;
        border: 1.5px solid currentColor;
        border-top-color: transparent;
        border-radius: 50%;
        animation: hub-spin 0.7s linear infinite;
      }
      /* Estado REFINADO da leitura (separa Analisado com/sem evidência). */
      .st-pending,
      .st-analyzedwithoutevidence {
        color: var(--text-2);
      }
      .st-queued {
        color: var(--cyan-2);
      }
      .st-processing {
        color: var(--amber);
      }
      .st-analyzedwithevidence {
        color: var(--cyan);
      }
      .st-failed {
        color: var(--red-text);
      }
      .ai-err {
        max-width: 240px;
        margin-top: var(--sp-1);
        font-size: var(--fs-meta);
        color: var(--red-text);
      }
      .map-toggle {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 4px var(--sp-2);
        border: 1px solid transparent;
        border-radius: var(--radius-sm);
        background: none;
        color: var(--text);
        cursor: pointer;
      }
      .map-toggle:not(:disabled):hover,
      .map-toggle.on {
        border-color: rgba(38, 224, 255, 0.4);
      }
      .map-toggle:focus-visible {
        outline: none;
        box-shadow: var(--focus);
      }
      .caret {
        font-size: var(--fs-caps);
        color: var(--cyan);
      }
      tr.detail-row td {
        padding: 0 var(--sp-4) var(--sp-4);
        background: rgba(38, 224, 255, 0.02);
      }
      .parecer {
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
      }
      .parecer-head {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-3);
      }
      .ev-badge {
        align-self: flex-start;
        padding: 3px 10px;
        border: 1px solid currentColor;
        border-radius: var(--radius-pill);
        font-size: var(--fs-caps);
        font-weight: 600;
      }
      .ev-badge.ok {
        color: var(--cyan);
      }
      .ev-badge.none {
        color: var(--amber);
      }
      .cites {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
      }
      .cite {
        padding-left: var(--sp-3);
        border-left: 2px solid rgba(38, 224, 255, 0.4);
      }
      .cite-head {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        margin-bottom: 6px;
      }
      .cite-head .ctrl {
        font-family: var(--mono);
        font-size: var(--fs-meta);
        font-weight: 600;
        color: var(--cyan-2);
      }
      .cite-head .conf {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .summary,
      .quote,
      .rationale,
      .no-evidence {
        line-height: var(--lh);
      }
      .summary,
      .quote {
        font-size: var(--fs-sm);
      }
      .quote {
        margin: 0 0 6px;
        font-style: italic;
      }
      .rationale,
      .no-evidence {
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
      .no-evidence b {
        color: var(--amber);
      }
      .row-actions {
        display: inline-flex;
        gap: 6px;
      }
      tr.empty td {
        padding: var(--sp-8);
        text-align: center;
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      @media (max-width: 900px) {
        .gs-grid {
          grid-template-columns: 1fr;
        }
      }
      @media (max-width: 720px) {
        .up-field {
          flex: 1 1 100%;
        }
        .up-field input[type='text'],
        .up-field select {
          min-width: 0;
          width: 100%;
        }
      }
    `,
  ],
})
export class DocumentHubComponent implements OnInit {
  private readonly svc = inject(GovernanceService);
  private readonly scoring = inject(ScoringService);
  private readonly scoreSvc = inject(AegisScoreService);
  protected readonly agent = inject(AgentStateService);

  constructor() {
    // O Auditor vive no App (global). Quando uma entrevista altera a cobertura, o AgentStateService
    // sinaliza e recarregamos o strip de cobertura documental desta tela.
    effect(() => {
      if (this.agent.coverageVersion() > 0) {
        this.loadCoverage();
        this.loadWorkspacePosture();
      }
    });

    // POLLING controlado (sem SignalR/SSE): a cada ~2s, SE houver documento ativo (Aguardando/Na fila/
    // Analisando), refaz a leitura da lista COMPLETA. `exhaustMap` impede requisições sobrepostas;
    // `catchError` tolera falha transitória sem limpar a tabela nem virar loop agressivo; o `filter` faz o
    // tick ser no-op quando tudo chega a estado terminal — o polling para sozinho. Encerra com o componente
    // via `takeUntilDestroyed` (chamado no contexto de injeção do construtor).
    timer(this.POLL_MS, this.POLL_MS)
      .pipe(
        filter(() => this.hasActiveDocs()),
        exhaustMap(() => this.svc.listDocuments().pipe(catchError(() => EMPTY))),
        takeUntilDestroyed(),
      )
      .subscribe((list) => this.applyPolledList(list));
  }

  // ---- Postura de Governança (telemetria GV.SC / GV.RR) ----
  private readonly govControls = signal<TenantControlStateDto[]>([]);
  scoringLoading = signal(true);
  scoringError = signal(false);
  /** Reusa o mesmo agregador dos painéis de pilar (DRY): a lista de controles ordenada por risco. */
  readonly govView = computed(() => buildPillarView(PILLARS.GV, this.govControls()));
  /** Postura GV pela projeção ÚNICA (mesmo cabeçalho compartilhado das seis Funções). */
  readonly govPosture = signal<FunctionPosture | null>(null);
  readonly govPostureState = signal<'loading' | 'loaded' | 'notFound' | 'error'>('loading');

  // ---- Integração corporativa (sync sob demanda) ----
  /** Disponibilidade REAL reportada pelo servidor; `null` enquanto a leitura não chega. */
  integration = signal<DocumentIntegrationAvailability | null>(null);
  syncState = signal<SyncState>('idle');
  syncMessage = signal<string | null>(null);

  // ---- Hub de documentos ----
  /** Intervalo do polling controlado (ms) enquanto houver documento em análise. */
  private readonly POLL_MS = 2000;
  /**
   * Lista COMPLETA (sem filtro) — a autoridade do acompanhamento. Um filtro de status não pode esconder
   * um documento recém-enfileirado, então o polling e a detecção de transição operam sempre sobre o
   * conjunto todo; o filtro é aplicado só na VISÃO (client-side).
   */
  private readonly allDocs = signal<GovernanceDocument[]>([]);
  /** Visão exibida: a lista completa filtrada no cliente por tipo e status (o filtro nunca some com o polling). */
  readonly docs = computed(() => {
    const type = this.typeFilter();
    const status = this.statusFilter();
    return this.allDocs().filter(
      (d) => (!type || d.type === type) && (!status || d.analysisStatus === status),
    );
  });
  coverage = signal<GovernCoverage | null>(null);
  loading = signal(false);
  loadError = signal(false);
  busyId = signal<string | null>(null); // linha em ação (reanalyze/delete)
  expandedId = signal<string | null>(null); // linha com o parecer expandido (uma por vez)

  // ---- Filtros ----
  typeFilter = signal<GovernanceDocumentType | null>(null);
  statusFilter = signal<AiAnalysisStatus | null>(null);

  // ---- Formulário de upload ----
  uploadTitle = signal('');
  uploadType = signal<GovernanceDocumentType>('Politica');
  uploadFile = signal<File | null>(null);
  uploading = signal(false);
  uploadError = signal<string | null>(null);

  canUpload = computed(() => !this.uploading() && this.uploadFile() !== null);

  // Constantes de UI expostas ao template.
  protected readonly govMeta = PILLARS.GV; // subtítulo tático da Função Govern (mesmo texto dos painéis de pilar)
  protected readonly documentTypes = DOCUMENT_TYPES;
  protected readonly analysisStatuses = ANALYSIS_STATUSES;
  protected readonly typeLabel = documentTypeLabel;
  protected readonly sourceLabel = documentSourceLabel;
  protected readonly apiBase = environment.apiBase;

  ngOnInit(): void {
    this.loadIntegrationAvailability();
    this.loadGovernPosture();
    this.loadWorkspacePosture();
    this.loadCoverage();
    this.loadDocuments();
  }

  /**
   * Carga CENTRALIZADA do cabeçalho de postura GV pela projeção única, com estados explícitos. Chamada no
   * init, no retry e após TODA ação do Hub que possa mudar a avaliação (sync/upload/reanálise/remoção/cobertura).
   */
  loadWorkspacePosture(): void {
    this.govPostureState.set('loading');
    this.scoreSvc.fetchWorkspace().subscribe({
      next: (w) => {
        const f = functionOf(w, 'GV') ?? null;
        this.govPosture.set(f);
        this.govPostureState.set(f ? 'loaded' : 'notFound');
      },
      error: () => {
        this.govPosture.set(null);
        this.govPostureState.set('error');
      },
    });
  }

  /** Postura por telemetria: os controles GV (GV.SC/GV.RR), filtrados no ScoringService pelo prefixo "GV". */
  private loadGovernPosture(): void {
    this.scoringLoading.set(true);
    this.scoring.getPillarControls('GV').subscribe({
      next: (list) => {
        this.govControls.set(list);
        this.scoringLoading.set(false);
        this.scoringError.set(false);
      },
      error: () => {
        this.scoringError.set(true);
        this.scoringLoading.set(false);
      },
    });
  }

  /**
   * [AEGIS-MVP-PRODUCT-01] Disponibilidade REAL da ingestão documental. Falhar aqui NÃO libera o botão: sem
   * resposta, a tela fica no estado de verificação — prometer sincronização que o servidor vai recusar é
   * exatamente o que este pacote veio corrigir.
   */
  private loadIntegrationAvailability(): void {
    this.svc.documentIntegration().subscribe({
      next: (a) => this.integration.set(a),
      error: (err) => console.warn('Disponibilidade da integração documental indisponível:', err),
    });
  }

  /** Dispara a sincronização das fontes corporativas. 202 = agendado; a ingestão roda em background. */
  triggerSync(): void {
    if (this.syncState() === 'loading') return;
    this.syncState.set('loading');
    this.syncMessage.set(null);

    this.svc.syncPolicies().subscribe({
      next: (res) => {
        this.syncState.set('done');
        this.syncMessage.set(res.message || 'Sincronização agendada — os documentos aparecerão em instantes.');
        // Ingestão assíncrona (worker): recarrega a lista e os agregados pouco depois, para captar os novos.
        // A partir daí, se algum documento chegar ativo, o polling assume o acompanhamento até o término.
        setTimeout(() => {
          this.loadDocuments();
          this.refreshAggregates();
        }, 2500);
      },
      error: (err) => {
        console.error('Falha ao sincronizar as políticas corporativas:', err);
        this.syncState.set('error');
        // 409 = o servidor recusou porque NÃO há fonte documental — mensagem operacional, não técnica.
        this.syncMessage.set(
          err?.status === 409
            ? 'A sincronização automática não está disponível neste ambiente. Use o upload abaixo.'
            : 'Não foi possível agendar a sincronização agora. Tente novamente em alguns instantes.',
        );
      },
    });
  }

  /** Carrega a lista COMPLETA (sem filtro no servidor): o filtro é aplicado na visão, e o acompanhamento
   *  precisa enxergar todo documento — inclusive um recém-enfileirado que um filtro de status esconderia. */
  private loadDocuments(): void {
    this.loading.set(true);
    this.svc.listDocuments().subscribe({
      next: (docs) => {
        this.allDocs.set(docs);
        this.loading.set(false);
        this.loadError.set(false);
      },
      error: (err) => {
        console.error('Falha ao carregar os documentos de governança:', err);
        this.allDocs.set([]);
        this.loading.set(false);
        this.loadError.set(true);
      },
    });
  }

  /** Há documento em estado ativo (não terminal)? Enquanto sim, o polling continua. */
  private hasActiveDocs(): boolean {
    return this.allDocs().some((d) => isActiveAnalysisStatus(d.analysisStatus));
  }

  /**
   * Aplica uma leitura do polling. Detecta se algo que ESTAVA ativo chegou a estado terminal (Analisado/
   * Falha) — só então atualiza os agregados UMA vez. Uma leitura que apenas mantém tudo ativo não dispara
   * recomputo: o polling se paga sozinho.
   */
  private applyPolledList(list: GovernanceDocument[]): void {
    const wasActive = new Set(
      this.allDocs().filter((d) => isActiveAnalysisStatus(d.analysisStatus)).map((d) => d.id),
    );
    const byId = new Map(list.map((d) => [d.id, d]));
    const reachedTerminal = [...wasActive].some((id) => {
      const d = byId.get(id);
      return !d || !isActiveAnalysisStatus(d.analysisStatus); // sumiu ou virou terminal
    });

    this.allDocs.set(list);
    this.loadError.set(false);
    if (reachedTerminal) this.refreshAggregates();
  }

  /** Agregados dependentes da análise: cobertura + postura Govern (telemetria) + resumo do workspace.
   *  Chamado na CONCLUSÃO detectada pelo polling e na EXCLUSÃO — nunca por refresh completo da página. */
  private refreshAggregates(): void {
    this.loadCoverage();
    this.loadGovernPosture();
    this.loadWorkspacePosture();
  }

  /** Cobertura é best-effort: um erro aqui não derruba a tela de documentos. */
  private loadCoverage(): void {
    this.svc.getCoverage().subscribe({
      next: (cov) => this.coverage.set(cov),
      error: () => this.coverage.set(null),
    });
  }

  // Os filtros agem só na VISÃO (client-side sobre a lista completa) — não refazem a busca e, sobretudo,
  // não escondem do acompanhamento um documento recém-enfileirado.
  setType(value: string): void {
    this.typeFilter.set((value || null) as GovernanceDocumentType | null);
  }

  setStatus(value: string): void {
    this.statusFilter.set((value || null) as AiAnalysisStatus | null);
  }

  clearFilters(): void {
    this.typeFilter.set(null);
    this.statusFilter.set(null);
  }

  /** Spinner discreto só em estados ATIVOS visíveis: Na fila e Analisando (terminais nunca giram). */
  protected showsSpinner(status: AiAnalysisStatus): boolean {
    return status === 'Queued' || status === 'Processing';
  }

  /** Estado refinado de exibição (separa "Analisado com evidência" de "sem evidência"). */
  protected displayState(d: GovernanceDocument): DocumentDisplayState {
    return documentDisplayState(d);
  }

  /** Rótulo PT do estado refinado (para a pill de status e o badge do parecer). */
  protected displayLabel(d: GovernanceDocument): string {
    return documentDisplayStateLabel(documentDisplayState(d));
  }

  /** Classe CSS derivada do estado refinado (st-analyzedwithevidence, st-failed…). */
  protected displayStateClass(d: GovernanceDocument): string {
    return documentDisplayState(d).toLowerCase();
  }

  /** True se o documento tem ao menos um trecho probatório literal. */
  protected hasEvidence(d: GovernanceDocument): boolean {
    return hasProbativeEvidence(d);
  }

  /** Percentual inteiro da confiança (0..1 → 0..100), para o parecer. */
  protected pct(confidence: number): number {
    return Math.round((confidence ?? 0) * 100);
  }

  /** Mensagem amigável da falha (traduz a categoria; nunca exibe o nome da exceção .NET). */
  protected errorLabel(category: string | null): string {
    return analysisErrorLabel(category);
  }

  /** Alterna a linha de parecer expandida (uma por vez). */
  toggleExpand(id: string): void {
    this.expandedId.update((cur) => (cur === id ? null : id));
  }

  /** Extensões aceitas — espelham os extratores do backend (PDF, texto e DOCX). O backend é a autoridade. */
  private readonly supportedExtensions = ['pdf', 'txt', 'md', 'csv', 'json', 'docx'];

  private isSupported(file: File): boolean {
    const ext = file.name.split('.').pop()?.toLowerCase() ?? '';
    return this.supportedExtensions.includes(ext);
  }

  /** Mensagem amigável da falha de upload (422 = formato, com o texto do backend; 409 = duplicado; senão genérica). */
  private uploadErrorMessage(err: { status?: number; error?: unknown }): string {
    if (err?.status === 409) return 'Documento idêntico já ingerido (mesmo hash) neste cliente.';
    if (err?.status === 422) {
      return typeof err.error === 'string' && err.error.trim()
        ? err.error
        : 'Formato não suportado. Envie PDF, TXT ou DOCX.';
    }
    return 'Não foi possível enviar o documento agora. Tente novamente em alguns instantes.';
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;

    // Recusa CLARA de formato antes do upload (o backend também recusa com 422). Evita a experiência de
    // enviar um .doc/.docm e só descobrir a falha depois, na fila.
    if (file && !this.isSupported(file)) {
      this.uploadError.set('Formato não suportado. Envie PDF, TXT ou DOCX.');
      this.uploadFile.set(null);
      input.value = '';
      return;
    }

    this.uploadError.set(null);
    this.uploadFile.set(file);
    // Sugere o título a partir do nome do arquivo, se ainda estiver vazio.
    if (file && !this.uploadTitle().trim()) {
      this.uploadTitle.set(file.name.replace(/\.[^.]+$/, ''));
    }
  }

  submitUpload(): void {
    const file = this.uploadFile();
    if (!file) return;

    const title = this.uploadTitle().trim() || file.name;
    this.uploading.set(true);
    this.uploadError.set(null);

    this.svc.uploadDocument(file, title, this.uploadType()).subscribe({
      next: () => {
        this.uploading.set(false);
        this.resetUploadForm();
        // Mostra o documento IMEDIATAMENTE (entra como "Na fila"); o polling assume daqui e atualiza os
        // agregados quando a análise concluir. Sem refresh completo da página.
        this.loadDocuments();
      },
      error: (err) => {
        console.error('Falha no upload do documento:', err);
        this.uploading.set(false);
        this.uploadError.set(this.uploadErrorMessage(err));
      },
    });
  }

  reanalyze(id: string): void {
    this.busyId.set(id);
    this.svc.reanalyzeDocument(id).subscribe({
      next: () => {
        this.busyId.set(null);
        // Volta a "Na fila" imediatamente; o polling acompanha até o término e então atualiza os agregados.
        this.loadDocuments();
      },
      error: (err) => {
        console.error('Falha ao reenviar o documento para análise:', err);
        this.busyId.set(null);
      },
    });
  }

  remove(doc: GovernanceDocument): void {
    if (!confirm(`Excluir "${doc.title}" e seus controles NIST associados? Esta ação não pode ser desfeita.`)) return;
    this.busyId.set(doc.id);
    this.svc.deleteDocument(doc.id).subscribe({
      next: () => {
        this.busyId.set(null);
        // A exclusão dispara a reconciliação no backend (retração): atualiza lista E agregados de uma vez.
        this.loadDocuments();
        this.refreshAggregates();
      },
      error: (err) => {
        console.error('Falha ao excluir o documento:', err);
        this.busyId.set(null);
      },
    });
  }

  private resetUploadForm(): void {
    this.uploadTitle.set('');
    this.uploadType.set('Politica');
    this.uploadFile.set(null);
  }
}
