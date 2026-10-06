import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  NIST_FUNCTIONS,
  NIST_STATES,
  NistFunctionMeta,
  NistProfile,
  NistProfileScore,
  NistStateCounts,
  NistSubcategoryState,
  averageText,
  functionSlugOf,
  gapText,
  levelLabel,
  methodLabel,
  nistFunctionTitle,
  profileBasisText,
  severityLabel,
  stateLabel,
} from '../../models/nist.models';

const STATE_KEY: Record<NistSubcategoryState, keyof NistStateCounts> = {
  Evaluated: 'evaluated',
  NotApplicable: 'notApplicable',
  PendingConfirmation: 'pendingConfirmation',
  InProgress: 'inProgress',
  NotEvaluated: 'notEvaluated',
};

/**
 * [AEGIS-NIST-JOURNEY-02] Painel da rodada e escopo: atual × alvo por função (escala 1–5 do AEGIS), andamento por situação
 * com acesso direto às subcategorias, procedimentos por método e tratamento (achados e planos). Sem dado, o painel diz que
 * não há dado — nunca desenha uma barra "zero" por ausência.
 */
@Component({
  selector: 'app-nist-dashboard-panel',
  standalone: true,
  imports: [RouterLink],
  template: `
    @let p = profile();
    <div class="cards">
      <div class="card"><div class="k">Atual (média)</div><div class="v">{{ averageText(p.overall.current) }}</div><div class="muted">escala 1–5 do AEGIS</div></div>
      <div class="card"><div class="k">Alvo (média)</div><div class="v">{{ averageText(p.overall.target) }}</div></div>
      <div class="card"><div class="k">Lacuna média</div><div class="v">{{ gapText(p.overall.gap) }}</div><div class="muted">indeterminada em {{ p.indeterminateGaps }}</div></div>
      <div class="card"><div class="k">Cobertura</div><div class="v">{{ coverage() }}</div><div class="muted">{{ decided() }} de {{ p.overall.subcategories }} decididas</div></div>
    </div>
    <p class="muted">{{ profileBasisText(p.overall) }}. Médias só com avaliações confirmadas; conteúdo herdado ou importado aguarda confirmação e não entra.</p>

    <section class="panel" aria-labelledby="dash-ct">
      <div class="hd"><h3 id="dash-ct">Atual × alvo por função</h3><span class="hint">clique para abrir a função</span></div>
      <ul class="bars">
        @for (f of functions; track f.code) {
          @let s = fnScore(f.code);
          <li>
            <a [routerLink]="['/nist', f.slug]" [queryParams]="params()" class="fn-name"><span class="mono">{{ f.code }}</span> {{ f.label }}</a>
            @if (s && (s.current !== null || s.target !== null)) {
              <span class="bar-row" [attr.aria-label]="'Atual ' + averageText(s.current) + ', alvo ' + averageText(s.target)">
                <span class="track"><span class="fill cur" [style.width.%]="pct(s.current)"></span></span><span class="val">Atual {{ averageText(s.current) }}</span>
                <span class="track"><span class="fill tgt" [style.width.%]="pct(s.target)"></span></span><span class="val">Alvo {{ averageText(s.target) }}</span>
              </span>
            } @else {
              <span class="muted">Sem nível confirmado nesta rodada</span>
            }
          </li>
        }
      </ul>
    </section>

    @if (p.functionStates?.length) {
      <section class="panel" aria-labelledby="dash-st">
        <div class="hd"><h3 id="dash-st">Andamento por função</h3><span class="hint">cada faixa leva às subcategorias naquela situação</span></div>
        <p class="legend">@for (st of states; track st) { <span class="lg"><span class="sw" [attr.data-st]="st"></span>{{ stateLabel(st) }}</span> }</p>
        <ul class="bars">
          @for (fs of p.functionStates!; track fs.code) {
            @let f = meta(fs.code);
            <li>
              <span class="fn-name"><span class="mono">{{ fs.code }}</span> {{ f?.label }}</span>
              <span class="stack">
                @for (st of states; track st) {
                  @if (count(fs, st) > 0) {
                    <a class="seg" [attr.data-st]="st" [style.flex-grow]="count(fs, st)"
                       [routerLink]="['/nist', f?.slug]" [queryParams]="stateParams(st)"
                       [attr.aria-label]="count(fs, st) + ' ' + stateLabel(st) + ' em ' + (f?.label ?? fs.code)" [title]="count(fs, st) + ' · ' + stateLabel(st)">{{ count(fs, st) }}</a>
                  }
                }
              </span>
            </li>
          }
        </ul>
      </section>
    }

    <div class="split">
      <section class="panel" aria-labelledby="dash-pr">
        <div class="hd"><h3 id="dash-pr">Procedimentos de avaliação</h3><span class="hint">método planejado ≠ resultado</span></div>
        @if (!p.procedures?.length || totalProcedures() === 0) {
          <p class="muted">Nenhum procedimento planejado nesta rodada e escopo.</p>
        } @else {
          <div class="table-wrap"><table class="data-table">
            <caption class="sr-only">Procedimentos por método</caption>
            <thead><tr><th scope="col">Método</th><th scope="col">Planejados</th><th scope="col">Em execução</th><th scope="col">Realizados</th><th scope="col">Não realizados</th><th scope="col">Insatisfatórios</th></tr></thead>
            <tbody>
              @for (m of p.procedures!; track m.method) {
                <tr><td>{{ methodLabel(m.method) }}</td><td>{{ m.planned }}</td><td>{{ m.inProgress }}</td><td>{{ m.performed }}</td><td>{{ m.notPerformed }}</td>
                  <td>{{ m.unsatisfactory }}@if (m.partiallySatisfactory) { <span class="muted"> + {{ m.partiallySatisfactory }} parciais</span> }</td></tr>
              }
            </tbody>
          </table></div>
        }
      </section>

      <section class="panel" aria-labelledby="dash-tr">
        <div class="hd"><h3 id="dash-tr">Tratamento</h3><a class="hint" [routerLink]="[]" [queryParams]="tabParams('achados')">ver achados e planos</a></div>
        @if (p.treatment; as t) {
          @if (t.findingsOpen + t.findingsRiskAccepted + t.findingsClosed === 0) {
            <p class="muted">Nenhum achado registrado. Achados nascem da decisão do analista sobre uma lacuna documentada — não de atual × alvo.</p>
          } @else {
            <ul class="kv">
              <li><a [routerLink]="[]" [queryParams]="tabParams('achados', 'Open')">{{ t.findingsOpen }} achado(s) aberto(s)</a>
                @if (severities(t.openBySeverity); as sv) { @if (sv) { <span class="muted"> · {{ sv }}</span> } }</li>
              <li>{{ t.findingsRiskAccepted }} com risco aceito · {{ t.findingsClosed }} encerrado(s)</li>
              <li [class.attn]="t.findingsWithoutPlan > 0">{{ t.findingsWithoutPlan }} aberto(s) sem plano de tratamento</li>
              <li>Planos: {{ t.plansOpen }} abertos · {{ t.plansInProgress }} em andamento · {{ t.plansAwaitingValidation }} aguardando validação · {{ t.plansCompleted }} concluídos</li>
              <li [class.attn]="t.plansOverdue > 0">{{ t.plansOverdue }} plano(s) com prazo vencido</li>
            </ul>
            <p class="hint">Concluir um plano não muda a maturidade: a reavaliação da subcategoria é um ato separado.</p>
          }
        } @else {
          <p class="muted">Sem dados de tratamento.</p>
        }
      </section>
    </div>

    <section class="panel" aria-labelledby="dash-gaps">
      <div class="hd"><h3 id="dash-gaps">Maiores lacunas</h3><span class="hint">atual e alvo confirmados</span></div>
      @if (p.gaps.length === 0) {
        <p class="muted">Nenhuma lacuna determinável ainda: registre e confirme atual e alvo nas subcategorias.</p>
      } @else {
        <div class="table-wrap"><table class="data-table">
          <caption class="sr-only">Maiores lacunas</caption>
          <thead><tr><th scope="col">Resultado</th><th scope="col">Atual</th><th scope="col">Alvo</th><th scope="col">Lacuna</th><th scope="col">Achados abertos</th><th scope="col">Responsável</th></tr></thead>
          <tbody>
            @for (g of p.gaps.slice(0, 12); track g.code) {
              <tr>
                <td><a [routerLink]="['/nist', slug(g.code), g.code]" [queryParams]="params()">{{ g.title }}</a> <span class="mono muted">{{ g.code }}</span>
                  @if (g.riskImpact) { <span class="muted block">Risco: {{ g.riskImpact }}</span> }</td>
                <td>{{ levelLabel(g.currentLevel) }}</td><td>{{ levelLabel(g.targetLevel) }}</td><td><strong>{{ gapText(g.gap) }}</strong></td>
                <td>@if (g.openFindings) { <a [routerLink]="['/nist', slug(g.code), g.code]" [queryParams]="params()" fragment="achados">{{ g.openFindings }}</a> } @else { 0 }</td>
                <td>{{ g.ownerName ?? '—' }}</td>
              </tr>
            }
          </tbody>
        </table></div>
      }
    </section>
  `,
  styles: [
    `
      .bars { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--sp-3); }
      .bars li { display: grid; grid-template-columns: minmax(120px, 180px) 1fr; gap: var(--sp-3); align-items: center; }
      .fn-name { font-weight: 500; }
      .bar-row { display: grid; grid-template-columns: 1fr auto; gap: 4px var(--sp-2); align-items: center; }
      .track { height: 10px; background: var(--line-2); border-radius: 5px; overflow: hidden; }
      .fill { display: block; height: 100%; }
      .fill.cur { background: var(--accent, #3b82f6); }
      .fill.tgt { background: var(--muted, #94a3b8); }
      .val { font-size: var(--fs-meta); min-width: 64px; }
      .stack { display: flex; min-height: 22px; border-radius: 6px; overflow: hidden; }
      .seg { flex: 1 1 0; min-width: 22px; text-align: center; font-size: var(--fs-meta); color: #fff; text-decoration: none; line-height: 22px; }
      .seg:focus-visible { outline: 2px solid var(--text); outline-offset: -2px; }
      [data-st='Evaluated'] { background: #2563eb; }
      [data-st='NotApplicable'] { background: #64748b; }
      [data-st='PendingConfirmation'] { background: #7c3aed; }
      [data-st='InProgress'] { background: #b45309; }
      [data-st='NotEvaluated'] { background: #94a3b8; color: #0f172a; }
      .legend { display: flex; flex-wrap: wrap; gap: var(--sp-3); font-size: var(--fs-meta); margin: 0 0 var(--sp-2); }
      .lg { display: inline-flex; align-items: center; gap: 4px; }
      .sw { width: 10px; height: 10px; border-radius: 2px; display: inline-block; }
      .split { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 340px), 1fr)); gap: var(--sp-4); }
      .kv { margin: 0 0 var(--sp-2); padding-left: 18px; }
      .attn { font-weight: 600; }
      .block { display: block; }
      @media (max-width: 520px) { .bars li { grid-template-columns: 1fr; } }
    `,
  ],
})
export class NistDashboardPanelComponent {
  readonly profile = input.required<NistProfile>();
  readonly params = input.required<Record<string, string>>();

