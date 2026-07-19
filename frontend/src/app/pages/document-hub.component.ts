import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { GovernanceService } from '../services/governance.service';
import {
  ANALYSIS_STATUSES,
  AiAnalysisStatus,
  DOCUMENT_TYPES,
  GovernCoverage,
  GovernanceDocument,
  GovernanceDocumentType,
  analysisStatusLabel,
  documentTypeLabel,
} from '../models/governance.models';
import { environment } from '../../environments/environment';
import { AgentStateService } from '../services/agent-state.service';
import { ScoreGaugeComponent } from '../components/scoring/score-gauge.component';
import { ControlComplianceCardComponent } from '../components/scoring/control-compliance-card.component';
import { AegisPillarChecklistComponent } from '../components/scoring/aegis-pillar-checklist.component';
import { ScoringService } from '../services/scoring.service';
import { PILLARS, TenantControlStateDto, buildPillarView } from '../models/scoring.models';

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
  imports: [DatePipe, ScoreGaugeComponent, ControlComplianceCardComponent, AegisPillarChecklistComponent],
  template: `
    <div class="app">
      <header class="topbar">
        <div class="brand">
          <span class="mark">Central <b>de Governança</b></span>
          <span class="sub">NIST CSF 2.0 · Govern (GV)</span>
        </div>
        <div class="client">
          <span class="label">Cobertura GV</span>
          <span class="name">
            @if (coverage(); as cov) {
              {{ cov.coveredPct }}%
            } @else {
              —
            }
          </span>
        </div>
      </header>

      <!-- Subtítulo tático da Função Govern (mesmo texto dos painéis de pilar) -->
      <p class="gov-description">{{ govMeta.description }}</p>

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
            Não foi possível carregar a postura de governança. Verifique a API em <code>{{ apiBase }}</code>.
          </p>
        } @else {
          <div class="gs-grid">
            <div class="gs-left">
              <app-score-gauge [percent]="govView().compliancePct" caption="GV CONFORME" />
              <div class="gs-counts">
                <span class="c">{{ govView().total }} controles avaliados</span>
                <span class="c ok">{{ govView().compliant }} conformes</span>
                @if (govView().partial > 0) {
                  <span class="c partial">{{ govView().partial }} parciais</span>
                }
                <span class="c fail" [class.hot]="govView().nonCompliant > 0">
                  {{ govView().nonCompliant }} não conformes
                </span>
              </div>
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
      <section class="panel integration">
        <p class="eyebrow">Integração Corporativa</p>
        <div class="int-row">
          <p class="int-copy">
            Puxe as políticas das fontes corporativas conectadas (SharePoint, Google Workspace…) para
            leitura automática pela IA — sem upload manual.
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
      </section>

      <!-- ---- Upload manual ---- -->
      <section class="panel uploader">
        <p class="eyebrow">Upload Manual</p>
        <div class="up-row">
          <label class="up-field">
            <span>Arquivo</span>
            <input type="file" (change)="onFileSelected($event)" />
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
            {{ uploading() ? 'Enviando…' : 'Enviar para leitura da IA' }}
          </button>
        </div>

        @if (uploadError()) {
          <p class="up-error">{{ uploadError() }}</p>
        }
      </section>

      <!-- ---- Filtros ---- -->
      <section class="panel filters">
        <div class="controls">
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
            <span>Status IA</span>
            <select [value]="statusFilter() ?? ''" (change)="setStatus($any($event.target).value)">
              <option value="">Todos</option>
              @for (s of analysisStatuses; track s.value) {
                <option [value]="s.value">{{ s.label }}</option>
              }
            </select>
          </label>

          @if (typeFilter() || statusFilter()) {
            <button type="button" class="clear" (click)="clearFilters()">Limpar</button>
          }
        </div>
      </section>

      <!-- ---- Estado de erro ---- -->
      @if (loadError()) {
        <div class="notice">
          <b>Falha ao carregar os documentos.</b> A API em <code>{{ apiBase }}</code> não respondeu —
          confira o endereço e o <code>tenantId</code>. O console traz o erro completo.
        </div>
      }

      <!-- ============ 3) HUB — documentos ingeridos ============ -->
      <section class="panel table-wrap">
        <table class="doc-table">
          <thead>
            <tr>
              <th>Documento</th>
              <th>Tipo</th>
              <th>Origem</th>
              <th>Status IA</th>
              <th class="num">Mapeamentos</th>
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
                <td><span class="src">{{ d.source }}</span></td>
                <td>
                  <span class="ai-status ai-{{ d.analysisStatus.toLowerCase() }}">
                    {{ statusLabel(d.analysisStatus) }}
                  </span>
                  @if (d.analysisStatus === 'Failed' && d.analysisError) {
                    <div class="ai-err" [title]="d.analysisError">{{ d.analysisError }}</div>
                  }
                </td>
                <td class="num"><span class="map-count">{{ d.mappings.length }}</span></td>
                <td class="dim">{{ d.analyzedAt ? (d.analyzedAt | date: 'dd/MM/yy HH:mm') : '—' }}</td>
                <td class="num">
                  <div class="row-actions">
                    <button type="button" class="act" (click)="reanalyze(d.id)" [disabled]="busyId() === d.id">
                      Reanalisar
                    </button>
                    <button type="button" class="act danger" (click)="remove(d)" [disabled]="busyId() === d.id">
                      Excluir
                    </button>
                  </div>
                </td>
              </tr>
            } @empty {
              <tr class="empty">
                <td colspan="7">
                  @if (loading()) {
                    Carregando documentos…
                  } @else {
                    Nenhum documento ingerido ainda. Sincronize as fontes ou envie o primeiro acima.
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
      /* Subtítulo tático da Função Govern (logo abaixo da topbar). O -14px encurta parte do
         margin-bottom:30px da topbar global, colando o texto ao título; sans + mutado + contido, para
         informar sem disputar atenção com o gauge e os cards de controle. */
      .gov-description {
        color: var(--muted);
        font-family: var(--sans);
        font-size: 13.5px;
        line-height: 1.6;
        margin: -14px 0 24px;
        max-width: 820px;
      }

      /* ---- 1) Postura de Governança (telemetria GV) ---- */
      .gov-score { padding: 20px 22px; margin-bottom: 18px; }
      .gov-score .hd { display: flex; align-items: baseline; justify-content: space-between; margin-bottom: 16px; gap: 12px; }
      .gov-score h3 { margin: 0; font-size: 14px; font-weight: 600; position: relative; padding-left: 13px; }
      .gov-score h3::before {
        content: ''; position: absolute; left: 0; top: 2px; bottom: 2px; width: 3px; border-radius: 2px;
        background: var(--neon); box-shadow: 0 0 10px rgba(38, 224, 255, 0.6);
      }
      .gov-score .hint { font-family: var(--mono); font-size: 11px; color: var(--muted); }
      .gs-grid { display: grid; grid-template-columns: 300px 1fr; gap: 18px; align-items: start; }
      .gs-left { display: flex; flex-direction: column; gap: 12px; }
      .gs-counts { display: flex; flex-direction: column; gap: 8px; }
      .gs-counts .c { font-family: var(--mono); font-size: 12px; color: var(--muted); display: inline-flex; align-items: center; gap: 8px; }
      .gs-counts .c::before { content: ''; width: 8px; height: 8px; border-radius: 50%; background: currentColor; opacity: 0.7; }
      .gs-counts .c.ok { color: var(--cyan); }
      .gs-counts .c.partial { color: var(--amber); }
      .gs-counts .c.fail.hot { color: var(--red); }
      .gs-counts .c.fail.hot::before { box-shadow: 0 0 8px 0 var(--red); opacity: 1; }
      .score-err { font-family: var(--mono); font-size: 12px; color: var(--red); margin: 0; }
      .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); letter-spacing: 0.08em; animation: hub-pulse 1.4s ease-in-out infinite; }
      @keyframes hub-pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 0.75; } }

      .coverage-strip { display: flex; gap: 30px; padding: 16px 20px; margin-bottom: 18px; }
      .cov-metric { display: flex; flex-direction: column; gap: 4px; }
      .cov-k { font-family: var(--mono); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.12em; color: var(--muted); }
      .cov-v { font-family: var(--display); font-weight: 700; font-size: 22px; color: var(--text); }
      .cov-v.ok { color: var(--cyan); }
      .cov-v.warn { color: var(--amber); }

      /* ---- 2) Integração Corporativa ---- */
      .integration { padding: 18px 20px; margin-bottom: 18px; }
      .integration .eyebrow { margin-bottom: 14px; }
      .int-row { display: flex; align-items: center; justify-content: space-between; gap: 20px; flex-wrap: wrap; }
      .int-copy { margin: 0; font-size: 12.5px; color: var(--muted); line-height: 1.55; max-width: 560px; }
      .sync-btn { display: inline-flex; align-items: center; gap: 9px; white-space: nowrap; }
      .spin { width: 13px; height: 13px; border: 2px solid rgba(5, 7, 15, 0.35); border-top-color: #05070f; border-radius: 50%; animation: hub-spin 0.7s linear infinite; }
      @keyframes hub-spin { to { transform: rotate(360deg); } }
      .int-ok { font-family: var(--mono); font-size: 12px; color: var(--cyan); margin: 12px 0 0; }
      .int-err { font-family: var(--mono); font-size: 12px; color: var(--red); margin: 12px 0 0; }

      .uploader { padding: 18px 20px; margin-bottom: 18px; }
      .uploader .eyebrow { margin-bottom: 14px; }
      .up-row { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 14px; }
      .up-field { display: flex; flex-direction: column; gap: 6px; font-family: var(--mono); font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.1em; }
      .up-field input[type='text'], .up-field select {
        font-family: var(--sans); font-size: 13px; color: var(--text); text-transform: none; letter-spacing: 0;
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 9px; padding: 8px 11px; min-width: 240px;
      }
      .up-field input[type='file'] { font-family: var(--sans); font-size: 12px; color: var(--muted); text-transform: none; letter-spacing: 0; }
      .up-field input:focus, .up-field select:focus { outline: none; border-color: rgba(38, 224, 255, 0.5); }
      .up-error { font-family: var(--mono); font-size: 11.5px; color: var(--red); margin: 12px 0 0; }

      .btn {
        font-family: var(--mono); font-size: 12px; color: var(--text);
        background: var(--panel-2); border: 1px solid var(--line); border-radius: 9px; padding: 9px 16px; cursor: pointer; transition: 0.15s;
      }
      .btn:hover:not(:disabled) { border-color: rgba(38, 224, 255, 0.5); }
      .btn:disabled { opacity: 0.4; cursor: not-allowed; }
      .btn.primary { color: #05070f; font-weight: 600; border-color: transparent; background: var(--neon-h); box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6); }

      .filters { padding: 14px 20px; margin-bottom: 18px; }
      .controls { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; }
      .ctl { display: inline-flex; align-items: center; gap: 8px; font-family: var(--mono); font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.1em; }
      .ctl select { font-family: var(--sans); font-size: 13px; color: var(--text); text-transform: none; letter-spacing: 0; background: var(--panel-2); border: 1px solid var(--line); border-radius: 9px; padding: 7px 10px; }
      .ctl select:focus { outline: none; border-color: rgba(38, 224, 255, 0.5); }
      .clear { font-family: var(--mono); font-size: 11px; color: var(--magenta); background: none; border: 1px solid rgba(255, 61, 154, 0.3); border-radius: 9px; padding: 7px 12px; cursor: pointer; }

      .table-wrap { padding: 6px 8px; overflow-x: auto; }
      table.doc-table { width: 100%; border-collapse: collapse; font-size: 13px; }
      table.doc-table thead th {
        font-family: var(--mono); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.12em;
        color: var(--muted); text-align: left; font-weight: 500; padding: 12px 14px; border-bottom: 1px solid var(--line);
      }
      table.doc-table th.num, table.doc-table td.num { text-align: center; }
      table.doc-table tbody td { padding: 13px 14px; border-bottom: 1px solid var(--line-2); vertical-align: middle; }
      table.doc-table tbody tr:hover td { background: rgba(38, 224, 255, 0.03); }
      .doc-title { font-weight: 600; color: var(--text); }
      .doc-sub { font-family: var(--mono); font-size: 11px; color: var(--muted); margin-top: 2px; }
      .tag { font-family: var(--mono); font-size: 11.5px; color: var(--cyan-2); }
      .src { font-family: var(--mono); font-size: 11px; color: var(--muted); }
      .dim { color: var(--muted); font-family: var(--mono); font-size: 11.5px; }
      .map-count { font-family: var(--display); font-weight: 700; font-size: 13px; color: var(--text); }

      .ai-status { font-family: var(--mono); font-size: 11px; padding: 4px 10px; border-radius: 999px; border: 1px solid currentColor; }
      .ai-pending { color: var(--muted); }
      .ai-queued { color: var(--cyan-2); }
      .ai-processing { color: var(--amber); }
      .ai-analyzed { color: var(--cyan); }
      .ai-failed { color: var(--red); }
      .ai-err { font-family: var(--mono); font-size: 10.5px; color: var(--red); margin-top: 4px; max-width: 220px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

      .row-actions { display: inline-flex; gap: 6px; }
      .act { font-family: var(--mono); font-size: 11px; color: var(--text); background: var(--panel-2); border: 1px solid var(--line); border-radius: 8px; padding: 6px 11px; cursor: pointer; transition: 0.15s; }
      .act:hover:not(:disabled) { border-color: rgba(38, 224, 255, 0.5); }
      .act.danger { color: var(--magenta); border-color: rgba(255, 61, 154, 0.3); }
      .act:disabled { opacity: 0.4; cursor: not-allowed; }

      tr.empty td { text-align: center; color: var(--muted); font-family: var(--mono); font-size: 12px; padding: 30px; }

      @media (max-width: 900px) { .gs-grid { grid-template-columns: 1fr; } }
      @media (max-width: 720px) {
        .up-field input[type='text'], .up-field select { min-width: 160px; }
      }
      @media (prefers-reduced-motion: reduce) { .pulse, .spin { animation: none; } }
    `,
  ],
})
export class DocumentHubComponent implements OnInit {
  private readonly svc = inject(GovernanceService);
  private readonly scoring = inject(ScoringService);
  protected readonly agent = inject(AgentStateService);

