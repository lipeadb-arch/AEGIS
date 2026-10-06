import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  NistCycle,
  NistCycleComparison,
  NistPublication,
  NistPublicationPreview,
  averageText,
  comparisonChangeLabel,
  cycleLabel,
  cyclePeriodText,
  gapText,
  functionSlugOf,
} from '../../models/nist.models';
import { PostureExportFormat } from '../../models/posture-history.models';
import { NistApiError, NistCtx, NistService, saveBlob } from '../../services/nist.service';
import { PostureHistoryService } from '../../services/posture-history.service';

/**
 * [AEGIS-NIST-JOURNEY-02] Publicação e relatórios da rodada e escopo.
 *   • Prévia: o MESMO documento que será congelado, com a impressão digital do conteúdo revisado na tela.
 *   • Publicar: o servidor só congela se o conteúdo ainda for o da prévia (senão 409 — nada é publicado em silêncio).
 *   • Fotografias: HTML, PDF e CSV saem da MESMA fotografia congelada (nunca de dados atuais).
 *   • Comparação: duas rodadas da mesma avaliação e escopo; incompatibilidade e mudança de universo são ditas.
 */
@Component({
  selector: 'app-nist-publish-panel',
  standalone: true,
  imports: [FormsModule, DatePipe, RouterLink],
  template: `
    <section class="panel" aria-labelledby="pub-h">
      <div class="hd"><h3 id="pub-h">Publicar a rodada</h3><span class="hint">fotografia imutável · maturidade 1–5 (AEGIS)</span></div>
      <p class="muted">A publicação congela a rodada e o escopo selecionados — perfil, situação de cada subcategoria, procedimentos,
        evidências, achados, planos e responsáveis — numa fotografia com hash. O relatório HTML, o PDF e o CSV são gerados dessa
        fotografia e nunca releem dados atuais.</p>
      @if (!preview()) {
        <button type="button" class="ghost sm" (click)="loadPreview()" [disabled]="previewLoading()">{{ previewLoading() ? 'Montando a prévia…' : 'Ver prévia da publicação' }}</button>
      }
      @if (previewError()) { <p class="notice error" role="alert">{{ previewError() }}</p> }
      @if (preview(); as pv) {
        <div class="cards">
          <div class="card"><div class="k">Atual</div><div class="v">{{ averageText(pv.summary.current) }}</div></div>
          <div class="card"><div class="k">Alvo</div><div class="v">{{ averageText(pv.summary.target) }}</div></div>
          <div class="card"><div class="k">Lacuna</div><div class="v">{{ gapText(pv.summary.gap) }}</div></div>
          <div class="card"><div class="k">Cobertura</div><div class="v">{{ pct(pv.summary.coverage) }}</div></div>
        </div>
        <p>{{ pv.summary.evaluated }} avaliadas · {{ pv.summary.notApplicable }} não se aplica{{ pv.summary.notApplicable === 1 ? "" : "m" }} · {{ pv.summary.pendingConfirmation }} aguardando
          confirmação · {{ pv.summary.inProgress }} em andamento · {{ pv.summary.notEvaluated }} não avaliadas — de {{ pv.summary.subcategories }}.
          {{ pv.summary.proceduresPerformed }} procedimento(s) realizado(s) · {{ pv.findings }} achado(s) · {{ pv.summary.plansActive }} plano(s) ativo(s).</p>
        <div class="table-wrap"><table class="data-table">
          <caption class="sr-only">Perfil por função na prévia</caption>
          <thead><tr><th scope="col">Função</th><th scope="col">Atual</th><th scope="col">Alvo</th><th scope="col">Lacuna</th><th scope="col">Avaliadas</th></tr></thead>
          <tbody>
            @for (f of pv.functions; track f.code) {
              <tr><td>{{ f.code }} · {{ f.name }}</td><td>{{ averageText(f.current) }}</td><td>{{ averageText(f.target) }}</td><td>{{ gapText(f.gap) }}</td><td>{{ f.evaluated }} de {{ f.subcategories }}</td></tr>
            }
          </tbody>
        </table></div>
        @if (pv.warnings.length) {
          <div class="notice warn" role="status"><strong>Antes de publicar:</strong><ul>@for (w of pv.warnings; track w) { <li>{{ w }}</li> }</ul></div>
        }
        @if (pv.limitations.length) {
          <details><summary>Limitações declaradas no relatório ({{ pv.limitations.length }})</summary><ul>@for (l of pv.limitations; track l) { <li>{{ l }}</li> }</ul></details>
        }
        <p class="hint">Impressão digital do conteúdo revisado: <span class="mono">{{ pv.contentFingerprint.slice(0, 16) }}…</span></p>
        <div class="actions">
          @if (canWrite()) {
            <button type="button" class="primary" (click)="publish(pv)" [disabled]="publishing()">{{ publishing() ? 'Publicando…' : 'Publicar exatamente esta versão' }}</button>
          }
          <button type="button" class="ghost sm" (click)="loadPreview()" [disabled]="previewLoading() || publishing()">Atualizar a prévia</button>
        </div>
      }
      @if (publishError(); as pe) { <p class="notice error" role="alert">{{ pe }}</p> }
      @if (published(); as pb) {
        <p class="notice" role="status">Fotografia publicada em {{ pb.capturedAt | date: 'dd/MM/yyyy HH:mm' }} · hash <span class="mono">{{ pb.contentHash.slice(0, 16) }}…</span>.
          Baixe HTML, PDF e CSV abaixo — os três vêm desta fotografia.</p>
      }
    </section>

    <section class="panel" aria-labelledby="pubs-h">
      <div class="hd"><h3 id="pubs-h">Fotografias publicadas desta rodada e escopo</h3><a class="hint" routerLink="/history">histórico mensal</a></div>
      @if (listError()) { <p class="notice error" role="alert">{{ listError() }}</p> }
      @if (publications().length === 0) {
        <p class="muted">Nenhuma publicação ainda.</p>
      } @else {
        <ul class="pubs">
          @for (p of publications(); track p.snapshotId) {
            <li>
              <p><strong>{{ p.capturedAt | date: 'dd/MM/yyyy HH:mm' }}</strong> · {{ p.publishedByName ?? '—' }} ·
                atual {{ averageText(p.current) }} · alvo {{ averageText(p.target) }} · lacuna {{ gapText(p.gap) }} · cobertura {{ pct(p.coverage) }}
                · <span class="mono">{{ p.contentHash.slice(0, 12) }}…</span></p>
              <div class="actions">
                @for (f of formats; track f) {
                  <button type="button" class="ghost xs" (click)="download(p, f)" [disabled]="downloading() !== null">
                    {{ downloading() === p.snapshotId + f ? 'Baixando…' : f.toUpperCase() }}<span class="sr-only"> da fotografia de {{ p.capturedAt | date: 'dd/MM/yyyy HH:mm' }}</span></button>
                }
              </div>
            </li>
          }
        </ul>
      }
      @if (downloadError()) { <p class="notice error" role="alert">{{ downloadError() }}</p> }
    </section>

    <section class="panel" aria-labelledby="cmp-h">
      <div class="hd"><h3 id="cmp-h">Comparar rodadas</h3><span class="hint">mesma avaliação e escopo</span></div>
      @if (cycles().length < 2) {
        <p class="muted">A comparação precisa de pelo menos duas rodadas nesta avaliação.</p>
      } @else {
        <div class="row">
          <label class="field"><span class="field-label">Rodada base</span>
            <select [(ngModel)]="baseId" name="cmpb">@for (c of cycles(); track c.id) { <option [value]="c.id">{{ cycleLabel(c) }}</option> }</select></label>
          <label class="field"><span class="field-label">Rodada comparada</span>
            <select [(ngModel)]="targetId" name="cmpt">@for (c of cycles(); track c.id) { <option [value]="c.id">{{ cycleLabel(c) }}</option> }</select></label>
          <button type="button" class="ghost sm" (click)="compare()" [disabled]="comparing() || !baseId || !targetId || baseId === targetId">Comparar</button>
        </div>
        @if (compareError()) { <p class="notice error" role="alert">{{ compareError() }}</p> }
        @if (comparison(); as cmp) {
          @if (!cmp.compatible) {
            <div class="notice warn" role="status"><strong>Rodadas não comparáveis:</strong><ul>@for (r of cmp.incompatibilityReasons; track r) { <li>{{ r }}</li> }</ul></div>
          } @else {
            <p><strong>{{ cmp.baseCycleName }} → {{ cmp.targetCycleName }}</strong>: atual {{ delta(cmp.currentDelta) }} · alvo {{ delta(cmp.targetDelta) }} ·
              cobertura {{ delta(cmp.coverageDelta, true) }} · universo aplicável {{ cmp.baseApplicable }} → {{ cmp.targetApplicable }}</p>
            @if (cmp.notes.length) { <ul class="notes">@for (n of cmp.notes; track n) { <li>{{ n }}</li> }</ul> }
            <div class="table-wrap"><table class="data-table">
              <caption class="sr-only">Comparação por função</caption>
              <thead><tr><th scope="col">Função</th><th scope="col">Atual (base → comparada)</th><th scope="col">Variação</th><th scope="col">Avaliadas</th></tr></thead>
              <tbody>
                @for (f of cmp.functions; track f.code) {
                  <tr><td>{{ f.code }} · {{ f.name }}</td><td>{{ averageText(f.baseCurrent) }} → {{ averageText(f.targetCurrent) }}</td><td>{{ delta(f.currentDelta) }}</td><td>{{ f.baseEvaluated }} → {{ f.targetEvaluated }}</td></tr>
                }
              </tbody>
            </table></div>
            @if (cmp.changes.length) {
              <details><summary>{{ cmp.changes.length }} subcategoria(s) com mudança</summary>
                <ul class="notes">@for (ch of cmp.changes; track ch.code + ch.kind) {
                  <li><a [routerLink]="['/nist', slug(ch.code), ch.code]" [queryParams]="params()">{{ ch.code }}</a> · {{ changeLabel(ch.kind) }} — {{ ch.description }}</li>
                }</ul></details>
            }
          }
        }
      }
    </section>
  `,
  styles: [
    `
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); align-items: center; margin-top: var(--sp-2); }
      .pubs { list-style: none; padding: 0; margin: 0; }
      .pubs li { padding: var(--sp-3) 0; border-top: 1px solid var(--line-2); }
      .pubs p { margin: 0; overflow-wrap: anywhere; }
      .row { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: var(--sp-3); align-items: end; }
      .notes { margin: var(--sp-2) 0; padding-left: 18px; }
      details summary { cursor: pointer; }
    `,
  ],
})
export class NistPublishPanelComponent {
  private readonly nist = inject(NistService);
  private readonly history = inject(PostureHistoryService);

