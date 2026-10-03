import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  KnightAssessment,
  KnightControlRef,
  KnightManualResult,
  KnightManualResultValue,
  KnightReferenceControlStatus,
  KnightReferenceCoverage,
  KnightReferenceFilter,
  KnightReferenceView,
  MANUAL_RESULT_OPTIONS,
  RecordKnightManualResultRequest,
  SeverityLevel,
  controlRefs,
  filterReferenceControls,
  manualResultBadge,
  manualResultProblems,
  referenceLabel,
  severityLabel,
} from '../../models/knight.models';
import { GovernanceDocument } from '../../models/governance.models';
import { canManageActionPlans } from '../../models/remediation.models';
import { AuthService } from '../../services/auth.service';
import { GovernanceService } from '../../services/governance.service';
import { KnightService } from '../../services/knight.service';
import { KnightControlRefsComponent } from './knight-control-refs.component';

const PAGE = 25;

const VIEWS: { value: KnightReferenceView; label: string }[] = [
  { value: 'manual', label: 'Sem avaliação automatizada (aceita resultado manual)' },
  { value: 'withManual', label: 'Com resultado manual registrado' },
  { value: 'preview', label: 'Avaliadas com leitura em versão preview' },
  { value: 'automated', label: 'Avaliadas (integral ou parcial)' },
  { value: 'all', label: 'Todos os controles de referência' },
];

/**
 * [AEGIS-KNIGHT-CLOSURE-01] Aba "Cobertura e verificação manual": os controles de referência do catálogo, um a um, com a
 * situação no produto (avaliado, parcial, sem método, manual, outro acesso), a versão preview das leituras quando houver e,
 * para os que NÃO têm avaliação automatizada, o resultado de verificação manual (atestação) do cliente — resultado,
 * justificativa, responsável e evidência (referência ou documento da Central de Evidências). O resultado manual é sempre
 * exibido à parte: não entra na nota, na cobertura desta avaliação nem na aprovação.
 */
