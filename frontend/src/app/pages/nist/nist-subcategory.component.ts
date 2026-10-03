import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import { GovernanceDocument } from '../../models/governance.models';
import {
  MATURITY_LEVEL_HINTS,
  MATURITY_LEVEL_LABELS,
  NistAiSuggestion,
  NistAvailableEvidence,
  NistEvaluationDraft,
  NistFunctionMeta,
  NistSubcategoryDetail,
  draftFrom,
  draftGap,
  draftProblem,
  evidenceOriginLabel,
  gapText,
  nistCategoryLabel,
  nistFunctionBySlug,
  nistFunctionTitle,
  stateBadgeClass,
  stateLabel,
  toSaveRequest,
} from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { GovernanceService } from '../../services/governance.service';
import { NistApiError, NistService } from '../../services/nist.service';

/**
 * [AEGIS-NIST-JOURNEY-01] Uma subcategoria do NIST CSF 2.0 dentro da avaliação: o resultado esperado, a situação atual
 * e o alvo, a justificativa, as lacunas e o risco, o responsável, as evidências (com origem, data e escopo) e a revisão.
 * Toda gravação vai ao servidor com a versão lida (conflito = 409, nada é sobrescrito). A sugestão da IA só preenche o
 * rascunho: vira avaliação apenas quando o analista grava.
 */