  readonly ctx = input.required<NistCtx>();
  readonly cycles = input.required<NistCycle[]>();
  readonly params = input.required<Record<string, string>>();
  readonly canWrite = input(false);
  readonly publishedChange = output<NistPublication>();

  protected readonly formats: PostureExportFormat[] = ['html', 'pdf', 'csv'];
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly cyclePeriodText = cyclePeriodText;
  protected readonly cycleLabel = cycleLabel;
  protected readonly changeLabel = comparisonChangeLabel;
  protected readonly slug = functionSlugOf;

  protected readonly preview = signal<NistPublicationPreview | null>(null);
  protected readonly previewLoading = signal(false);
  protected readonly previewError = signal<string | null>(null);
  protected readonly publishing = signal(false);
  protected readonly publishError = signal<string | null>(null);
  protected readonly published = signal<NistPublication | null>(null);
  protected readonly publications = signal<NistPublication[]>([]);
  protected readonly listError = signal<string | null>(null);
  protected readonly downloading = signal<string | null>(null);
  protected readonly downloadError = signal<string | null>(null);
  protected readonly comparison = signal<NistCycleComparison | null>(null);
  protected readonly comparing = signal(false);
  protected readonly compareError = signal<string | null>(null);
  protected baseId = '';
  protected targetId = '';

