import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import { NistImportPreview, NistImportResult, NistImportRow, importActionLabel } from '../../models/nist.models';
import { NistApiError, NistCtx, NistService, saveBlob } from '../../services/nist.service';

/** Mesmo limite do servidor (o servidor continua sendo a autoridade). */
const MAX_BYTES = 2_000_000;

/**
 * [AEGIS-NIST-JOURNEY-02] Importação CSV da rodada e escopo em dois passos: PRÉVIA (nada é gravado; erros e conflitos por
 * linha; regra de atualização dita) e APLICAÇÃO (tudo ou nada, amarrada ao token da prévia — se o arquivo ou qualquer
 * registro mudar no meio, nada é gravado). Conteúdo importado entra como rascunho a confirmar, nunca como revisão humana.
 */
@Component({
  selector: 'app-nist-import-panel',
  standalone: true,
  template: `
    <section class="panel" aria-labelledby="imp-h">
      <div class="hd"><h3 id="imp-h">Importar CSV de trabalho</h3><span class="hint">prévia antes de gravar</span></div>
      <ol class="steps">
        <li>Baixe o CSV de trabalho desta rodada e escopo (todas as subcategorias, valores vigentes e versão de cada registro).
          <button type="button" class="ghost xs" (click)="downloadTemplate()" [disabled]="downloading()">{{ downloading() ? 'Baixando…' : 'Baixar CSV de trabalho' }}</button></li>
        <li>Preencha as colunas (código e título identificam cada linha; células vazias mantêm o valor atual).</li>
        <li>Envie o arquivo para a prévia, confira linha a linha e só então aplique.</li>
      </ol>
      @if (closed()) {
        <p class="notice warn">Esta rodada está encerrada: a importação está bloqueada.</p>
      } @else if (!canWrite()) {
        <p class="notice">Seu papel permite baixar o CSV, mas não importar (requer Manager ou TenantAdmin).</p>
      } @else {
        <label class="field"><span class="field-label">Arquivo CSV</span>
          <input type="file" accept=".csv,text/csv" (change)="pick($event)" [disabled]="busy()" /></label>
      }
      @if (error(); as e) { <p class="notice error" role="alert">{{ e }}</p> }

      @if (preview(); as pv) {
        <h4>Prévia de {{ pv.fileName ?? 'arquivo' }} — nada foi gravado</h4>
        <p>{{ pv.rows }} linha(s): <strong>{{ pv.creates }}</strong> criam · <strong>{{ pv.updates }}</strong> atualizam · {{ pv.unchanged }} sem mudança ·
          <strong [class.bad]="pv.errors > 0">{{ pv.errors }} com erro</strong> · <strong [class.bad]="pv.conflicts > 0">{{ pv.conflicts }} em conflito de versão</strong></p>
        @if (pv.fileErrors.length) { <div class="notice error" role="alert"><ul>@for (f of pv.fileErrors; track f) { <li>{{ f }}</li> }</ul></div> }
        <p class="hint">{{ pv.updateRule }}</p>
        <label class="chk"><input type="checkbox" [checked]="onlyRelevant()" (change)="onlyRelevant.set(!onlyRelevant())" /> mostrar só linhas com mudança, erro ou conflito</label>
        <div class="table-wrap"><table class="data-table">
          <caption class="sr-only">Linhas da prévia</caption>
          <thead><tr><th scope="col">Linha</th><th scope="col">Subcategoria</th><th scope="col">Resultado</th><th scope="col">Detalhes</th></tr></thead>
          <tbody>
            @for (r of rows(); track r.line) {
              <tr>
                <td>{{ r.line }}</td>
                <td><span class="mono">{{ r.code ?? '—' }}</span>@if (r.title) { <span class="muted block">{{ r.title }}</span> }</td>
                <td><span [class]="'badge ' + badge(r)">{{ actionLabel(r.action) }}</span></td>
                <td>
                  @for (m of r.messages; track m) { <p class="msg">{{ m }}</p> }
                  @for (c of r.changes; track c.field) { <p class="msg"><strong>{{ c.label }}:</strong> {{ c.from ?? '—' }} → {{ c.to ?? '—' }}</p> }
                </td>
              </tr>
            }
          </tbody>
        </table></div>
        <div class="actions">
          <button type="button" class="primary" (click)="apply(pv)" [disabled]="busy() || !pv.canApply">{{ busy() ? 'Aplicando…' : 'Aplicar importação' }}</button>
          <button type="button" class="ghost sm" (click)="discard()" [disabled]="busy()">Descartar</button>
          @if (!pv.canApply) { <span class="muted">Corrija os erros e conflitos e envie de novo: nada será gravado enquanto houver problema.</span> }
        </div>
      }

      @if (result(); as r) {
        <p class="notice" role="status">Importação aplicada: {{ r.created }} criada(s), {{ r.updated }} atualizada(s), {{ r.unchanged }} sem mudança.
          O conteúdo importado aguarda confirmação humana na tela de cada subcategoria e não entra nas médias até lá.</p>
      }
    </section>
  `,
  styles: [
    `
      .steps { margin: 0 0 var(--sp-3); padding-left: 18px; }
      .steps li { margin-bottom: 6px; }
      .chk { display: inline-flex; align-items: center; gap: 6px; font-size: var(--fs-sm); margin: var(--sp-2) 0; }
      .msg { margin: 0; overflow-wrap: anywhere; }
      .block { display: block; }
      .bad { color: var(--bad, #b91c1c); }
      .actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); align-items: center; margin-top: var(--sp-3); }
      h4 { margin: var(--sp-4) 0 var(--sp-2); }
    `,
  ],
})
export class NistImportPanelComponent {
  private readonly nist = inject(NistService);