@Component({
  selector: 'app-nist-subcategory',
  standalone: true,
  imports: [RouterLink, FormsModule, DatePipe, DecimalPipe],
  template: `
    <section class="page">
      @if (fn(); as f) {
        <nav class="crumbs" aria-label="Você está em">
          <a [routerLink]="['/nist']" [queryParams]="params()">AEGIS NIST</a><span aria-hidden="true">›</span>
          <a [routerLink]="['/nist', f.slug]" [queryParams]="params()">{{ nistFunctionTitle(f) }}</a><span aria-hidden="true">›</span>
          <span aria-current="page">{{ code() }}</span>
        </nav>
      }

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a subcategoria…</p></div></div>
      } @else if (error()) {
        <div class="panel"><div class="state error" role="alert"><p class="err">{{ error() }}</p>
          <a class="ghost" [routerLink]="['/nist']">Voltar ao AEGIS NIST</a></div></div>
      } @else if (detail()) {
              @let d = detail()!;
        <header class="page-head">
          <div>
            <p class="page-eyebrow">{{ nistCategoryLabel(d.categoryCode, d.categoryName) }} · {{ d.categoryCode }}</p>
            <h1>{{ d.title }}</h1>
            <p class="page-meta"><span class="mono">{{ d.code }}</span> ·
              <span [class]="'badge ' + stateBadgeClass(d.evaluation?.state ?? 'NotEvaluated')">{{ stateLabel(d.evaluation?.state ?? 'NotEvaluated') }}</span></p>
          </div>
        </header>

        <section class="panel" aria-labelledby="sc-outcome">
          <div class="hd"><h3 id="sc-outcome">Resultado esperado</h3></div>
          @if (d.summary) { <p><strong>{{ d.summary }}</strong></p> }
          <p class="muted" lang="en"><span class="lbl">Texto oficial do NIST CSF 2.0 (inglês):</span> {{ d.officialOutcome }}</p>
          @if (d.impact) { <p><span class="lbl">Por que importa:</span> {{ d.impact }}</p> }
          @if (d.initialAction) { <p><span class="lbl">Primeiro passo de melhoria:</span> {{ d.initialAction }}</p> }
          @if (d.implementationExamples) {
            <details><summary>Exemplos de implementação (NIST, inglês)</summary><p class="pre" lang="en">{{ d.implementationExamples }}</p></details>
          }
          <p class="hint">Título e explicação em linguagem clara: redação do AEGIS, não tradução oficial.</p>
        </section>

        <section class="panel" aria-labelledby="sc-eval">
          <div class="hd"><h3 id="sc-eval">Avaliação</h3>
            <span class="hint">escala 1–5 · metodologia {{ d.methodologyVersion }}</span></div>
          @if (!canWrite()) { <p class="notice">Seu papel permite consultar, mas não registrar (requer Manager ou TenantAdmin).</p> }
          <form (ngSubmit)="save(d)">
            <fieldset [disabled]="!canWrite() || saving()">
              <label class="check"><input type="checkbox" name="na" [(ngModel)]="draft.notApplicable" /> Este resultado não se aplica ao escopo</label>
              @if (!draft.notApplicable) {
                <div class="levels">
                  <fieldset class="lv-set">
                    <legend>Situação atual</legend>
                    <label class="lv-opt"><input type="radio" name="cur" [ngModel]="draft.currentLevel" (ngModelChange)="draft.currentLevel = $event" [value]="null" /> Sem nível</label>
                    @for (l of levels; track l) {
                      <label class="lv-opt" [title]="hints[l]"><input type="radio" name="cur" [ngModel]="draft.currentLevel" (ngModelChange)="draft.currentLevel = $event" [value]="l" /> {{ l }} · {{ labels[l] }}</label>
                    }
                  </fieldset>
                  <fieldset class="lv-set">
                    <legend>Alvo</legend>
                    <label class="lv-opt"><input type="radio" name="tgt" [ngModel]="draft.targetLevel" (ngModelChange)="draft.targetLevel = $event" [value]="null" /> Sem nível</label>
                    @for (l of levels; track l) {
                      <label class="lv-opt" [title]="hints[l]"><input type="radio" name="tgt" [ngModel]="draft.targetLevel" (ngModelChange)="draft.targetLevel = $event" [value]="l" /> {{ l }} · {{ labels[l] }}</label>
                    }
                  </fieldset>
                </div>
                <p>Lacuna: <strong>{{ gapText(gap()) }}</strong>
                  @if (gap() === null) { <span class="muted"> — registre atual e alvo para determiná-la.</span> }</p>
              }
              <div class="grid">
                <label class="field wide"><span class="field-label">{{ draft.notApplicable ? 'Por que não se aplica (obrigatório)' : 'Justificativa' }}</span>
                  <textarea name="rat" rows="3" maxlength="4000" [(ngModel)]="draft.rationale"></textarea></label>
                @if (!draft.notApplicable) {
                  <label class="field"><span class="field-label">Observações sobre a situação atual</span><textarea name="cc" rows="3" maxlength="4000" [(ngModel)]="draft.currentComments"></textarea></label>
                  <label class="field"><span class="field-label">Observações sobre o alvo</span><textarea name="tc" rows="3" maxlength="4000" [(ngModel)]="draft.targetComments"></textarea></label>
                  <label class="field"><span class="field-label">Lacunas observadas</span><textarea name="gp" rows="3" maxlength="4000" [(ngModel)]="draft.gaps"></textarea></label>
                  <label class="field"><span class="field-label">Risco ou impacto (quando fundamentado)</span><textarea name="rk" rows="3" maxlength="2000" [(ngModel)]="draft.riskImpact"></textarea></label>
                  <label class="field wide"><span class="field-label">Orientação de melhoria / ação de tratamento</span><textarea name="ig" rows="2" maxlength="4000" [(ngModel)]="draft.improvementGuidance"></textarea></label>
                }
                <label class="field"><span class="field-label">Responsável na organização</span><input name="own" maxlength="200" [(ngModel)]="draft.ownerName" /></label>
              </div>
            </fieldset>
            @if (canWrite()) {
              <div class="actions">
                <button type="submit" class="primary" [disabled]="saving() || !!problem()">{{ saving() ? 'Gravando…' : 'Gravar avaliação' }}</button>
                @if (problem(); as pb) { <span class="muted">{{ pb }}</span> }
              </div>
            }
          </form>
          @if (saveError(); as se) {
            <div class="notice error" role="alert">{{ se }}
              @if (conflict()) { <button type="button" class="ghost sm" (click)="reload()">Recarregar a versão atual</button> }</div>
          }
          @if (savedNote()) { <p class="notice" role="status">{{ savedNote() }}</p> }
          @if (d.evaluation; as e) {
            <p class="hint">Revisão humana: {{ e.reviewedByName ?? 'autor não identificado' }}
              @if (e.reviewedAt) { em {{ e.reviewedAt | date: 'dd/MM/yyyy HH:mm' }} } · versão {{ e.version }}</p>
          } @else { <p class="hint">Ainda sem revisão registrada.</p> }
        </section>

        <section class="panel" aria-labelledby="sc-ai">
          <div class="hd"><h3 id="sc-ai">Sugestão da IA</h3><span class="hint">apoio à interpretação — não é revisão</span></div>
          <p class="muted">A IA lê as observações e as evidências vinculadas e sugere uma situação atual. Nada é gravado: use a sugestão como rascunho e grave só depois de revisar.</p>
          @if (canWrite()) {
            <button type="button" class="ghost sm" (click)="suggest(d)" [disabled]="suggesting()">{{ suggesting() ? 'Consultando…' : 'Pedir sugestão' }}</button>
          }
          @if (aiError()) { <p class="notice warn" role="status">{{ aiError() }}</p> }
          @if (suggestion(); as s) {
            <div class="ai">
              <p><strong>Sugestão: {{ s.suggestedCurrentLevel }} · {{ labels[s.suggestedCurrentLevel] }}</strong> · confiança {{ s.confidence * 100 | number: '1.0-0' }}%
                @if (s.simulated) { <span class="badge warn">Simulada — sem IA real neste ambiente</span> }</p>
              <p class="pre">{{ s.rationale }}</p>
              <button type="button" class="ghost sm" (click)="useSuggestion(s)">Usar como rascunho da situação atual</button>
            </div>
          }
        </section>

        <section class="panel" aria-labelledby="sc-ev">
          <div class="hd"><h3 id="sc-ev">Evidências</h3><span class="hint">{{ d.evidence.length }} vinculada(s)</span></div>
          @if (d.evidence.length === 0) { <p class="muted">Nenhuma evidência vinculada ainda.</p> }
          <ul class="ev-list">
            @for (e of d.evidence; track e.id) {
              <li>
                <div class="ev-top"><span class="badge info">{{ evidenceOriginLabel(e.originKind) }}</span><strong>{{ e.title }}</strong>
                  @if (e.uri) { <a [href]="e.uri" target="_blank" rel="noopener noreferrer">abrir link<span class="sr-only"> (nova aba)</span></a> }</div>
                <p class="muted">{{ e.originLabel ?? '' }} · data na origem {{ e.collectedAt | date: 'dd/MM/yyyy' : 'UTC' }} · vinculada por {{ e.recordedByName ?? '—' }} em {{ e.linkedAt | date: 'dd/MM/yyyy' }}</p>
                @if (e.originScope) { <p class="muted">Escopo da coleta: {{ e.originScope }}</p> }
                @if (e.notes) { <p>{{ e.notes }}</p> }
                @if (canWrite()) { <button type="button" class="ghost xs" (click)="remove(d, e.id)" [disabled]="busy()">Retirar vínculo</button> }
              </li>
            }
          </ul>

          <h4>Evidências disponíveis na plataforma</h4>
          @if (d.availableEvidence.length === 0) {
            <p class="muted">Nenhuma evidência técnica ou documental mapeada para esta subcategoria.</p>
          }
          <ul class="ev-list">
            @for (a of d.availableEvidence; track a.originKind + (a.knightRunId ?? '') + (a.knightIndicatorId ?? '') + (a.documentId ?? '')) {
              <li>
                <div class="ev-top"><span class="badge neutral">{{ evidenceOriginLabel(a.originKind) }}</span><strong>{{ a.title }}</strong>
                  @if (a.status) { <span class="badge violet">{{ a.status }}</span> }
                  @if (a.isDemo) { <span class="badge warn">Demonstração</span> }</div>
                <p class="muted">{{ a.originLabel }} @if (a.collectedAt) { · {{ a.collectedAt | date: 'dd/MM/yyyy' : 'UTC' }} } @if (a.originScope) { · {{ a.originScope }} }</p>
                <p class="muted">Critério: {{ a.criterion }} @if (a.limitation) { <strong>{{ a.limitation }}</strong> }</p>
                @if (canWrite()) {
                  @if (a.alreadyLinked) { <span class="hint">Já vinculada</span> }
                  @else if (linkable(a)) { <button type="button" class="ghost xs" (click)="linkAvailable(d, a)" [disabled]="busy()">Vincular</button> }
                }
              </li>
            }
          </ul>

          @if (canWrite()) {
            <details class="add">
              <summary>Vincular documento da biblioteca</summary>
              @if (documents() === null) { <button type="button" class="ghost xs" (click)="loadDocuments()">Listar documentos</button> }
              @else if (documents()!.length === 0) { <p class="muted">A biblioteca não tem documentos. <a routerLink="/nist/gv/documentos">Enviar documento</a></p> }
              @else {
                <div class="row">
                  <label class="field"><span class="field-label">Documento</span>
                    <select name="doc" [(ngModel)]="docChoice">
                      @for (doc of documents()!; track doc.id) { <option [value]="doc.id">{{ doc.title }}</option> }
                    </select></label>
                  <label class="field"><span class="field-label">O que este documento demonstra</span><input name="docnote" maxlength="2000" [(ngModel)]="docNote" /></label>
                  <button type="button" class="primary sm" (click)="linkDocument(d)" [disabled]="busy() || !docChoice">Vincular documento</button>
                </div>
              }
            </details>
            <details class="add">
              <summary>Registrar evidência (link, entrevista, observação)</summary>
              <div class="row">
                <label class="field"><span class="field-label">Título</span><input name="mt" maxlength="300" [(ngModel)]="manual.title" /></label>
                <label class="field"><span class="field-label">Tipo</span>
                  <select name="mty" [(ngModel)]="manual.type"><option value="Interview">Entrevista</option><option value="Link">Link</option><option value="Document">Documento externo</option><option value="Screenshot">Captura</option></select></label>
                <label class="field"><span class="field-label">Link (opcional)</span><input name="mu" type="url" maxlength="2000" [(ngModel)]="manual.uri" placeholder="https://" /></label>
                <label class="field"><span class="field-label">Data</span><input name="md" type="date" [(ngModel)]="manual.date" /></label>
                <label class="field wide"><span class="field-label">Observação</span><textarea name="mn" rows="2" maxlength="2000" [(ngModel)]="manual.notes"></textarea></label>
                <button type="button" class="primary sm" (click)="linkManual(d)" [disabled]="busy() || manual.title.trim().length === 0">Registrar evidência</button>
              </div>
            </details>
          }
          @if (evidenceError()) { <p class="notice error" role="alert">{{ evidenceError() }}</p> }
        </section>

        <section class="panel" aria-labelledby="sc-posture">
          <div class="hd"><h3 id="sc-posture">Postura do ambiente (AEGIS Score)</h3><span class="hint">outro conceito · não entra na maturidade</span></div>
          @if (d.posture; as p) {
            <p>Controle {{ postureLabel(p.status) }} por {{ p.verdictSource === 'Telemetry' ? 'telemetria' : 'análise documental' }} ·
              {{ p.achievedPoints }} de {{ p.maxPoints }} pontos · em {{ p.lastEvaluatedAt | date: 'dd/MM/yyyy' }}</p>
          } @else {
            <p class="muted">Sem leitura de postura para este controle no ambiente.</p>
          }
        </section>
      }
    </section>
  `,
  styles: [
    `
      .crumbs { display: flex; flex-wrap: wrap; gap: 6px; font-size: var(--fs-sm); color: var(--muted); }
      .crumbs a { color: var(--text-2); }
      .lbl { color: var(--muted); font-weight: 600; }
      .pre { white-space: pre-line; }
      fieldset { border: 0; margin: 0; padding: 0; min-width: 0; }
      .levels { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: var(--sp-4); margin: var(--sp-3) 0; }
      .lv-set { display: flex; flex-direction: column; gap: 6px; }
      .lv-set legend { font-weight: 600; margin-bottom: 6px; }
      .lv-opt, .check { display: flex; align-items: center; gap: var(--sp-2); font-size: var(--fs-sm); }
      .grid, .row { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: var(--sp-3); margin-top: var(--sp-3); align-items: end; }
      .wide { grid-column: 1 / -1; }
      .actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-3); margin-top: var(--sp-3); }
      .ev-list { list-style: none; padding: 0; margin: 0 0 var(--sp-3); display: flex; flex-direction: column; }
      .ev-list li { padding: var(--sp-3) 0; border-top: 1px solid var(--line-2); display: flex; flex-direction: column; gap: 4px; align-items: flex-start; }
      .ev-top { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-2); }
      .ev-list p { margin: 0; overflow-wrap: anywhere; }
      .add { margin-top: var(--sp-3); }
      .add summary { cursor: pointer; font-weight: 500; }
      .ai { margin-top: var(--sp-3); }
      h4 { margin: var(--sp-4) 0 var(--sp-2); font-size: var(--fs-body); }
    `,
  ],
})
export class NistSubcategoryComponent {
  private readonly nist = inject(NistService);
  private readonly governance = inject(GovernanceService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  protected readonly levels = [1, 2, 3, 4, 5];
  protected readonly labels = MATURITY_LEVEL_LABELS;
  protected readonly hints = MATURITY_LEVEL_HINTS;
  protected readonly gapText = gapText;
  protected readonly stateLabel = stateLabel;
  protected readonly stateBadgeClass = stateBadgeClass;
  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly nistCategoryLabel = nistCategoryLabel;
  protected readonly evidenceOriginLabel = evidenceOriginLabel;

  protected readonly fn = signal<NistFunctionMeta | null>(null);
  protected readonly code = signal('');
  private readonly ids = signal<{ assessmentId: string; scopeId: string } | null>(null);
  protected readonly detail = signal<NistSubcategoryDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly busy = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly conflict = signal(false);
  protected readonly savedNote = signal<string | null>(null);
  protected readonly evidenceError = signal<string | null>(null);
  protected readonly suggesting = signal(false);
  protected readonly suggestion = signal<NistAiSuggestion | null>(null);
  protected readonly aiError = signal<string | null>(null);
  protected readonly documents = signal<GovernanceDocument[] | null>(null);

  /** Rascunho do formulário (ngModel); os derivados (lacuna, pendência) são relidos a cada detecção de mudanças. */
  protected draft: NistEvaluationDraft = draftFrom(null);
  protected docChoice = '';
  protected docNote = '';
  protected manual = { title: '', type: 'Interview', uri: '', date: '', notes: '' };

  protected readonly canWrite = computed(() => ['Manager', 'TenantAdmin'].includes(this.auth.activeRole() ?? ''));
  protected readonly params = computed(() => {
    const i = this.ids();
    return i ? { avaliacao: i.assessmentId, escopo: i.scopeId } : {};
  });

  constructor() {
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([p, q]) => {
        this.fn.set(nistFunctionBySlug(p.get('fn')));
        this.code.set((p.get('code') ?? '').toUpperCase());
        const a = q.get('avaliacao');
        const s = q.get('escopo');
        if (!a || !s) {
          this.loading.set(false);
          this.error.set('Abra a subcategoria a partir de uma avaliação e de um escopo.');
          return;
        }
        this.ids.set({ assessmentId: a, scopeId: s });
        this.reload();
      });
  }

