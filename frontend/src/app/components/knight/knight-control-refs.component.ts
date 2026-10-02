import { Component, computed, input, output, signal } from '@angular/core';
import { KnightControlRef } from '../../models/knight.models';

let nextId = 0;

/**
 * [AEGIS-KNIGHT-PRESENTATION-01] Controles citados fora do próprio detalhe: o título DESTA avaliação como informação
 * principal e o código como secundária. Cada entrada é um botão (clique, Enter ou Espaço) que abre o detalhe. Listas
 * longas mostram uma prévia com o total e um botão de expansão — nenhum controle fica oculto de forma definitiva.
 */
@Component({
  selector: 'app-knight-control-refs',
  standalone: true,
  template: `
    <ul class="refs" [id]="listId">
      @for (r of shown(); track r.id) {
        <li>
          <button type="button" class="ref" (click)="open.emit(r.id)"
                  [attr.aria-label]="(r.title ? r.title + ' (' + r.id + ')' : 'Controle ' + r.id) + ': abrir o detalhe'">
            <span class="rt">{{ r.title || 'Título não disponível nesta avaliação' }}</span>
            <span class="rc">{{ r.id }}</span>
          </button>
        </li>
      }
    </ul>
    @if (refs().length > preview()) {
      <button type="button" class="more" [attr.aria-expanded]="expanded()" [attr.aria-controls]="listId" (click)="expanded.set(!expanded())">
        {{ expanded() ? 'Mostrar só os ' + preview() + ' primeiros' : 'Mostrar todos os ' + refs().length + ' controles' }}
      </button>
    }
  `,
  styles: [
    `
      .refs { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
      .ref { display: block; width: 100%; padding: 4px 8px; text-align: left; color: var(--text); background: none;
        border: 1px solid var(--line); border-radius: var(--radius-sm); cursor: pointer; }
      .ref:hover { border-color: var(--cyan); }
      .ref:focus-visible, .more:focus-visible { outline: none; box-shadow: var(--focus); }
      .rt { display: block; font-size: var(--fs-sm); }
      .rc { display: block; font-family: var(--mono); font-size: var(--fs-caps); color: var(--muted); }
      .more { margin-top: 4px; padding: 2px 0; border: 0; background: none; color: var(--cyan); text-decoration: underline;
        font-size: var(--fs-meta); cursor: pointer; }
    `,
  ],
})
export class KnightControlRefsComponent {
  readonly refs = input.required<KnightControlRef[]>();
  readonly preview = input(3);
  readonly open = output<string>();

  protected readonly listId = `knight-refs-${++nextId}`;
  readonly expanded = signal(false);
  readonly shown = computed(() => (this.expanded() ? this.refs() : this.refs().slice(0, this.preview())));
}