  readonly ctx = input.required<NistCtx>();
  readonly canWrite = input(false);
  readonly closed = input(false);
  readonly applied = output<NistImportResult>();

  protected readonly actionLabel = importActionLabel;
  protected readonly preview = signal<NistImportPreview | null>(null);
  protected readonly result = signal<NistImportResult | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly downloading = signal(false);
  protected readonly onlyRelevant = signal(true);
  protected readonly rows = computed(() => {
    const items = this.preview()?.items ?? [];
    return this.onlyRelevant() ? items.filter((r) => r.action !== 'Unchanged') : items;
  });

  /** Arquivo da prévia: a aplicação reenvia exatamente o mesmo conteúdo com o token. */
  private csv = '';
  private fileName: string | null = null;
  private gen = 0;
  private sub: Subscription | null = null;
  private readonly key = computed(() => `${this.ctx().assessmentId}|${this.ctx().cycleId}|${this.ctx().scopeId}`);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clear());
    effect(() => {
      this.key();
      untracked(() => this.clear());
    });
  }

  protected badge(r: NistImportRow): string {
    return r.action === 'Error' ? 'bad' : r.action === 'Conflict' ? 'warn' : r.action === 'Unchanged' ? 'neutral' : 'info';
  }

  protected downloadTemplate(): void {
    this.downloading.set(true);
    this.error.set(null);
    this.nist.workingCsv(this.ctx()).subscribe({
      next: (f) => {
        saveBlob(f.blob, f.filename);
        this.downloading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.downloading.set(false);
      },
    });
  }

  protected async pick(ev: Event): Promise<void> {
    const inputEl = ev.target as HTMLInputElement;
    const file = inputEl.files?.[0];
    inputEl.value = '';
    if (!file) return;
    this.error.set(null);
    this.result.set(null);
    this.preview.set(null);
    if (file.size > MAX_BYTES) {
      this.error.set('Arquivo acima de 2 MB: divida a importação.');
      return;
    }
    const gen = this.gen;
    const text = await file.text();
    if (gen !== this.gen) return;
    this.csv = text;
    this.fileName = file.name;
    this.busy.set(true);
    this.sub = this.nist.importPreview(this.ctx(), text, file.name).subscribe({
      next: (p) => {
        if (gen !== this.gen) return;
        this.preview.set(p);
        this.busy.set(false);
      },
      error: (e: Error) => {
        if (gen !== this.gen) return;
        this.error.set(e.message);
        this.busy.set(false);
      },
    });
  }

  protected apply(pv: NistImportPreview): void {
    if (this.busy() || !pv.canApply) return;
    const gen = this.gen;
    this.busy.set(true);
    this.error.set(null);
    this.sub = this.nist.importApply(this.ctx(), this.csv, this.fileName, pv.token).subscribe({
      next: (r) => {
        if (gen !== this.gen) return;
        this.busy.set(false);
        this.preview.set(null);
        this.result.set(r);
        this.applied.emit(r);
      },
      error: (e: NistApiError) => {
        if (gen !== this.gen) return;
        this.busy.set(false);
        this.error.set(
          e.status === 409 ? `${e.message} Nada foi gravado: gere uma nova prévia com o arquivo atualizado.` : e.message,
        );
        if (e.status === 409) this.preview.set(null);
      },
    });
  }

  protected discard(): void {
    this.preview.set(null);
    this.csv = '';
    this.fileName = null;
  }

  private clear(): void {
    this.gen++;
    this.sub?.unsubscribe();
    this.sub = null;
    this.preview.set(null);
    this.result.set(null);
    this.error.set(null);
    this.busy.set(false);
    this.csv = '';
    this.fileName = null;
  }
}
