import { Component, DestroyRef, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Subscription, forkJoin } from 'rxjs';
import { navLevelText, navTitle } from '../../models/nist-workspace.models';
import {
  NistFunctionMeta,
  NistFunctionView,
  cycleLabel,
  nistCategoryLabel,
  nistFunctionTitle,
  reviewStateLabel,
  stateBadgeClass,
  stateLabel,
} from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { NistCtx, NistService } from '../../services/nist.service';

/** Nomes do contexto (avaliação · rodada · escopo) para o cabeçalho compacto do detalhe. */
export interface NistWorkspaceContext {
  assessmentId: string;
  cycleId: string;
  scopeId: string;
  assessment: string;
  cycle: string;
  cycleClosed: boolean;
  scope: string;
}

/**
 * [AEGIS-NIST-RECOVER-GUIDE-01] Lista contextual das subcategorias de UMA função, ao lado do detalhe (coluna intermediária da
 * área de trabalho). Lê a MESMA visão da função (`functionView`) que a tela da função e os painéis consomem — nenhum dado
 * novo. Cada item abre a rota existente da subcategoria com a mesma avaliação, rodada e escopo; em tela estreita a lista vira
 * um seletor. O componente sobrevive à troca de subcategoria (a posição da lista se mantém); só uma troca de contexto
 * (tenant · função · avaliação · rodada · escopo) relê tudo, e respostas de um contexto substituído são descartadas.
 */