  protected readonly functions = NIST_FUNCTIONS;
  protected readonly states = NIST_STATES;
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly levelLabel = levelLabel;
  protected readonly stateLabel = stateLabel;
  protected readonly methodLabel = methodLabel;
  protected readonly profileBasisText = profileBasisText;
  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly slug = functionSlugOf;

  protected readonly decided = computed(() => this.profile().overall.evaluated + this.profile().overall.notApplicable);
  protected readonly coverage = computed(() => {
    const o = this.profile().overall;
    if (o.subcategories <= 0) return '—';
    return `${(Math.round((this.decided() / o.subcategories) * 1000) / 10).toLocaleString('pt-BR')}%`;
  });
  protected readonly totalProcedures = computed(() =>
    (this.profile().procedures ?? []).reduce((n, m) => n + m.planned + m.inProgress + m.performed + m.notPerformed, 0),
  );

  protected fnScore(code: string): NistProfileScore | null {
    return this.profile().functions.find((f) => f.code === code) ?? null;
  }

  protected meta(code: string): NistFunctionMeta | null {
    return NIST_FUNCTIONS.find((f) => f.code === code) ?? null;
  }

  protected pct(v: number | null): number {
    return v === null ? 0 : Math.max(0, Math.min(100, (v / 5) * 100));
  }

  protected count(fs: NistStateCounts, st: NistSubcategoryState): number {
    return fs[STATE_KEY[st]] as number;
  }

  protected stateParams(st: NistSubcategoryState): Record<string, string> {
    return { ...this.params(), situacao: st };
  }

  protected tabParams(tab: string, status?: string): Record<string, string | null> {
    return { ...this.params(), aba: tab, status: status ?? null };
  }

  protected severities(by: Record<string, number>): string {
    return Object.entries(by)
      .filter(([, n]) => n > 0)
      .map(([k, n]) => `${n} ${severityLabel(k.charAt(0).toUpperCase() + k.slice(1)).toLowerCase()}`)
      .join(' · ');
  }
}