  /** Geração do contexto: toda resposta pedida antes de uma troca de rodada/escopo é descartada. */
  private gen = 0;
  private subs: Subscription[] = [];
  private readonly key = computed(() => `${this.ctx().assessmentId}|${this.ctx().cycleId}|${this.ctx().scopeId}`);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.reset());
    effect(() => {
      this.key();
      untracked(() => {
        const cycles = this.cycles();
        this.reset();
        this.targetId = this.ctx().cycleId;
        this.baseId = cycles.find((c) => c.id !== this.ctx().cycleId)?.id ?? '';
        this.loadPublications();
      });
    });
  }

  protected pct(v: number): string {
    return `${(Math.round(v * 10) / 10).toLocaleString('pt-BR')}%`;
  }

  protected delta(v: number | null, percent = false): string {
    if (v === null || v === undefined) return 'indeterminada';
    const n = Math.round(v * 10) / 10;
    const s = Math.abs(n).toLocaleString('pt-BR', { maximumFractionDigits: 1 });
    return `${n > 0 ? '+' : n < 0 ? '−' : ''}${s}${percent ? ' p.p.' : ''}`;
  }

  protected loadPreview(keepError = false): void {
    const gen = this.gen;
    this.previewLoading.set(true);
    this.previewError.set(null);
    if (!keepError) this.publishError.set(null);
    this.track(
      this.nist.publicationPreview(this.ctx()).subscribe({
        next: (p) => {
          if (gen !== this.gen) return;
          this.preview.set(p);
          this.previewLoading.set(false);
        },
        error: (e: Error) => {
          if (gen !== this.gen) return;
          this.previewError.set(e.message);
          this.previewLoading.set(false);
        },
      }),
    );
  }

  protected publish(pv: NistPublicationPreview): void {
    if (this.publishing()) return;
    const gen = this.gen;
    this.publishing.set(true);
    this.publishError.set(null);
    this.published.set(null);
    this.track(
      this.nist.publish(this.ctx(), pv.contentFingerprint).subscribe({
        next: (p) => {
          if (gen !== this.gen) return;
          this.publishing.set(false);
          this.published.set(p);
          this.preview.set(null);
          this.publications.update((list) => [p, ...list.filter((x) => x.snapshotId !== p.snapshotId)]);
          this.publishedChange.emit(p);
        },
        error: (e: NistApiError) => {
          if (gen !== this.gen) return;
          this.publishing.set(false);
          if (e.status === 409) {
            // O conteúdo mudou depois da prévia: nada foi publicado. A nova prévia é montada para nova revisão.
            this.publishError.set(`${e.message} Nada foi publicado: revise a prévia atualizada antes de publicar.`);
            this.preview.set(null);
            this.loadPreview(true);
          } else {
            this.publishError.set(e.message);
          }
        },
      }),
    );
  }

  protected download(p: NistPublication, format: PostureExportFormat): void {
    if (this.downloading() !== null) return;
    this.downloading.set(p.snapshotId + format);
    this.downloadError.set(null);
    this.history.exportSnapshot(p.snapshotId, format).subscribe({
      next: (file) => {
        saveBlob(file.blob, file.filename);
        this.downloading.set(null);
      },
      error: (e: Error) => {
        this.downloadError.set(e.message);
        this.downloading.set(null);
      },
    });
  }

  protected compare(): void {
    if (!this.baseId || !this.targetId || this.baseId === this.targetId) return;
    const gen = this.gen;
    this.comparing.set(true);
    this.compareError.set(null);
    this.comparison.set(null);
    this.track(
      this.nist.compare(this.ctx().assessmentId, this.ctx().scopeId, this.baseId, this.targetId).subscribe({
        next: (c) => {
          if (gen !== this.gen) return;
          this.comparison.set(c);
          this.comparing.set(false);
        },
        error: (e: Error) => {
          if (gen !== this.gen) return;
          this.compareError.set(e.message);
          this.comparing.set(false);
        },
      }),
    );
  }

  private loadPublications(): void {
    const gen = this.gen;
    const c = this.ctx();
    this.track(
      this.nist.publications(c.assessmentId, { cycleId: c.cycleId, scopeId: c.scopeId }).subscribe({
        next: (list) => {
          if (gen === this.gen) this.publications.set(list.filter((p) => p.cycleId === c.cycleId && p.scopeId === c.scopeId));
        },
        error: (e: Error) => {
          if (gen === this.gen) this.listError.set(e.message);
        },
      }),
    );
  }

  private track(s: Subscription): void {
    this.subs.push(s);
  }

  private reset(): void {
    this.gen++;
    this.subs.forEach((s) => s.unsubscribe());
    this.subs = [];
    this.preview.set(null);
    this.previewLoading.set(false);
    this.previewError.set(null);
    this.publishing.set(false);
    this.publishError.set(null);
    this.published.set(null);
    this.publications.set([]);
    this.listError.set(null);
    this.comparison.set(null);
    this.comparing.set(false);
    this.compareError.set(null);
  }
}