@Component({
  selector: 'app-knight-coverage',
  standalone: true,
  imports: [DatePipe, FormsModule, RouterLink, KnightControlRefsComponent],
  template: `
    <div class="panel">
      <div class="hd"><h3>Controles de referência e verificação manual</h3></div>
      <p class="muted small">
        Cada controle de referência ({{ coverage()?.frameworks?.join(' · ') }}) com a situação no produto. Os que não têm avaliação
        automatizada — critério organizacional, sem método publicado na API ou acesso que o conector não tem — podem receber um
        <b>resultado de verificação manual</b>: uma atestação com justificativa, responsável e evidência. Ele aparece à parte nesta
        tela e nos relatórios publicados e <b>não</b> entra na nota, na cobertura desta avaliação nem na aprovação.
      </p>
      @if (!coverage()) {
        <p class="muted">{{ state() === 'error' ? 'A cobertura do catálogo não pôde ser carregada agora.' : 'Carregando…' }}</p>
      } @else {
        <div class="filter-row" role="search">
          <label class="fl">
            <span class="filter-label">Recorte</span>
            <select [ngModel]="filter().view" (ngModelChange)="setFilter('view', $event)" name="view">
              @for (v of views; track v.value) { <option [value]="v.value">{{ v.label }}</option> }
            </select>
          </label>
          <label class="fl">
            <span class="filter-label">Plataforma</span>
            <select [ngModel]="filter().platform" (ngModelChange)="setFilter('platform', $event)" name="platform">
              <option value="">Todas</option>
              @for (p of platforms(); track p) { <option [value]="p">{{ p }}</option> }
            </select>
          </label>
          <label class="fl grow">
            <span class="filter-label">Pesquisa</span>
            <input type="search" [ngModel]="filter().q" (ngModelChange)="setFilter('q', $event)" name="q"
                   placeholder="Código da referência, título, serviço, AK-…" />
          </label>
        </div>
        <p class="muted small" role="status" aria-live="polite">
          {{ rows().length }} de {{ coverage()!.controls.length }} controle(s) de referência neste recorte ·
          {{ manualCount() }} com resultado manual vigente.
        </p>
        @if (message(); as msg) { <p class="ok small" role="status">{{ msg }}</p> }

        @for (c of shown(); track c.key) {
          <article class="ref" [attr.data-key]="c.key">
            <div class="ref-hd">
              <code class="ref-code">{{ label(c) }}</code>
              <span [class]="'badge disp ' + c.disposition">{{ c.dispositionLabel }}</span>
              @if ((c.previewApis ?? []).length) { <span class="badge preview" title="{{ (c.previewApis ?? []).join(' · ') }}">versão preview</span> }
              @if (badge(c.manualResult); as b) { <span class="badge manual" [class.expired]="c.manualResult?.expired">{{ b }}</span> }
            </div>
            <p class="ref-title">{{ c.title }}</p>
            <p class="meta">{{ c.serviceLabel }}@if (c.platform !== c.serviceLabel) { · {{ c.platform }} } · severidade {{ sev(c.severity) }}</p>
            @if (c.note) { <p class="meta note">{{ c.note }}</p> }
            @if ((c.previewApis ?? []).length) {
              <p class="meta">Leitura em versão preview: {{ (c.previewApis ?? []).join(' · ') }}. Mudança de contrato vira limitação, nunca aprovação.</p>
            }
            @if (c.indicatorIds.length && assessment(); as a) {
              <app-knight-control-refs [refs]="refs(c.indicatorIds, a)" (open)="open.emit($event)" />
            }

            @if (c.manualEligible) {
              <div class="manual">
                @if (c.manualResult; as m) {
                  <dl class="mr">
                    <dt>Resultado manual</dt><dd>{{ m.resultLabel }}@if (m.expired) { <b class="warn"> — vencido</b> }@if (m.validUntil) { · válido até {{ m.validUntil | date: 'dd/MM/yyyy' }} }</dd>
                    <dt>Justificativa</dt><dd class="pre">{{ m.justification }}</dd>
                    <dt>Responsável</dt><dd>{{ m.responsibleName }}</dd>
                    <dt>Evidência</dt><dd>{{ evidenceText(m) }}</dd>
                    <dt>Registro</dt><dd>{{ m.recordedByName || '—' }} em {{ m.recordedAt | date: 'dd/MM/yyyy HH:mm' }} · catálogo {{ m.catalogVersion }}</dd>
                  </dl>
                } @else {
                  <p class="meta">Sem resultado manual registrado.</p>
                }
                <div class="actions">
                  @if (canManage()) {
                    <button type="button" class="btn-sm" (click)="startEdit(c)" [disabled]="editing() === c.key">
                      {{ c.manualResult ? 'Atualizar resultado manual' : 'Registrar resultado manual' }}
                    </button>
                  }
                  <button type="button" class="btn-sm ghost" (click)="toggleHistory(c.key)">
                    {{ historyKey() === c.key ? 'Ocultar histórico' : 'Histórico' }}
                  </button>
                </div>
                @if (!canManage()) { <p class="meta">Registrar resultado manual exige o papel Manager ou TenantAdmin.</p> }

                @if (historyKey() === c.key) {
                  <div class="history" role="region" aria-label="Histórico de resultados manuais">
                    @if (historyState() === 'loading') { <p class="meta">Carregando…</p> }
                    @else if (historyState() === 'error') { <p class="meta warn">O histórico não pôde ser carregado agora.</p> }
                    @else if (history().length === 0) { <p class="meta">Nenhum registro.</p> }
                    @else {
                      <ol>
                        @for (h of history(); track h.id) {
                          <li>
                            <b>{{ h.resultLabel }}</b> — {{ h.responsibleName }} · {{ h.recordedAt | date: 'dd/MM/yyyy HH:mm' }}
                            por {{ h.recordedByName || '—' }}<br />
                            <span class="meta pre">{{ h.justification }}</span>
                            @if (evidenceText(h) !== '—') { <span class="meta"> · Evidência: {{ evidenceText(h) }}</span> }
                          </li>
                        }
                      </ol>
                    }
                  </div>
                }

                @if (editing() === c.key) {
                  <form class="mform" (ngSubmit)="submit(c)" aria-label="Resultado de verificação manual">
                    <div class="grid">
                      <label class="fl">
                        <span class="filter-label">Resultado</span>
                        <select [(ngModel)]="draft.result" name="result">
                          @for (o of resultOptions; track o.value) { <option [value]="o.value">{{ o.label }}</option> }
                          @if (c.manualResult) { <option value="Withdrawn">Retirar o resultado vigente</option> }
                        </select>
                      </label>
                      <label class="fl">
                        <span class="filter-label">Responsável</span>
                        <input type="text" [(ngModel)]="draft.responsibleName" name="responsible" maxlength="200" />
                      </label>
                      <label class="fl">
                        <span class="filter-label">Válido até (opcional)</span>
                        <input type="date" [(ngModel)]="draft.validUntil" name="validUntil" [min]="today" [disabled]="draft.result === 'Withdrawn'" />
                      </label>
                    </div>
                    <label class="fl wide">
                      <span class="filter-label">Justificativa — o que foi verificado e como</span>
                      <textarea rows="3" [(ngModel)]="draft.justification" name="justification" maxlength="2000"></textarea>
                    </label>
                    @if (draft.result !== 'Withdrawn') {
                      <div class="grid">
                        <label class="fl">
                          <span class="filter-label">Referência da evidência (chamado, registro, ata)</span>
                          <input type="text" [(ngModel)]="draft.evidenceReference" name="evidenceReference" maxlength="500" />
                        </label>
                        <label class="fl">
                          <span class="filter-label">Ou um documento da Central de Evidências</span>
                          <select [(ngModel)]="draft.evidenceDocumentId" name="evidenceDocumentId">
                            <option [ngValue]="null">— nenhum —</option>
                            @for (d of documents(); track d.id) { <option [ngValue]="d.id">{{ d.title }}</option> }
                          </select>
                          <em class="hint">
                            @if (documentsState() === 'error') { Os documentos não puderam ser carregados agora. }
                            @else if (documentsState() === 'ok' && documents().length === 0) { Nenhum documento enviado ainda. }
                            Envie o documento em <a routerLink="/nist/gv/documentos">Biblioteca de documentos do AEGIS NIST</a>; o título e o SHA-256 ficam gravados no registro.
                          </em>
                        </label>
                      </div>
                    }
                    @if (problems().length) {
                      <ul class="problems" role="alert">@for (p of problems(); track p) { <li>{{ p }}</li> }</ul>
                    }
                    <div class="actions">
                      <button type="submit" class="btn-sm primary" [disabled]="saving()">{{ saving() ? 'Salvando…' : 'Salvar resultado manual' }}</button>
                      <button type="button" class="btn-sm ghost" (click)="cancel()" [disabled]="saving()">Cancelar</button>
                    </div>
                  </form>
                }
              </div>
            }
          </article>
        }
        @if (rows().length > shown().length) {
          <button type="button" class="btn-sm ghost more" (click)="limit.set(limit() + page)">
            Mostrar mais {{ Math.min(page, rows().length - shown().length) }} de {{ rows().length - shown().length }} restantes
          </button>
        }
      }
    </div>
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; gap: var(--sp-4); }
      .small, .meta { font-size: var(--fs-meta); }
      .meta { margin: 2px 0 0; color: var(--text-2); overflow-wrap: anywhere; }
      .filter-row { display: flex; flex-wrap: wrap; gap: var(--sp-3); margin: var(--sp-2) 0; }
      .fl { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
      .fl.grow { flex: 1 1 240px; }
      .fl.wide { margin-top: var(--sp-2); }
      .fl input, .fl select, .fl textarea { min-height: var(--control-h); padding: 0 10px; border: 1px solid var(--line-strong);
        border-radius: var(--radius-sm); background: var(--panel-2); color: var(--text); max-width: 100%; }
      .fl textarea { padding: 8px 10px; resize: vertical; font: inherit; }
      .fl select { max-width: min(100%, 420px); }
      .fl input:focus-visible, .fl select:focus-visible, .fl textarea:focus-visible { outline: none; box-shadow: var(--field-focus); border-color: var(--cyan); }
      .ref { padding: var(--sp-3) 0; border-top: 1px solid var(--line); min-width: 0; }
      .ref-hd { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
      .ref-code { font-size: var(--fs-meta); color: var(--text-2); overflow-wrap: anywhere; }
      .ref-title { margin: 4px 0 0; font-weight: 600; overflow-wrap: anywhere; }
      .badge { padding: 1px 8px; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; background: var(--panel-2); color: var(--text-2); }
      .badge.Implemented { color: var(--green, #2e7d32); }
      .badge.Partial { color: var(--amber, #b26a00); }
      .badge.preview { color: var(--cyan); background: var(--tint-cyan); }
      .badge.manual { color: var(--violet, #6a3fb5); border: 1px solid currentColor; background: transparent; }
      .badge.manual.expired, .warn { color: var(--red, #c62828); }
      .note { font-style: italic; }
      .manual { margin-top: var(--sp-2); padding: var(--sp-2) var(--sp-3); border-left: 3px solid var(--line-strong); background: var(--panel-2); border-radius: var(--radius-sm); }
      .mr { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 2px var(--sp-3); margin: 0; font-size: var(--fs-sm); }
      .mr dt { color: var(--text-2); }
      .mr dd { margin: 0; overflow-wrap: anywhere; }
      .pre { white-space: pre-line; }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); margin-top: var(--sp-2); }
      .btn-sm { min-height: 32px; padding: 0 12px; border-radius: var(--radius-sm); border: 1px solid var(--line-strong); background: var(--panel); color: var(--text); cursor: pointer; }
      .btn-sm.primary { background: var(--cyan); border-color: var(--cyan); color: var(--on-accent, #fff); }
      .btn-sm.ghost { background: transparent; }
      .btn-sm:disabled { opacity: .6; cursor: default; }
      .history ol { margin: var(--sp-2) 0 0; padding-left: 1.2em; font-size: var(--fs-sm); }
      .history li { margin-bottom: 4px; overflow-wrap: anywhere; }
      .mform { margin-top: var(--sp-3); }
      .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: var(--sp-3); margin-top: var(--sp-2); }
      .hint { font-style: normal; font-size: var(--fs-meta); color: var(--text-2); }
      .problems { margin: var(--sp-2) 0 0; padding-left: 1.2em; color: var(--red, #c62828); font-size: var(--fs-sm); }
      .ok { color: var(--green, #2e7d32); }
      .more { margin-top: var(--sp-3); }
      @media (max-width: 480px) {
        .mr { grid-template-columns: minmax(0, 1fr); }
        .mr dt { margin-top: 4px; }
      }
    `,
  ],
})
export class KnightCoverageComponent {
  private readonly knight = inject(KnightService);
  private readonly governance = inject(GovernanceService);
  private readonly auth = inject(AuthService);

