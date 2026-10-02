import { Component, computed, input, output, signal } from '@angular/core';
import {
  KnightAssessment,
  KnightGlossary,
  findingTitle,
  glossaryTermsIn,
  indicatorTexts,
} from '../../models/knight.models';
import { KnightControlRefsComponent } from './knight-control-refs.component';

/**
 * [AEGIS-KNIGHT-PRESENTATION-01] Aba "Glossário" do assessment. Duas coisas diferentes, separadas:
 *   • identificadores internos (AK-…): a consulta procura nos controles DESTA avaliação (não num catálogo paralelo),
 *     diz o que cada um avalia pela descrição registrada com ele e abre o detalhe;
 *   • termos técnicos: as definições do glossário único do servidor — as mesmas do HTML e do PDF.
 */
@Component({
  selector: 'app-knight-glossary',
  standalone: true,
  imports: [KnightControlRefsComponent],
  template: `
    <div class="panel">
      <div class="hd"><h3>Identificadores internos (códigos AK-…)</h3></div>
      <p class="muted small">{{ glossary()?.identifierExplanation }}</p>
      <label class="fl">
        <span class="filter-label">Consultar um código ou título</span>
        <input type="search" [value]="idQuery()" (input)="idQuery.set($any($event.target).value)" (keydown.enter)="openExact()"
               placeholder="Ex.: AK-AZ-IAM-004 ou parte do título" />
      </label>
      <div role="status" aria-live="polite">
        @if (idQuery().trim()) {
          @if (idMatches().length === 0) {
            <p class="muted">Nenhum controle desta avaliação corresponde a “{{ idQuery().trim() }}”.</p>
          } @else {
            <p class="muted small">{{ idMatches().length }} controle(s) encontrado(s). Enter com o código completo abre o controle.</p>
            @for (m of idMatches(); track m.id) {
              <div class="gid">
                <app-knight-control-refs [refs]="[m]" (open)="open.emit($event)" />
                <p class="small">O que avalia: {{ m.description || 'descrição não registrada nesta avaliação; vale o título acima.' }}</p>
              </div>
            }
          }
        }
      </div>
    </div>

    <div class="panel">
      <div class="hd"><h3>Termos técnicos</h3></div>
      @if (!glossary()) {
        <p class="muted">{{ state() === 'error' ? 'O glossário não pôde ser carregado agora.' : 'Carregando…' }}</p>
      } @else {
        <p class="muted small">
          Siglas usadas nos controles, com o significado e uma explicação curta. As marcadas aparecem nesta avaliação.
          As definições são as mesmas do relatório HTML e do PDF.
        </p>
        <label class="fl">
          <span class="filter-label">Pesquisar no glossário</span>
          <input type="search" [value]="termQuery()" (input)="termQuery.set($any($event.target).value)" placeholder="Sigla ou palavra (ex.: MFA, rede)" />
        </label>
        @if (terms().length === 0) {
          <p class="muted">Nenhum termo corresponde à pesquisa.</p>
        } @else {
          <dl class="gl">
            @for (t of terms(); track t.term) {
              <dt>{{ t.term }} @if (used().has(t.term)) { <span class="used">nesta avaliação</span> }</dt>
              <dd><span class="mean">{{ t.meaning }}.</span> {{ t.explanation }}</dd>
            }
          </dl>
        }
      }
    </div>
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; gap: var(--sp-4); }
      .fl { display: flex; flex-direction: column; gap: 4px; max-width: 420px; margin: var(--sp-2) 0; }
      .fl input { min-height: var(--control-h); padding: 0 10px; border: 1px solid var(--line-strong);
        border-radius: var(--radius-sm); background: var(--panel-2); color: var(--text); }
      .fl input:focus-visible { outline: none; box-shadow: var(--field-focus); border-color: var(--cyan); }
      .small { font-size: var(--fs-meta); }
      .gid { padding: var(--sp-2) 0; border-top: 1px solid var(--line); }
      .gid p { margin: 4px 0 0; color: var(--text-2); }
      .gl { margin: 0; }
      .gl dt { font-weight: 700; margin-top: var(--sp-2); }
      .gl dd { margin: 2px 0 0; color: var(--text-2); font-size: var(--fs-sm); }
      .mean { color: var(--text); }
      .used { margin-left: 6px; padding: 1px 8px; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600;
        color: var(--cyan); background: var(--tint-cyan); }
    `,
  ],
})
export class KnightGlossaryComponent {
  readonly assessment = input.required<KnightAssessment>();
  readonly glossary = input<KnightGlossary | null>(null);
  readonly state = input<'loading' | 'ok' | 'error'>('loading');
  /** Pedido de abrir UM controle. */
  readonly open = output<string>();

  readonly idQuery = signal('');
  readonly termQuery = signal('');

  readonly idMatches = computed(() => {
    const q = this.idQuery().trim().toLowerCase();
    if (!q) return [];
    return this.assessment()
      .indicators.filter((i) => i.indicatorId.toLowerCase().includes(q) || findingTitle(i).toLowerCase().includes(q) || i.title.toLowerCase().includes(q))
      .map((i) => ({ id: i.indicatorId, title: findingTitle(i) || i.title, description: i.presentation?.description ?? null }));
  });

  /** Siglas que aparecem nos textos desta avaliação — pela mesma regra do servidor. */
  readonly used = computed(() => {
    const terms = this.glossary()?.terms ?? [];
    return new Set(glossaryTermsIn(this.assessment().indicators.flatMap(indicatorTexts), terms).map((t) => t.term));
  });

  readonly terms = computed(() => {
    const q = this.termQuery().trim().toLowerCase();
    return (this.glossary()?.terms ?? []).filter((t) => !q || `${t.term} ${t.meaning} ${t.explanation}`.toLowerCase().includes(q));
  });

  openExact(): void {
    const q = this.idQuery().trim().toUpperCase();
    if (this.assessment().indicators.some((i) => i.indicatorId === q)) this.open.emit(q);
  }
}