  constructor() {
    // O Auditor vive no App (global). Quando uma entrevista altera a cobertura, o AgentStateService
    // sinaliza e recarregamos o strip de cobertura documental desta tela.
    effect(() => {
      if (this.agent.coverageVersion() > 0) this.loadCoverage();
    });
  }

  // ---- Postura de Governança (telemetria GV.SC / GV.RR) ----
  private readonly govControls = signal<TenantControlStateDto[]>([]);
  scoringLoading = signal(true);
  scoringError = signal(false);
  /** Reusa o mesmo agregador dos painéis de pilar (DRY): pct, contagens e ordenação por risco. */
  readonly govView = computed(() => buildPillarView(PILLARS.GV, this.govControls()));

  // ---- Integração corporativa (sync sob demanda) ----
  syncState = signal<SyncState>('idle');
  syncMessage = signal<string | null>(null);

  // ---- Hub de documentos ----
  docs = signal<GovernanceDocument[]>([]);
  coverage = signal<GovernCoverage | null>(null);
  loading = signal(false);
  loadError = signal(false);
  busyId = signal<string | null>(null); // linha em ação (reanalyze/delete)

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
  protected readonly statusLabel = analysisStatusLabel;
  protected readonly apiBase = environment.apiBase;

  ngOnInit(): void {
    this.loadGovernPosture();
    this.loadCoverage();
    this.loadDocuments();
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

  /** Dispara a sincronização das fontes corporativas. 202 = agendado; a ingestão roda em background. */
  triggerSync(): void {
    if (this.syncState() === 'loading') return;
    this.syncState.set('loading');
    this.syncMessage.set(null);

    this.svc.syncPolicies().subscribe({
      next: (res) => {
        this.syncState.set('done');
        this.syncMessage.set(res.message || 'Sincronização agendada — os documentos aparecerão em instantes.');
        // Ingestão assíncrona (worker): recarrega a lista e a postura pouco depois, para captar os novos.
        setTimeout(() => {
          this.loadDocuments();
          this.loadGovernPosture();
        }, 2500);
      },
      error: (err) => {
        console.error('Falha ao sincronizar as políticas corporativas:', err);
        this.syncState.set('error');
        this.syncMessage.set('Não foi possível agendar a sincronização. Verifique a API e tente novamente.');
      },
    });
  }

  private loadDocuments(): void {
    this.loading.set(true);
    this.svc
      .listDocuments({
        type: this.typeFilter() ?? undefined,
        analysisStatus: this.statusFilter() ?? undefined,
      })
      .subscribe({
        next: (docs) => {
          this.docs.set(docs);
          this.loading.set(false);
          this.loadError.set(false);
        },
        error: (err) => {
          console.error('Falha ao carregar os documentos de governança:', err);
          this.docs.set([]);
          this.loading.set(false);
          this.loadError.set(true);
        },
      });
  }

  /** Cobertura é best-effort: um erro aqui não derruba a tela de documentos. */
  private loadCoverage(): void {
    this.svc.getCoverage().subscribe({
      next: (cov) => this.coverage.set(cov),
      error: () => this.coverage.set(null),
    });
  }

  setType(value: string): void {
    this.typeFilter.set((value || null) as GovernanceDocumentType | null);
    this.loadDocuments();
  }

  setStatus(value: string): void {
    this.statusFilter.set((value || null) as AiAnalysisStatus | null);
    this.loadDocuments();
  }

  clearFilters(): void {
    this.typeFilter.set(null);
    this.statusFilter.set(null);
    this.loadDocuments();
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
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
        this.loadDocuments();
      },
      error: (err) => {
        console.error('Falha no upload do documento:', err);
        this.uploading.set(false);
        this.uploadError.set(
          err?.status === 409
            ? 'Documento idêntico já ingerido (mesmo hash) neste cliente.'
            : 'Não foi possível enviar o documento. Verifique a API e tente novamente.',
        );
      },
    });
  }

  reanalyze(id: string): void {
    this.busyId.set(id);
    this.svc.reanalyzeDocument(id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.loadDocuments();
      },
      error: (err) => {
        console.error('Falha ao re-enfileirar a leitura da IA:', err);
        this.busyId.set(null);
      },
    });
  }

  remove(doc: GovernanceDocument): void {
    if (!confirm(`Excluir "${doc.title}" e seus mapeamentos? Esta ação não pode ser desfeita.`)) return;
    this.busyId.set(doc.id);
    this.svc.deleteDocument(doc.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.loadDocuments();
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