  protected gap(): number | null {
    return draftGap(this.draft);
  }

  protected problem(): string | null {
    return draftProblem(this.draft);
  }

  protected postureLabel(status: string): string {
    return status === 'Compliant' ? 'conforme' : status === 'MitigatedByThirdParty' ? 'mitigado por terceiro' : 'não conforme';
  }

  reload(): void {
    const i = this.ids();
    if (!i) return;
    this.loading.set(true);
    this.error.set(null);
    this.saveError.set(null);
    this.conflict.set(false);
    this.nist.subcategory(i.assessmentId, i.scopeId, this.code()).subscribe({
      next: (d) => this.accept(d),
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  protected save(d: NistSubcategoryDetail): void {
    if (this.saving() || this.problem()) return;
    this.saving.set(true);
    this.saveError.set(null);
    this.savedNote.set(null);
    this.nist.save(d.assessmentId, d.scopeId, d.code, toSaveRequest(this.draft, d.evaluation?.version ?? 0)).subscribe({
      next: (r) => {
        this.saving.set(false);
        this.accept(r);
        this.savedNote.set('Avaliação gravada.');
      },
      error: (e: NistApiError) => {
        this.saving.set(false);
        this.conflict.set(e.status === 409);
        this.saveError.set(e.message);
      },
    });
  }

  protected suggest(d: NistSubcategoryDetail): void {
    this.suggesting.set(true);
    this.aiError.set(null);
    this.nist.suggest(d.assessmentId, d.scopeId, d.code).subscribe({
      next: (s) => {
        this.suggestion.set(s);
        this.suggesting.set(false);
      },
      error: (e: Error) => {
        this.aiError.set(e.message);
        this.suggesting.set(false);
      },
    });
  }

  protected useSuggestion(s: NistAiSuggestion): void {
    this.draft = { ...this.draft, notApplicable: false, currentLevel: s.suggestedCurrentLevel };
    this.savedNote.set('Sugestão aplicada ao rascunho — revise e grave para registrar a avaliação.');
  }

  protected linkable(a: NistAvailableEvidence): boolean {
    return a.originKind === 'AssetInventory' || a.originKind === 'GovernanceDocument'
      || (a.originKind === 'KnightIndicator' && !a.limitation?.startsWith('Sem resultado'));
  }

  protected linkAvailable(d: NistSubcategoryDetail, a: NistAvailableEvidence): void {
    this.link(d, {
      kind: a.originKind,
      documentId: a.documentId,
      knightRunId: a.knightRunId,
      knightIndicatorId: a.knightIndicatorId,
    });
  }

  protected loadDocuments(): void {
    this.governance.listDocuments().subscribe({
      next: (docs) => {
        this.documents.set(docs);
        this.docChoice = docs[0]?.id ?? '';
      },
      error: () => this.evidenceError.set('Não foi possível listar os documentos da biblioteca.'),
    });
  }

  protected linkDocument(d: NistSubcategoryDetail): void {
    this.link(d, { kind: 'GovernanceDocument', documentId: this.docChoice, notes: this.docNote.trim() || null }, () => (this.docNote = ''));
  }

  protected linkManual(d: NistSubcategoryDetail): void {
    const m = this.manual;
    this.link(
      d,
      { kind: 'Manual', title: m.title.trim(), uri: m.uri.trim() || null, notes: m.notes.trim() || null, collectedOn: m.date || null, manualType: m.type },
      () => (this.manual = { title: '', type: 'Interview', uri: '', date: '', notes: '' }),
    );
  }

  protected remove(d: NistSubcategoryDetail, evidenceId: string): void {
    this.busy.set(true);
    this.evidenceError.set(null);
    this.nist.removeEvidence(d.assessmentId, d.scopeId, d.code, evidenceId).subscribe({
      next: (r) => this.acceptEvidence(r),
      error: (e: Error) => {
        this.busy.set(false);
        this.evidenceError.set(e.message);
      },
    });
  }

  private link(d: NistSubcategoryDetail, request: Parameters<NistService['linkEvidence']>[3], done?: () => void): void {
    this.busy.set(true);
    this.evidenceError.set(null);
    this.nist.linkEvidence(d.assessmentId, d.scopeId, d.code, request).subscribe({
      next: (r) => {
        done?.();
        this.acceptEvidence(r);
      },
      error: (e: Error) => {
        this.busy.set(false);
        this.evidenceError.set(e.message);
      },
    });
  }

  /** Evidência mudou: atualiza a lista sem descartar o rascunho do formulário em edição. */
  private acceptEvidence(d: NistSubcategoryDetail): void {
    this.busy.set(false);
    this.detail.set(d);
  }

  private accept(d: NistSubcategoryDetail): void {
    this.detail.set(d);
    this.draft = draftFrom(d.evaluation);
    this.loading.set(false);
  }
}
