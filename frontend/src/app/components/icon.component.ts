import { Component, computed, input } from '@angular/core';

/**
 * Ícones de linha do AEGIS — traço único, grade 24×24, herdando `currentColor`. Desenhados à mão para o produto
 * (sem biblioteca de ícones): cada nome é um conjunto de caminhos simples, legíveis entre 16 e 20 px.
 * Decorativos por padrão (`aria-hidden`): o rótulo acessível fica com o controle que contém o ícone.
 */
const PATHS: Record<string, readonly string[]> = {
  overview: ['M4 4h6.5v6.5H4z', 'M13.5 4H20v6.5h-6.5z', 'M4 13.5h6.5V20H4z', 'M13.5 13.5H20V20h-6.5z'],
  priorities: [
    'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z',
    'M12 7.5a4.5 4.5 0 1 0 0 9a4.5 4.5 0 1 0 0-9z',
    'M12 11.2a.8.8 0 1 0 0 1.6a.8.8 0 1 0 0-1.6z',
  ],
  assets: ['M4 4.5h16v6H4z', 'M4 13.5h16v6H4z', 'M7.5 7.5h.01', 'M7.5 16.5h.01', 'M11 7.5h5', 'M11 16.5h5'],
  vulnerabilities: ['M12 3.5l9 16H3z', 'M12 9.5v4.5', 'M12 17h.01'],
  recommendations: ['M4 7h9', 'M17 7h3', 'M15 4.5v5', 'M4 17h3', 'M11 17h9', 'M9 14.5v5'],
  identity: ['M12 3.5a4 4 0 1 0 0 8a4 4 0 1 0 0-8z', 'M4.5 20.5a7.5 7.5 0 0 1 15 0'],
  controls: ['M10 6h10', 'M10 12h10', 'M10 18h10', 'M4 6l1.2 1.2L7.5 5', 'M4 12l1.2 1.2L7.5 11', 'M4 18l1.2 1.2L7.5 17'],
  documents: ['M6 3h8l4 4v14H6z', 'M14 3v4h4', 'M9 12h6', 'M9 16h6'],
  history: ['M3.5 12a8.5 8.5 0 1 0 2.5-6', 'M3.5 4v4h4', 'M12 8v4.5l3 2'],
  trend: ['M3 17l6-6 4 4 8-8', 'M15 7h6v6'],
  settings: [
    'M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z',
    'M12 2.5v3', 'M12 18.5v3', 'M2.5 12h3', 'M18.5 12h3',
    'M5.3 5.3l2.1 2.1', 'M16.6 16.6l2.1 2.1', 'M5.3 18.7l2.1-2.1', 'M16.6 7.4l2.1-2.1',
  ],
  external: ['M14 4h6v6', 'M20 4l-9 9', 'M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5'],
  logout: ['M9 4H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h3', 'M14 16l4-4-4-4', 'M18 12H9'],
  menu: ['M4 6.5h16', 'M4 12h16', 'M4 17.5h16'],
  close: ['M6 6l12 12', 'M18 6L6 18'],
  refresh: ['M20 12a8 8 0 1 1-2.34-5.66', 'M20 4v5h-5'],
  search: ['M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14z', 'M20 20l-4-4'],
  'chevron-down': ['M6 9l6 6 6-6'],
  'arrow-right': ['M5 12h14', 'M13 6l6 6-6 6'],
  shield: ['M12 3l7 3v6c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6z'],
};

export type IconName = keyof typeof PATHS;

@Component({
  selector: 'app-icon',
  standalone: true,
  host: { class: 'icon', 'aria-hidden': 'true' },
  template: `
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round"
      stroke-linejoin="round" focusable="false">
      @for (d of paths(); track $index) {
        <svg:path [attr.d]="d" />
      }
    </svg>
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        flex: none;
        width: var(--icon-size, 18px);
        height: var(--icon-size, 18px);
      }
      svg {
        width: 100%;
        height: 100%;
        display: block;
      }
    `,
  ],
})
export class IconComponent {
  readonly name = input.required<IconName>();
  protected readonly paths = computed(() => PATHS[this.name()] ?? []);
}
