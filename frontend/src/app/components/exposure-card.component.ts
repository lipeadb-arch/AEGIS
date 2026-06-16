import { Component, input } from '@angular/core';

@Component({
  selector: 'app-exposure-card',
  standalone: true,
  template: `
    <div
      class="card"
      [class.danger]="tone() === 'danger'"
      [class.warn]="tone() === 'warn'"
      [class.ok]="tone() === 'ok'"
    >
      <div class="k">{{ label() }}</div>
      <div class="v">
        {{ value() }}@if (suffix()) {<small> {{ suffix() }}</small>}
      </div>
    </div>
  `,
})
export class ExposureCardComponent {
  label = input.required<string>();
  value = input.required<string | number>();
  suffix = input<string | undefined>(undefined);
  tone = input<'danger' | 'warn' | 'ok' | ''>('');
}
