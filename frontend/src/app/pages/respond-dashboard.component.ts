import { Component } from '@angular/core';

@Component({
  selector: 'app-respond-dashboard',
  standalone: true,
  template: `
    <header class="head">
      <h1>Respond <span class="code">RS</span></h1>
      <p>Resposta a incidentes — Análise e Mitigação (RS.MA/RS.MI).</p>
    </header>
    <section class="card">
      <span class="tag">Em construção</span>
      <p>Aqui virão os gráficos de conformidade e a listagem de controles NonCompliant.</p>
    </section>
  `,
  styles: [`
    :host { display:block; padding:28px 32px; }
    .head h1 { font-family:var(--sans); font-size:22px; color:var(--text); margin:0 0 4px; }
    .head .code { font-family:var(--mono); font-size:13px; color:var(--cyan); margin-left:6px; }
    .head p { color:var(--muted); font-size:13px; margin:0 0 20px; }
    .card { border:1px solid var(--line); border-radius:14px; padding:28px; background:rgba(122,145,190,.04); min-height:180px; }
    .card .tag { font-family:var(--mono); font-size:10px; letter-spacing:.14em; text-transform:uppercase; color:var(--cyan); }
    .card p { color:var(--muted); font-size:13px; margin:10px 0 0; }
  `],
})
export class RespondDashboardComponent {}
