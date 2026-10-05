import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { EMPTY, Subscription, combineLatest, switchMap } from 'rxjs';
import { DetectionCoverageComponent } from '../../components/scoring/detection-coverage.component';
import { DevicePostureComponent } from '../../components/scoring/device-posture.component';
import { NIST_FUNCTION_DESCRIPTIONS } from '../../models/nist-glossary';
import {
  NistFunctionCode,
  NistFunctionMeta,
  NistFunctionView,
  NistSelection,
  averageText,
  gapText,
  levelLabel,
  nistCategoryLabel,
  nistFunctionBySlug,
  nistFunctionTitle,
  profileBasisText,
  resolveSelection,
  selectionParams,
  stateBadgeClass,
  stateLabel,
} from '../../models/nist.models';
import { AgentStateService } from '../../services/agent-state.service';
import { AuthService } from '../../services/auth.service';
import { NistSelectionService, NistService } from '../../services/nist.service';
import { PillarDashboardComponent } from '../pillar-dashboard.component';

interface SupportResource {
  path: string;
  title: string;
  description: string;
}

/** Recursos de apoio por função: telas existentes, reorganizadas como fonte de evidência — não módulos à parte. */
const RESOURCES: Record<NistFunctionCode, SupportResource[]> = {
  GV: [
    { path: '/nist/gv/documentos', title: 'Biblioteca de documentos', description: 'Políticas, normas e procedimentos. A leitura assistida aponta trechos por subcategoria; o vínculo à avaliação é decisão do analista.' },
  ],
  ID: [
    { path: '/nist/id/ativos', title: 'Inventário de ativos', description: 'O que está inventariado, de que fonte e com que criticidade — apoio para a gestão de ativos (ID.AM).' },
    { path: '/nist/id/vulnerabilidades', title: 'Vulnerabilidades', description: 'Problemas abertos por ativo e CVE — apoio para a avaliação de riscos (ID.RA).' },
    { path: '/nist/id/prioridades', title: 'Prioridades de tratamento', description: 'Fila de tratamento por dispositivo e planos de ação — apoio para ID.RA e melhoria (ID.IM).' },
  ],
  PR: [
    { path: '/nist/pr/recomendacoes', title: 'Recomendações de postura', description: 'Recomendações do Microsoft Secure Score: sinal de configuração, não prova por si.' },
    { path: '/knight', title: 'AEGIS KNIGHT', description: 'Assessment técnico de configurações e exposição. Controles mapeados aparecem como evidência disponível nas subcategorias.' },
  ],
  DE: [
    { path: '/nist/de/postura', title: 'Cobertura de detecção', description: 'Regras do SIEM por técnica MITRE ATT&CK, na aba "Postura do ambiente".' },
  ],
  RS: [
    { path: '/nist/gv/documentos', title: 'Planos e procedimentos de resposta', description: 'Evidências documentais de resposta a incidentes entram pela biblioteca de documentos.' },
  ],
  RC: [
    { path: '/nist/gv/documentos', title: 'Planos de continuidade e recuperação', description: 'Planos e registros de testes entram como documentos da biblioteca.' },
  ],
};

/**
 * [AEGIS-NIST-JOURNEY-01] Uma função do NIST CSF 2.0 no AEGIS NIST, em três abas:
 *   • Avaliação — TODAS as categorias e subcategorias do catálogo, avaliadas ou não, com atual, alvo e lacuna;
 *   • Postura do ambiente — o score de postura (AEGIS Score) dos controles da função, que é outro conceito;
 *   • Recursos de apoio — as telas que fornecem evidência para a função.
 */