  readonly coverage = input<KnightReferenceCoverage | null>(null);
  readonly state = input<'loading' | 'ok' | 'error'>('loading');
  /** A avaliação aberta — só para nomear os controles KNIGHT que avaliam cada referência. */
  readonly assessment = input<KnightAssessment | null>(null);
  /** Pedido de abrir UM controle KNIGHT. */
  readonly open = output<string>();
  /** Um resultado manual foi registrado: a página relê a cobertura. */
  readonly recorded = output<string>();

  protected readonly views = VIEWS;
  protected readonly resultOptions = MANUAL_RESULT_OPTIONS;
  protected readonly page = PAGE;
  protected readonly Math = Math;
  protected readonly today = new Date().toISOString().slice(0, 10);
  protected readonly label = referenceLabel;
  protected readonly badge = manualResultBadge;
  protected sev(s: string): string {
    return (severityLabel(s as SeverityLevel) ?? s).toLowerCase();
  }

  readonly canManage = computed(() => canManageActionPlans(this.auth.activeRole()));
  readonly filter = signal<KnightReferenceFilter>({ view: 'manual', platform: '', q: '' });
  readonly limit = signal(PAGE);
  readonly rows = computed(() => filterReferenceControls(this.coverage()?.controls ?? [], this.filter()));
  readonly shown = computed(() => this.rows().slice(0, this.limit()));
  readonly platforms = computed(() => [...new Set((this.coverage()?.controls ?? []).map((c) => c.platform))].sort());
  readonly manualCount = computed(() => (this.coverage()?.controls ?? []).filter((c) => !!c.manualResult).length);