@Component({
  selector: 'app-nist-subcategory-nav',
  standalone: true,
  imports: [RouterLink],
  template: `
    @if (fnMeta; as f) {
      <nav class="ctx" [attr.aria-label]="'Subcategorias de ' + nistFunctionTitle(f)">
        <div class="ctx-head">
          <p class="ctx-eyebrow">{{ f.code }} · função em avaliação</p>
          <p class="ctx-title">{{ nistFunctionTitle(f) }}</p>
          @if (view(); as v) { <p class="hint">{{ total(v) }} subcategorias · {{ v.profile.evaluated }} avaliada(s)</p> }
        </div>
        @if (view(); as v) {
          <label class="field picker"><span class="field-label">Subcategoria</span>
            <select (change)="pick($event)" aria-describedby="ctx-picker-hint">
              @for (c of v.categories; track c.code) {
                <optgroup [label]="c.code + ' — ' + nistCategoryLabel(c.code, c.name)">
                  @for (s of c.subcategories; track s.code) {
                    <option [value]="s.code" [selected]="s.code === current">{{ s.code }} — {{ navTitle(s) }} ({{ stateLabel(s.state) }})</option>
                  }
                </optgroup>
              }
            </select>
            <span class="hint" id="ctx-picker-hint">Escolher abre a subcategoria na mesma avaliação, rodada e escopo.</span>
          </label>
          <div class="groups">
            @for (c of v.categories; track c.code) {
              <section class="grp" [attr.aria-labelledby]="'ctx-grp-' + c.code">
                <h2 class="grp-h" [id]="'ctx-grp-' + c.code"><span>{{ nistCategoryLabel(c.code, c.name) }}</span><span class="mono">{{ c.code }}</span></h2>
                <ul>
                  @for (s of c.subcategories; track s.code) {
                    <li>
                      <a class="item" [class.on]="s.code === current" [attr.aria-current]="s.code === current ? 'page' : null"
                        [routerLink]="['/nist', f.slug, s.code]" [queryParams]="params()">
                        <span class="row1"><span class="mono code">{{ s.code }}</span>
                          <span [class]="'badge ' + stateBadgeClass(s.state)">{{ stateLabel(s.state) }}</span></span>
                        <span class="t">{{ navTitle(s) }}</span>
                        @if (navLevelText(s); as lv) { <span class="lv">{{ lv }}</span> }
                        @if (s.reviewState && s.reviewState !== 'None') { <span class="lv">{{ reviewStateLabel(s.reviewState) }}</span> }
                      </a>
                    </li>
                  }
                </ul>
              </section>
            }
          </div>
          @if (softError()) { <p class="hint" role="status">{{ softError() }}</p> }
        } @else if (error()) {
          <p class="err" role="alert">{{ error() }}</p>
        } @else {
          <p class="hint" role="status"><span class="spinner" aria-hidden="true"></span> Carregando a lista…</p>
        }
      </nav>
    }
  `,
  styles: [
    `
      :host { display: block; min-width: 0; }
      .ctx { display: flex; flex-direction: column; gap: var(--sp-3); padding: var(--sp-4) var(--sp-3); border: 1px solid var(--line);
        border-radius: var(--radius-lg); background: var(--panel); }
      .ctx-head p { margin: 0; }
      .ctx-eyebrow { font-size: var(--fs-caps); font-weight: 600; letter-spacing: var(--tracking-caps); text-transform: uppercase; color: var(--cyan); }
      .ctx-title { font-weight: 600; font-size: var(--fs-panel); margin-top: 2px !important; }
      .grp + .grp { margin-top: var(--sp-3); }
      .grp-h { display: flex; justify-content: space-between; gap: var(--sp-2); margin: 0 0 var(--sp-1); padding: 0 var(--sp-2);
        font-size: var(--fs-caps); font-weight: 600; letter-spacing: var(--tracking-caps); text-transform: uppercase; color: var(--muted); }
      ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
      .item { display: flex; flex-direction: column; gap: 3px; padding: var(--sp-2); border-radius: var(--radius-sm);
        border-left: 3px solid transparent; color: var(--text-2); text-decoration: none; }
      .item:hover { background: var(--hover); color: var(--text); }
      .item:focus-visible { outline: none; box-shadow: var(--focus); }
      .item.on { background: var(--tint-cyan); border-left-color: var(--cyan); color: var(--text); }
      .row1 { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 4px var(--sp-2); }
      .code { font-size: var(--fs-meta); color: var(--text-2); }
      .item.on .code { color: var(--cyan); font-weight: 600; }
      .t { font-size: var(--fs-sm); line-height: 1.35; font-weight: 500; overflow-wrap: anywhere; }
      .lv { font-size: var(--fs-meta); color: var(--muted); }
      .picker { display: none; }
      .picker select { width: 100%; }
      .err { color: var(--red-text); font-size: var(--fs-sm); margin: 0; }
      /* Área de trabalho estreita: a lista vira seletor acessível (sem três colunas comprimidas). */
      @container nistws (max-width: 899px) {
        .ctx { padding: var(--sp-3); }
        .picker { display: flex; flex-direction: column; gap: 4px; }
        .groups { display: none; }
      }
    `,
  ],
})
export class NistSubcategoryNavComponent {
  private readonly nist = inject(NistService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly nistCategoryLabel = nistCategoryLabel;
  protected readonly stateLabel = stateLabel;
  protected readonly stateBadgeClass = stateBadgeClass;
  protected readonly reviewStateLabel = reviewStateLabel;
  protected readonly navTitle = navTitle;
  protected readonly navLevelText = navLevelText;

  protected readonly view = signal<NistFunctionView | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly softError = signal<string | null>(null);
  protected readonly params = signal<Record<string, string>>({});

  /** Nomes do contexto, quando lidos — o detalhe os mostra no cabeçalho compacto (só se ainda for o contexto aberto). */
  @Output() readonly context = new EventEmitter<NistWorkspaceContext>();

  protected fnMeta: NistFunctionMeta | null = null;
  private ctxValue: NistCtx | null = null;
  protected current = '';
  private key: string | null = null;
  private ticket = 0;
  private sub: Subscription | null = null;
  private lastRefresh: number | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.ticket++;
      this.sub?.unsubscribe();
    });
  }

  @Input({ required: true }) set fn(v: NistFunctionMeta) {
    this.fnMeta = v;
    this.sync();
  }

  @Input({ required: true }) set ctx(v: NistCtx) {
    this.ctxValue = v;
    this.sync();
  }

  /** Subcategoria aberta no detalhe (destacada na lista e no seletor). */
  @Input() set code(v: string) {
    this.current = (v ?? '').toUpperCase();
  }

  /** Muda depois de uma gravação no detalhe: relê só a visão da função, sem limpar a lista exibida. */
  @Input() set refresh(n: number) {
    const first = this.lastRefresh === undefined;
    if (n === this.lastRefresh) return;
    this.lastRefresh = n;
    if (!first) this.reloadView();
  }

  protected total(v: NistFunctionView): number {
    return v.categories.reduce((n, c) => n + c.subcategories.length, 0);
  }

  /** Seletor (tela estreita): abre a rota da subcategoria; se a navegação for recusada (rascunho mantido), volta a seleção. */
  protected pick(event: Event): void {
    const el = event.target as HTMLSelectElement;
    const f = this.fnMeta;
    const code = el.value;
    if (!f || !code || code === this.current) return;
    void this.router.navigate(['/nist', f.slug, code], { queryParams: this.params() }).then((done) => {
      if (!done) el.value = this.current;
    });
  }

  private sync(): void {
    const f = this.fnMeta;
    const c = this.ctxValue;
    if (!f || !c) return;
    const tenant = this.auth.activeTenantId();
    const key = [tenant, f.code, c.assessmentId, c.cycleId, c.scopeId].join('|');
    if (key === this.key) return;
    this.key = key;
    const ticket = ++this.ticket;
    this.sub?.unsubscribe();
    this.view.set(null);
    this.error.set(null);
    this.softError.set(null);
    this.params.set({ avaliacao: c.assessmentId, rodada: c.cycleId, escopo: c.scopeId });
    this.sub = forkJoin([this.nist.list(), this.nist.functionView(c, f.code)]).subscribe({
      next: ([list, v]) => {
        if (!this.live(ticket, tenant) || !this.sameContext(v, c, f)) return;
        this.view.set(v);
        const a = list.find((x) => x.id === c.assessmentId);
        const cy = a?.cycles?.find((x) => x.id === c.cycleId);
        const sc = a?.scopes.find((x) => x.id === c.scopeId);
        if (a) this.context.emit({ assessmentId: c.assessmentId, cycleId: c.cycleId, scopeId: c.scopeId, assessment: a.name, cycle: cy ? cycleLabel(cy) : '—', cycleClosed: cy?.status === 'Closed', scope: sc?.name ?? '—' });
      },
      error: (e: Error) => {
        if (this.live(ticket, tenant)) this.error.set(e.message);
      },
    });
  }

  /** Releitura após gravação: a lista exibida continua até a resposta; erro não apaga o que já se sabe. */
  private reloadView(): void {
    const f = this.fnMeta;
    const c = this.ctxValue;
    if (!f || !c || !this.view()) return;
    const tenant = this.auth.activeTenantId();
    const ticket = ++this.ticket;
    this.sub?.unsubscribe();
    this.sub = this.nist.functionView(c, f.code).subscribe({
      next: (v) => {
        if (!this.live(ticket, tenant) || !this.sameContext(v, c, f)) return;
        this.softError.set(null);
        this.view.set(v);
      },
      error: () => {
        if (this.live(ticket, tenant)) this.softError.set('Não foi possível atualizar a situação da lista.');
      },
    });
  }

  private live(ticket: number, tenant: string | null): boolean {
    return ticket === this.ticket && tenant === this.auth.activeTenantId();
  }

  /** A visão é da função, do escopo e da rodada pedidos (defesa além do ticket). */
  private sameContext(v: NistFunctionView, c: NistCtx, f: NistFunctionMeta): boolean {
    return v.code === f.code && v.assessmentId === c.assessmentId && v.scopeId === c.scopeId && (!v.cycleId || v.cycleId === c.cycleId);
  }
}