@Component({
  selector: 'app-nist-function',
  standalone: true,
  imports: [RouterLink, DatePipe, PillarDashboardComponent, DevicePostureComponent, DetectionCoverageComponent],
  template: `
    @if (fn(); as f) {
      <section class="page">
        <nav class="crumbs" aria-label="Você está em">
          <a [routerLink]="['/nist']" [queryParams]="params()">AEGIS NIST</a><span aria-hidden="true">›</span><span aria-current="page">{{ nistFunctionTitle(f) }}</span>
        </nav>
        <header class="page-head">
          <div>
            <p class="page-eyebrow">AEGIS NIST · {{ f.code }}</p>
            <h1>{{ nistFunctionTitle(f) }}</h1>
            <p class="page-desc">{{ description(f.code) }}</p>
            @if (selection(); as sel) {
              <p class="page-meta">Avaliação: {{ sel.assessment.name }} · Escopo: {{ sel.scope?.name ?? '—' }} · <a [routerLink]="['/nist']" [queryParams]="params()">trocar</a></p>
            }
          </div>
          <div class="page-actions"><button type="button" class="ghost" (click)="agent.openAgent()">Perguntar ao Auditor Virtual</button></div>
        </header>

        <nav class="tabbar" aria-label="Seções da função">
          <a [routerLink]="['/nist', f.slug]" [queryParams]="params()" [class.on]="tab() === 'avaliacao'" [attr.aria-current]="tab() === 'avaliacao' ? 'page' : null">Avaliação</a>
          <a [routerLink]="['/nist', f.slug, 'postura']" [queryParams]="params()" [class.on]="tab() === 'postura'" [attr.aria-current]="tab() === 'postura' ? 'page' : null">Postura do ambiente</a>
          <a [routerLink]="['/nist', f.slug, 'recursos']" [queryParams]="params()" [class.on]="tab() === 'recursos'" [attr.aria-current]="tab() === 'recursos' ? 'page' : null">Recursos de apoio</a>
        </nav>

        @switch (tab()) {
          @case ('postura') {
            <p class="notice">
              <strong>Postura do ambiente (AEGIS Score, aegis-score-v1).</strong> Estado dos controles avaliados por telemetria
              e documentos — outro conceito, distinto da maturidade atual × alvo da avaliação. Não é somado a ela.
            </p>
            <app-pillar-dashboard [pillar]="f.code" [embedded]="true" />
            @if (f.code === 'PR') { <app-device-posture /> }
            @if (f.code === 'DE') { <app-detection-coverage /> }
          }
          @case ('recursos') {
            <div class="res-grid">
              @for (r of resources(f.code); track r.path + r.title) {
                <a class="card res" [routerLink]="r.path"><span class="k">{{ r.title }}</span><span class="muted">{{ r.description }}</span></a>
              }
            </div>
          }
          @default {
            @if (loading()) {
              <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a função…</p></div></div>
            } @else if (error()) {
              <div class="panel"><div class="state error" role="alert"><p class="err">{{ error() }}</p></div></div>
            } @else if (!selection()?.scope) {
              <div class="panel"><div class="state"><p>Escolha ou crie uma avaliação e um escopo para avaliar esta função.</p>
                <a class="primary" [routerLink]="['/nist']">Ir para a avaliação</a></div></div>
            } @else if (view()) {
              @let v = view()!;
              <div class="cards">
                <div class="card"><div class="k">Avaliadas</div><div class="v">{{ v.profile.evaluated }}<small> de {{ v.profile.subcategories }}</small></div></div>
                <div class="card"><div class="k">Atual (média)</div><div class="v">{{ averageText(v.profile.current) }}</div></div>
                <div class="card"><div class="k">Alvo (média)</div><div class="v">{{ averageText(v.profile.target) }}</div></div>
                <div class="card"><div class="k">Lacuna média</div><div class="v">{{ gapText(v.profile.gap) }}</div></div>
              </div>
              <p class="muted">{{ profileBasisText(v.profile) }}. Ausência de nível não conta como zero.</p>

              @for (c of v.categories; track c.code) {
                <details class="panel cat" [open]="true">
                  <summary>
                    <span class="cat-name">{{ nistCategoryLabel(c.code, c.name) }}</span>
                    <span class="mono code">{{ c.code }}</span>
                    <span class="muted">{{ c.profile.evaluated }} de {{ c.subcategories.length }} avaliadas · Atual {{ averageText(c.profile.current) }} · Alvo {{ averageText(c.profile.target) }}</span>
                  </summary>
                  <ul class="subs">
                    @for (s of c.subcategories; track s.code) {
                      <li>
                        <a class="sub-title" [routerLink]="['/nist', f.slug, s.code]" [queryParams]="params()">{{ s.title }}</a>
                        <span class="mono code">{{ s.code }}</span>
                        <span class="badge" [class]="'badge ' + stateBadgeClass(s.state)">{{ stateLabel(s.state) }}</span>
                        <span class="lv">Atual {{ levelLabel(s.currentLevel) }} · Alvo {{ levelLabel(s.targetLevel) }} · Lacuna {{ gapText(s.gap) }}</span>
                        <span class="muted">{{ s.evidenceCount }} evidência(s)@if (s.ownerName) { · {{ s.ownerName }} }@if (s.reviewedAt) { · revisada em {{ s.reviewedAt | date: 'dd/MM/yyyy' }} }</span>
                      </li>
                    }
                  </ul>
                </details>
              }
            }
          }
        }
      </section>
    } @else {
      <section class="page"><div class="panel"><div class="state"><p>Função desconhecida.</p><a class="primary" routerLink="/nist">Voltar ao AEGIS NIST</a></div></div></section>
    }
  `,
  styles: [
    `
      .crumbs { display: flex; flex-wrap: wrap; gap: 6px; font-size: var(--fs-sm); color: var(--muted); }
      .crumbs a { color: var(--text-2); }
      .res-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 280px), 1fr)); gap: var(--sp-4); }
      .res { display: flex; flex-direction: column; gap: 6px; text-decoration: none; color: inherit; }
      .cat summary { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px var(--sp-3); cursor: pointer; padding: 2px 0; }
      .cat summary:focus-visible { outline: none; box-shadow: var(--focus); }
      .cat-name { font-weight: 600; font-size: var(--fs-panel); }
      .code { font-size: var(--fs-meta); color: var(--muted); }
      .subs { list-style: none; margin: var(--sp-3) 0 0; padding: 0; display: flex; flex-direction: column; }
      .subs li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px var(--sp-3); padding: var(--sp-3) 0; border-top: 1px solid var(--line-2); }
      .sub-title { flex: 1 1 260px; min-width: 0; font-weight: 500; }
      .lv { font-size: var(--fs-sm); color: var(--text-2); }
      .subs .muted { flex-basis: 100%; font-size: var(--fs-meta); }
    `,
  ],
})
export class NistFunctionComponent {
  private readonly nist = inject(NistService);
  private readonly memory = inject(NistSelectionService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly agent = inject(AgentStateService);

  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly nistCategoryLabel = nistCategoryLabel;
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly levelLabel = levelLabel;
  protected readonly stateLabel = stateLabel;
  protected readonly stateBadgeClass = stateBadgeClass;
  protected readonly profileBasisText = profileBasisText;

  protected readonly fn = signal<NistFunctionMeta | null>(null);
  protected readonly tab = signal<'avaliacao' | 'postura' | 'recursos'>('avaliacao');
  protected readonly selection = signal<NistSelection | null>(null);
  protected readonly view = signal<NistFunctionView | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly params = computed(() => selectionParams(this.selection()));

  private requestKey: string | null = null;
  private ticket = 0;
  private pending: Subscription | null = null;
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.ticket++;
      this.pending?.unsubscribe();
    });
    combineLatest([this.route.paramMap, this.route.queryParamMap, this.route.data])
      .pipe(takeUntilDestroyed())
      .subscribe(([p, q, d]) => {
        const f = nistFunctionBySlug(p.get('fn'));
        this.fn.set(f);
        this.tab.set((d['tab'] as 'postura' | 'recursos') ?? 'avaliacao');
        if (f) this.load(f, q.get('avaliacao'), q.get('escopo'));
      });
  }

  protected description(code: NistFunctionCode): string {
    return NIST_FUNCTION_DESCRIPTIONS[code];
  }

  protected resources(code: NistFunctionCode): SupportResource[] {
    return RESOURCES[code];
  }

  /**
   * Leitura da função para (tenant · função · avaliação · escopo). Uma nova seleção cancela a leitura anterior e limpa a
   * tela; resposta ou erro de uma leitura substituída (ou que chega depois de sair da tela) é descartado sem navegar.
   */
  private load(f: NistFunctionMeta, assessmentId: string | null, scopeId: string | null): void {
    const tenant = this.auth.activeTenantId();
    const key = [tenant, f.code, assessmentId, scopeId].join('|');
    if (key === this.requestKey) return;
    this.requestKey = key;
    const ticket = ++this.ticket;
    this.pending?.unsubscribe();
    this.view.set(null);
    this.selection.set(null);
    this.loading.set(true);
    this.error.set(null);
    const current = () => !this.destroyed && ticket === this.ticket && tenant === this.auth.activeTenantId();
    this.pending = this.nist
      .list()
      .pipe(
        switchMap((list) => {
          if (!current()) return EMPTY;
          const sel = resolveSelection(list, { assessmentId, scopeId }, this.memory.read(tenant));
          this.selection.set(sel);
          if (!sel?.scope) {
            this.loading.set(false);
            return EMPTY;
          }
          if (sel.assessment.id !== assessmentId || sel.scope.id !== scopeId) {
            // A URL passa a dizer a seleção resolvida; a navegação resultante é esta mesma leitura.
            this.requestKey = [tenant, f.code, sel.assessment.id, sel.scope.id].join('|');
            void this.router.navigate([], { relativeTo: this.route, queryParams: selectionParams(sel), replaceUrl: true });
          }
          this.memory.write(tenant, sel.assessment.id, sel.scope.id);
          return this.nist.functionView(sel.assessment.id, sel.scope.id, f.code);
        }),
      )
      .subscribe({
        next: (v) => {
          if (!current()) return;
          this.view.set(v);
          this.loading.set(false);
        },
        error: (e: Error) => {
          if (!current()) return;
          this.error.set(e.message);
          this.loading.set(false);
        },
      });
  }
}