  readonly editing = signal<string | null>(null);
  readonly saving = signal(false);
  readonly problems = signal<string[]>([]);
  readonly message = signal<string | null>(null);
  readonly documents = signal<GovernanceDocument[]>([]);
  readonly documentsState = signal<'idle' | 'loading' | 'ok' | 'error'>('idle');
  readonly historyKey = signal<string | null>(null);
  readonly history = signal<KnightManualResult[]>([]);
  readonly historyState = signal<'loading' | 'ok' | 'error'>('loading');

  protected draft: RecordKnightManualResultRequest = this.emptyDraft('');

  setFilter<K extends keyof KnightReferenceFilter>(key: K, value: KnightReferenceFilter[K]): void {
    this.filter.set({ ...this.filter(), [key]: value });
    this.limit.set(PAGE);
  }

  refs(ids: string[], a: KnightAssessment): KnightControlRef[] {
    return controlRefs(ids, a);
  }

  evidenceText(m: KnightManualResult): string {
    const doc = m.evidenceDocumentTitle
      ? `Documento: ${m.evidenceDocumentTitle}${m.evidenceDocumentSha256 ? ` (SHA-256 ${m.evidenceDocumentSha256.slice(0, 12)}…)` : ''}`
      : null;
    return [m.evidenceReference, doc].filter((x) => !!x).join(' · ') || '—';
  }

  startEdit(c: KnightReferenceControlStatus): void {
    this.message.set(null);
    this.problems.set([]);
    const m = c.manualResult;
    this.draft = m
      ? {
          referenceKey: c.key, result: m.result as KnightManualResultValue, justification: '', responsibleName: m.responsibleName,
          evidenceReference: m.evidenceReference, evidenceDocumentId: m.evidenceDocumentId, validUntil: m.expired ? null : m.validUntil,
        }
      : this.emptyDraft(c.key);
    this.editing.set(c.key);
    this.loadDocuments();
  }

  cancel(): void {
    this.editing.set(null);
    this.problems.set([]);
  }

  submit(c: KnightReferenceControlStatus): void {
    const request: RecordKnightManualResultRequest = {
      ...this.draft,
      referenceKey: c.key,
      justification: (this.draft.justification ?? '').trim(),
      responsibleName: (this.draft.responsibleName ?? '').trim(),
      evidenceReference: this.draft.result === 'Withdrawn' ? null : (this.draft.evidenceReference ?? '').trim() || null,
      evidenceDocumentId: this.draft.result === 'Withdrawn' ? null : this.draft.evidenceDocumentId || null,
      validUntil: this.draft.result === 'Withdrawn' ? null : this.draft.validUntil || null,
    };
    const problems = manualResultProblems(request, this.today);
    this.problems.set(problems);
    if (problems.length) return;
    this.saving.set(true);
    this.knight.recordManualResult(request).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editing.set(null);
        this.message.set(`${referenceLabel(c)}: ${saved.resultLabel} registrado. O resultado aparece à parte e não altera nota nem cobertura.`);
        if (this.historyKey() === c.key) this.loadHistory(c.key);
        this.recorded.emit(c.key);
      },
      error: (e: Error) => {
        this.saving.set(false);
        this.problems.set([e.message]);
      },
    });
  }

  toggleHistory(key: string): void {
    if (this.historyKey() === key) {
      this.historyKey.set(null);
      return;
    }
    this.historyKey.set(key);
    this.loadHistory(key);
  }

  private loadHistory(key: string): void {
    this.historyState.set('loading');
    this.knight.getManualHistory(key).subscribe({
      next: (h) => {
        if (this.historyKey() !== key) return;
        this.history.set(h);
        this.historyState.set('ok');
      },
      error: () => this.historyState.set('error'),
    });
  }

  private loadDocuments(): void {
    if (this.documentsState() === 'ok' || this.documentsState() === 'loading') return;
    this.documentsState.set('loading');
    this.governance.listDocuments().subscribe({
      next: (docs) => {
        this.documents.set(docs);
        this.documentsState.set('ok');
      },
      error: () => this.documentsState.set('error'),
    });
  }

  private emptyDraft(key: string): RecordKnightManualResultRequest {
    return { referenceKey: key, result: 'Compliant', justification: '', responsibleName: '', evidenceReference: null, evidenceDocumentId: null, validUntil: null };
  }
}
