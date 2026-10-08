import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, Subscription, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { ChartComponent } from '../components/chart.component';
import { MonthlyEvolutionComponent } from '../components/monthly-evolution.component';
import {
  ChartSpec,
  knightCoverageDonut,
  knightDomainBars,
  knightServiceStacked,
  knightSeverityColumns,
  nistFunctionColumns,
  nistGapBars,
  nistProgressStacked,
  nistRadar,
  nistStatesDonut,
} from '../models/charts.models';
import {
  KnightView,
  PLAN_ORIGIN_LABEL,
  PlanOrigin,
  attentionItems,
  defaultKnightView,
  knightReading,
  knightStateLabel,
  knightViewKey,
  nistPostureReading,
  planTreatment,
  scoreText,
} from '../models/dashboards.models';
import { KnightAssessment, KnightSourceLatest, KnightSourceType, overviewKpis, sourceTypeLabel } from '../models/knight.models';
import {
  NistAssessment,
  NistCorrelationRow,
  NistEvidenceOverview,
  NistProfile,
  NistSelection,
  NistTechnicalEvidence,
  averageText,
  correlationAttention,
  cycleLabel,
  gapText,
  knightResultQuery,
  resolveSelection,
  selectionParams,
  stateLabel,
  subcategoryRoute,
} from '../models/nist.models';
import { HistoryWindowRequest, PostureMonthlyHistory } from '../models/posture-history.models';
import { ActionPlan } from '../models/remediation.models';
import { WorkspacePosture } from '../models/workspace.models';
import { AegisScoreService } from '../services/aegis-score.service';
import { AgentStateService } from '../services/agent-state.service';
import { AuthService } from '../services/auth.service';
import { KnightService } from '../services/knight.service';
import { NistSelectionService, NistService } from '../services/nist.service';
import { PostureHistoryService } from '../services/posture-history.service';
import { RemediationService } from '../services/remediation.service';

/** Bloco que pode falhar sozinho sem derrubar o restante do painel. `loading` distingue "carregando" de "sem dado". */
interface Block<T> {
  value: T | null;
  error: string | null;
  loading: boolean;
}

const idle = <T>(): Block<T> => ({ value: null, error: null, loading: true });

function days(iso: string | null | undefined, now: Date): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isFinite(t) ? Math.max(0, Math.floor((now.getTime() - t) / 86_400_000)) : null;
}

/**
 * [AEGIS-NIST-JOURNEY-01 · AEGIS-ASSESSMENT-VISUALS-01] Dashboards — os DOIS assessments com destaque equivalente:
 *   • AEGIS KNIGHT: nota 0–100, cobertura (avaliados de aplicáveis), controles reprovados e estado da avaliação — do
 *     CONSOLIDADO das fontes elegíveis (o construtor do servidor; nunca média feita aqui) ou de uma fonte escolhida, com a
 *     composição consultável;
 *   • AEGIS NIST: maturidade atual × alvo (1–5), cobertura, achados abertos e tratamento — da avaliação · rodada · escopo
 *     identificados e selecionáveis (mesma regra da jornada NIST);
 *   • métricas comuns lado a lado, com as bases ditas (não se somam), e o tratamento contado uma vez por plano;
 *   • necessidades de atenção com origem e evolução mensal com período;
 *   • [AEGIS-AUDITOR-CONTEXT-01] correlação KNIGHT × NIST da rodada selecionada, calculada pelos registros: evidências técnicas vinculadas,
 *     achados KNIGHT disponíveis para revisão, lacunas de evidência e tratamentos ligados, com links para a origem (contagens sem duplicar
 *     um controle vinculado a várias subcategorias). A interpretação da IA é pedida ao Auditor, separada.
 * Cada bloco carrega e falha sozinho; troca de seleção descarta a resposta atrasada. Nenhum score geral combina os módulos.
 */
@Component({
  selector: 'app-dashboards',
  standalone: true,
  imports: [RouterLink, DatePipe, ChartComponent, MonthlyEvolutionComponent],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Dashboards</p>
          <h1>Visão dos assessments</h1>
          <p class="page-desc">
            <strong>AEGIS KNIGHT</strong> mede a postura técnica de configuração (nota 0–100); <strong>AEGIS NIST</strong>, a maturidade
            organizacional pelo NIST CSF 2.0 (escala 1–5 do AEGIS). São instrumentos diferentes: os números não se somam.
          </p>
          @if (generatedAt(); as g) { <p class="page-meta">Leitura de {{ g | date: 'dd/MM/yyyy HH:mm' }}</p> }
        </div>
        <div class="page-actions"><button type="button" class="ghost" (click)="loadAll()">Atualizar</button></div>
      </header>

      <div class="modules">
        <!-- ============================== AEGIS KNIGHT ============================== -->
        <section class="panel module" aria-labelledby="m-knight">
          <div class="mhead">
            <div><h2 id="m-knight">AEGIS KNIGHT</h2><p class="mdesc">Postura técnica de configuração · nota 0–100</p></div>
            <a class="linknav" routerLink="/knight">Abrir o KNIGHT →</a>
          </div>
          @if (knight().loading) {
            <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando as avaliações do KNIGHT…</p></div>
          } @else if (knight().error) {
            <p class="notice error" role="alert">{{ knight().error }} <button type="button" class="linkbtn" (click)="loadKnight()">Tentar novamente</button></p>
          } @else if (!knightView()) {
            <div class="state"><p><strong>Nenhuma avaliação técnica concluída.</strong> Conecte uma fonte e execute a primeira avaliação.</p>
              <a class="primary" routerLink="/knight">Abrir o KNIGHT</a></div>
          } @else {
            <div class="selrow">
              <label class="field">
                <span class="field-label">Resultado exibido</span>
                <select [value]="knightViewKey(knightView())" (change)="selectKnight($any($event.target).value)">
                  @if (consolidatedOption(); as co) { <option [value]="co.key">{{ co.label }}</option> }
                  @for (s of assessedSources(); track s.source) { <option [value]="'source:' + s.source">{{ s.label }}</option> }
                </select>
              </label>
              <p class="why">{{ knightReason() }}</p>
            </div>
            @if (knightAssessment(); as a) {
              @let r = knightRead();
              @let st = knightState();
              @let k = knightKpis();
              <div class="metrics">
                <div class="metric" [class.is-void]="a.score === null" [class.is-partial]="st.tone === 'warn'">
                  <span class="metric-label">Nota KNIGHT</span>
                  <span class="metric-value" [class.is-na]="a.score === null">{{ scoreText(a.score) }}@if (a.score !== null) {<small>/100</small>}</span>
                  <span class="metric-unit">{{ a.score === null ? 'Sem controle avaliado: não há nota.' : 'Pondera severidade e resultado dos controles avaliados.' }}</span>
                </div>
                <div class="metric">
                  <span class="metric-label">Cobertura da avaliação</span>
                  <span class="metric-value">{{ pctText(a.coverage) }}</span>
                  <span class="metric-unit">{{ k!.evaluated }} de {{ k!.evaluated + k!.notEvaluated + k!.errors }} controles aplicáveis avaliados</span>
                </div>
                <div class="metric">
                  <span class="metric-label">Controles reprovados</span>
                  <span class="metric-value">{{ k!.failed }}</span>
                  <span class="metric-unit">{{ k!.mitigated }} mitigado(s) com atenção · {{ k!.total }} controle(s) no escopo</span>
                </div>
                <div class="metric" [class.is-partial]="st.tone === 'warn'" [class.is-void]="st.tone === 'neutral'">
                  <span class="metric-label">Avaliação</span>
                  <span class="metric-value sm">{{ (a.completedAt ?? a.startedAt) | date: 'dd/MM/yyyy' }}</span>
                  <span class="metric-foot"><span class="metric-state">{{ st.label }}</span>
                    @for (l of r!.labels; track l) { <span class="metric-src">{{ l }}</span> }</span>
                </div>
              </div>
              @if (consolidatedBlock().loading && knightView()!.kind === 'consolidated') {
                <p class="muted" role="status">Atualizando o consolidado…</p>
              }
              <div class="charts2">
                <app-chart [spec]="knightCharts()!.coverage" />
                <app-chart [spec]="knightCharts()!.services" />
              </div>
              <details class="more">
                <summary>Composição das fontes ({{ compositionRows().length }})</summary>
                <div class="table-wrap">
                  <table class="data-table">
                    <thead><tr><th scope="col">Fonte</th><th scope="col">Situação</th><th scope="col">Nota</th><th scope="col">Cobertura</th><th scope="col">Coleta</th><th scope="col">Avaliação</th></tr></thead>
                    <tbody>
                      @for (c of compositionRows(); track c.label) {
                        <tr>
                          <th scope="row">{{ c.label }}</th>
                          <td><span class="badge" [class.ok]="c.state === 'Incluída'" [class.neutral]="c.state !== 'Incluída'">{{ c.state }}</span></td>
                          <td>{{ scoreText(c.score) }}</td>
                          <td>{{ c.coverage === null ? '—' : pctText(c.coverage) }}</td>
                          <td>{{ c.at ? (c.at | date: 'dd/MM/yyyy') : '—' }}</td>
                          <td>@if (c.runId) { <a routerLink="/knight" [queryParams]="{ run: c.runId }">abrir</a> } @else { — }</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                <p class="muted note">Cada fonte tem a própria nota e cobertura. A nota do consolidado aplica a fórmula do KNIGHT sobre a união dos
                  controles das fontes incluídas — não é a média das notas. Fonte sem avaliação concluída não vira aprovação.</p>
              </details>
            } @else if (consolidatedBlock().error && knightView()!.kind === 'consolidated') {
              <p class="notice error" role="alert">{{ consolidatedBlock().error }}</p>
            } @else {
              <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Montando o consolidado…</p></div>
            }
          }
        </section>

        <!-- ============================== AEGIS NIST ============================== -->
        <section class="panel module" aria-labelledby="m-nist">
          <div class="mhead">
            <div><h2 id="m-nist">AEGIS NIST</h2><p class="mdesc">Maturidade organizacional · NIST CSF 2.0 · escala 1–5 do AEGIS</p></div>
            <a class="linknav" routerLink="/nist" [queryParams]="nistQuery()">Abrir o NIST →</a>
          </div>
          @if (nistList().loading) {
            <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando as avaliações NIST…</p></div>
          } @else if (nistList().error) {
            <p class="notice error" role="alert">{{ nistList().error }} <button type="button" class="linkbtn" (click)="loadNist()">Tentar novamente</button></p>
          } @else if (!nistSel()) {
            <div class="state"><p><strong>Nenhuma avaliação NIST criada.</strong> Defina a avaliação, a rodada e o escopo para começar.</p>
              <a class="primary" routerLink="/nist">Criar avaliação</a></div>
          } @else {
            @let sel = nistSel()!;
            <div class="selrow sel3">
              <label class="field"><span class="field-label">Avaliação</span>
                <select [value]="sel.assessment.id" (change)="selectNist($any($event.target).value, null, null)" [disabled]="(nistList().value?.length ?? 0) < 2">
                  @for (a of nistList().value ?? []; track a.id) { <option [value]="a.id">{{ a.name }}</option> }
                </select></label>
              <label class="field"><span class="field-label">Rodada</span>
                <select [value]="sel.cycle?.id ?? ''" (change)="selectNist(sel.assessment.id, $any($event.target).value, sel.scope?.id ?? null)" [disabled]="(sel.assessment.cycles?.length ?? 0) < 2">
                  @for (c of sel.assessment.cycles ?? []; track c.id) { <option [value]="c.id">{{ cycleLabel(c) }}</option> }
                </select></label>
              <label class="field"><span class="field-label">Escopo</span>
                <select [value]="sel.scope?.id ?? ''" (change)="selectNist(sel.assessment.id, sel.cycle?.id ?? null, $any($event.target).value)" [disabled]="sel.assessment.scopes.length < 2">
                  @for (s of sel.assessment.scopes; track s.id) { <option [value]="s.id">{{ s.name }}</option> }
                </select></label>
            </div>
            <p class="why">{{ nistReason() }} Metodologia {{ sel.assessment.methodologyVersion }}.</p>
            @if (profile().loading) {
              <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando o perfil da rodada…</p></div>
            } @else if (profile().error) {
              <p class="notice error" role="alert">{{ profile().error }} <button type="button" class="linkbtn" (click)="loadProfile()">Tentar novamente</button></p>
            } @else if (profile().value) {
              @let p = profile().value!;
              @let t = p.treatment;
              <div class="metrics">
                <div class="metric" [class.is-void]="p.overall.current === null">
                  <span class="metric-label">Maturidade atual × alvo</span>
                  <span class="metric-value" [class.is-na]="p.overall.current === null">{{ averageText(p.overall.current) }}<small> → {{ averageText(p.overall.target) }}</small></span>
                  <span class="metric-unit">{{ p.overall.current === null ? 'Sem avaliação confirmada nesta rodada.' : 'Lacuna média ' + gapText(p.overall.gap) + ' · médias das funções avaliadas' }}</span>
                </div>
                <div class="metric" [class.is-partial]="nistCoverage() !== null && nistCoverage()! < 100">
                  <span class="metric-label">Cobertura da avaliação</span>
                  <span class="metric-value">{{ nistCoverage() === null ? '—' : pctText(nistCoverage()!) }}</span>
                  <span class="metric-unit">@if (p.states; as s) { {{ s.evaluated }} avaliadas + {{ s.notApplicable }} não se aplicam de {{ s.subcategories }} subcategorias } @else { sem contagem }</span>
                </div>
                <div class="metric">
                  <span class="metric-label">Achados abertos</span>
                  <span class="metric-value">{{ t?.findingsOpen ?? '—' }}</span>
                  <span class="metric-unit">@if (t) { {{ t.findingsWithoutPlan }} sem plano · {{ t.findingsRiskAccepted }} com risco aceito } @else { sem tratamento registrado }</span>
                </div>
                <div class="metric" [class.is-partial]="(t?.plansOverdue ?? 0) > 0">
                  <span class="metric-label">Planos atrasados</span>
                  <span class="metric-value">{{ t?.plansOverdue ?? '—' }}</span>
                  <span class="metric-unit">@if (t) { {{ t.plansAwaitingValidation }} aguardando validação · {{ t.plansInProgress }} em andamento } @else { — }</span>
                </div>
              </div>
              <div class="charts2">
                <app-chart [spec]="nistCharts()!.functions" />
                @if (nistCharts()!.states; as stc) { <app-chart [spec]="stc" /> }
              </div>
            }
          }
        </section>
      </div>

      <!-- ============================== Correlação KNIGHT × NIST ============================== -->
      <!-- [AEGIS-AUDITOR-CONTEXT-01] Calculada pelos registros (mapeamento explícito do catálogo do KNIGHT e vínculos do assessor), para a
           avaliação · rodada · escopo selecionados no NIST. Não é média de notas nem interpretação de IA; abre os registros de origem. -->
      @if (nistSel()?.scope && nistSel()?.cycle) {
        <section class="panel" aria-labelledby="m-corr">
          <div class="hd"><h3 id="m-corr">Correlação KNIGHT × NIST</h3>
            <span class="hint">calculada pelos registros · {{ nistSel()!.cycle!.name }} · {{ nistSel()!.scope!.name }}</span></div>
          @if (overview().loading) {
            <p class="muted" role="status">Carregando a correlação da rodada…</p>
          } @else if (overview().error) {
            <p class="notice error" role="alert">{{ overview().error }} <button type="button" class="linkbtn" (click)="loadOverview()">Tentar novamente</button></p>
          } @else if (overview().value) {
            @let o = overview().value!;
            @let sm = o.summary;
            <dl class="corr-sum">
              <dt>Evidências técnicas vinculadas</dt>
              <dd><strong>{{ sm.linkedTechnicalControls }}</strong> controle(s) em {{ sm.subcategoriesWithLinkedTechnical }} subcategoria(s)
                <span class="muted">· {{ sm.linkedTechnicalLinks }} vínculo(s)</span></dd>
              <dt>Achados KNIGHT disponíveis para revisão</dt>
              <dd><strong>{{ sm.candidateControls }}</strong> controle(s) em {{ sm.subcategoriesWithCandidates }} subcategoria(s)
                <span class="muted">· ainda não vinculados</span></dd>
              <dt>Lacunas de evidência</dt>
              <dd><strong>{{ sm.subcategoriesEvaluatedWithoutEvidence }}</strong> com nível confirmado sem evidência ·
                <strong>{{ sm.subcategoriesWithTechnicalNotEvaluated }}</strong> com controles técnicos sem resultado avaliado</dd>
              <dt>Tratamentos ligados</dt>
              <dd><strong>{{ sm.plans }}</strong> plano(s)@if (sm.plansOverdue) { · <span class="warn-text">{{ sm.plansOverdue }} atrasado(s)</span> }</dd>
            </dl>
            @if (o.rows.length === 0) {
              <p class="muted">Nenhuma subcategoria com evidência técnica relacionada, nível sem evidência ou tratamento ligado nesta rodada.</p>
            } @else {
              <div class="table-wrap">
                <table class="data-table corr">
                  <caption class="sr-only">Subcategorias com relação técnica, lacuna de evidência ou tratamento</caption>
                  <thead><tr><th scope="col">Subcategoria</th><th scope="col">Vinculadas</th><th scope="col">Disponíveis para revisão</th><th scope="col">Lacunas</th><th scope="col">Tratamentos</th></tr></thead>
                  <tbody>
                    @for (r of corrRows(); track r.code) {
                      <tr>
                        <th scope="row"><a [routerLink]="subRoute(r.code)" [queryParams]="nistQuery()">{{ r.code }}</a>
                          <span class="muted"> · {{ stateText(r.state) }}@if (r.currentLevel !== null) { · atual {{ r.currentLevel }} }</span></th>
                        <td>@for (t of r.linkedTechnical; track t.evidenceId) {
                              <a [routerLink]="subRoute(r.code)" [queryParams]="nistQuery()" [fragment]="'ev-' + t.evidenceId">{{ t.knightIndicatorId }}</a>
                              <span class="muted"> ({{ t.statusLabel }}@if (t.newerStatus) { · mais recente: {{ t.newerStatus }} })</span>@if (!$last) {, }
                            } @empty { <span class="muted">—</span> }</td>
                        <td>@for (t of r.candidates; track t.knightRunId + t.knightIndicatorId) {
                              <a routerLink="/knight" [queryParams]="knightQuery(t)">{{ t.knightIndicatorId }}</a>
                              <span class="muted"> ({{ t.statusLabel }}@if (t.isDemo) { · demonstração })</span>@if (!$last) {, }
                            } @empty { <span class="muted">—</span> }</td>
                        <td>
                          @if (r.evaluatedWithoutEvidence) { <span class="badge warn">nível sem evidência</span> }
                          @if (r.technicalNotEvaluated) { <span class="badge neutral">{{ r.technicalNotEvaluated }} sem resultado técnico</span> }
                          @if (!r.evaluatedWithoutEvidence && !r.technicalNotEvaluated) { <span class="muted">—</span> }
                        </td>
                        <td>@for (p of r.plans; track p.planId) {
                              @if (p.origin === 'KnightFinding') { <a routerLink="/knight" [queryParams]="{ plan: p.planId }">{{ p.title }}</a> }
                              @else { <a [routerLink]="subRoute(r.code)" [queryParams]="nistQuery()" [fragment]="'achado-' + p.nistFindingId">{{ p.title }}</a> }
                              <span class="muted"> ({{ p.statusLabel }}@if (p.isOverdue) { · atrasado })</span>@if (!$last) {; }
                            } @empty { <span class="muted">—</span> }</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (o.rows.length > corrLimit) {
                <button type="button" class="linkbtn" (click)="corrAll.set(!corrAll())">{{ corrAll() ? 'Mostrar só as que pedem revisão primeiro' : 'Ver todas as ' + o.rows.length + ' subcategorias' }}</button>
              }
            }
            <p class="muted note">{{ o.limitations[0] }} {{ o.limitations[1] }}</p>
            @if (o.limitations.length > 2) {
              <details class="more"><summary>Limitações da leitura ({{ o.limitations.length - 2 }})</summary>
                <ul class="notes">@for (l of o.limitations.slice(2); track l) { <li>{{ l }}</li> }</ul></details>
            }
            <p class="muted note">Interpretação: <button type="button" class="linkbtn" (click)="askAuditor()">pedir ao Auditor Virtual</button> — a resposta da IA
              é sugestão, cita estas fontes e não altera vínculos nem resultados.</p>
          }
        </section>
      }

      <!-- ============================== Métricas comuns ============================== -->
      <section class="panel" aria-labelledby="m-common">
        <div class="hd"><h3 id="m-common">Métricas comuns</h3><span class="hint">lado a lado, com a base de cada uma</span></div>
        <div class="common">
          <div class="cbox">
            <h4>Cobertura</h4>
            <dl>
              <dt>KNIGHT</dt><dd>{{ knightAssessment() ? pctText(knightAssessment()!.coverage) : '—' }} <span class="muted">dos controles técnicos aplicáveis</span></dd>
              <dt>NIST</dt><dd>{{ nistCoverage() === null ? '—' : pctText(nistCoverage()!) }} <span class="muted">das subcategorias do catálogo no escopo</span></dd>
            </dl>
            <p class="muted note">Universos diferentes: controles de configuração × resultados organizacionais. Não se somam nem se comparam como o mesmo número.</p>
          </div>
          <div class="cbox">
            <h4>Atualidade dos dados</h4>
            <dl>
              <dt>KNIGHT</dt><dd>{{ freshness().knight }}</dd>
              <dt>NIST</dt><dd>{{ freshness().nist }}</dd>
            </dl>
            <p class="muted note">Coleta técnica mais recente × última revisão humana registrada na rodada.</p>
          </div>
          <div class="cbox">
            <h4>Tratamento</h4>
            @if (plans().error) {
              <p class="notice error" role="alert">{{ plans().error }}</p>
            } @else if (plans().loading) {
              <p class="muted" role="status">Carregando os planos…</p>
            } @else {
              <div class="chips" role="group" aria-label="Origem dos planos">
                @for (o of planOrigins; track o) {
                  <button type="button" class="filter-chip" [class.on]="planFilter() === o" [attr.aria-pressed]="planFilter() === o" (click)="planFilter.set(o)">{{ PLAN_ORIGIN_LABEL[o] }}</button>
                }
              </div>
              @let tr = treatment();
              <dl>
                <dt>Planos ativos</dt><dd>{{ tr.active }} <span class="muted">de {{ tr.total }}</span></dd>
                <dt>Atrasados</dt><dd [class.warn-text]="tr.overdue > 0">{{ tr.overdue }}</dd>
                <dt>Aguardando validação</dt><dd>{{ tr.awaitingValidation }}</dd>
                <dt>Concluídos</dt><dd>{{ tr.completed }}</dd>
              </dl>
              <p class="muted note">Cada plano conta uma vez. Por origem: KNIGHT {{ tr.byOrigin.knight }} · NIST {{ tr.byOrigin.nist }} · dispositivos e outros {{ tr.byOrigin.other }}.
                <a routerLink="/nist/id/prioridades" [queryParams]="{ tab: 'planos' }">Ver os planos</a></p>
            }
          </div>
        </div>
      </section>

      <!-- ============================== Atenção ============================== -->
      <section class="panel" aria-labelledby="m-att">
        <div class="hd"><h3 id="m-att">Necessidades de atenção</h3><span class="hint">critérios do KNIGHT, do NIST e prazo dos planos</span></div>
        @if (attention().length === 0) {
          <p class="muted">Nada pendente pelos critérios atuais: nenhum controle reprovado, nenhuma lacuna com achado aberto e nenhum plano atrasado.</p>
        } @else {
          <ul class="att">
            @for (i of attention(); track $index) {
              <li>
                <span class="badge" [class.risk]="i.origin === 'KNIGHT'" [class.violet]="i.origin === 'NIST'" [class.warn]="i.origin === 'Plano'">{{ i.origin }}</span>
                <div><a [routerLink]="i.route" [queryParams]="i.query">{{ i.title }}</a><span class="muted">{{ i.detail }}</span></div>
              </li>
            }
          </ul>
          <p class="muted note">KNIGHT: controles reprovados por severidade e itens afetados. NIST: maiores lacunas confirmadas com achado aberto. Planos: prazo vencido.</p>
        }
      </section>

      <!-- ============================== Evolução ============================== -->
      <section class="panel" aria-labelledby="m-evo">
        <div class="hd"><h3 id="m-evo">Evolução mensal</h3><a class="linkbtn" routerLink="/history">Histórico de postura</a></div>
        @if (monthly().error) {
          <p class="notice error" role="alert">{{ monthly().error }}</p>
        } @else if (monthly().loading) {
          <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a evolução mensal…</p></div>
        } @else {
          <app-monthly-evolution [history]="monthlyFiltered()" [period]="period()" (periodChange)="setPeriod($event)" />
        }
      </section>

      <!-- ============================== Detalhes por módulo ============================== -->
      @if (knightAssessment() && knightCharts(); as kc) {
        <details class="panel more-section">
          <summary><h3>KNIGHT · severidades e domínios</h3></summary>
          <div class="charts2">
            <app-chart [spec]="kc.severity" />
            <app-chart [spec]="kc.domains" />
          </div>
        </details>
      }
      @if (nistCharts(); as nc) {
        <details class="panel more-section">
          <summary><h3>NIST · perfil, andamento e lacunas</h3></summary>
          <div class="charts2">
            <app-chart [spec]="nc.radar" />
            @if (nc.progress; as pg) { <app-chart [spec]="pg" /> }
            <app-chart [spec]="nc.gaps" />
          </div>
        </details>
      }
      <details class="panel more-section">
        <summary><h3>AEGIS Score · postura do ambiente (outro instrumento)</h3></summary>
        @if (posture().error) {
          <p class="notice error" role="alert">{{ posture().error }}</p>
        } @else if (posture().value) {
          @let w = posture().value!;
          @let pr = postureReading();
          <p>Score {{ w.overall.percentage === null ? '—' : scoreText(w.overall.percentage) + '%' }} · cobertura {{ scoreText(w.overall.coveragePercentage) }}% ·
            {{ w.overall.evaluatedControls }} de {{ w.overall.eligibleControls }} controles avaliados (aegis-score-v1).</p>
          <p class="muted note">Mede a postura do ambiente pela telemetria e pelos documentos, na escala 0–100. Não é a maturidade NIST (1–5) nem a nota
            KNIGHT. @for (l of pr.labels; track l) { {{ l }}. }</p>
        } @else {
          <p class="muted">Carregando…</p>
        }
      </details>
    </section>
  `,
  styles: [
    `
      .modules { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 520px), 1fr)); gap: var(--sp-4); margin-bottom: var(--sp-4); }
      .module { display: flex; flex-direction: column; gap: var(--sp-3); min-width: 0; }
      .mhead { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: flex-start; gap: var(--sp-2); }
      .mhead h2 { margin: 0; font-size: var(--fs-section); }
      .mdesc { margin: 2px 0 0; font-size: var(--fs-meta); color: var(--muted); }
      .selrow { display: flex; flex-wrap: wrap; align-items: flex-end; gap: var(--sp-2) var(--sp-3); }
      .selrow .field { min-width: 0; flex: 1 1 200px; }
      .selrow select { width: 100%; }
      .why { margin: 0; font-size: var(--fs-meta); color: var(--muted); flex: 1 1 100%; }
      .metrics { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-3); }
      .metric-value small { font-size: 0.5em; color: var(--muted); font-weight: 600; margin-left: 2px; }
      .metric-value.sm { font-size: 22px; }
      .charts2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 300px), 1fr)); gap: var(--sp-4); }
      .more summary, .more-section summary { cursor: pointer; }
      .more-section { margin-top: var(--sp-4); }
      .more-section summary h3 { display: inline; font-size: var(--fs-panel); margin: 0; }
      .more-section[open] summary { margin-bottom: var(--sp-3); }
      .note { font-size: var(--fs-meta); margin: var(--sp-2) 0 0; }
      .common { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 260px), 1fr)); gap: var(--sp-4); }
      .cbox h4 { margin: 0 0 var(--sp-2); font-size: var(--fs-sm); color: var(--text-2); text-transform: uppercase; letter-spacing: var(--tracking-caps); }
      .cbox dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 4px var(--sp-3); margin: 0; font-size: var(--fs-sm); }
      .cbox dt { color: var(--muted); }
      .cbox dd { margin: 0; overflow-wrap: anywhere; }
      .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: var(--sp-2); }
      .att { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--sp-2); }
      .att li { display: grid; grid-template-columns: 76px minmax(0, 1fr); gap: var(--sp-3); align-items: start; padding: var(--sp-2) 0; border-top: 1px solid var(--line-2); }
      .att li:first-child { border-top: 0; }
      .att .badge { justify-content: center; }
      .att li > div { display: flex; flex-direction: column; min-width: 0; overflow-wrap: anywhere; }
      .att .muted { font-size: var(--fs-meta); }
      .corr-sum { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 230px), 1fr)); gap: var(--sp-2) var(--sp-4); margin: 0 0 var(--sp-3); }
      .corr-sum dt { font-size: var(--fs-meta); color: var(--muted); text-transform: uppercase; letter-spacing: var(--tracking-caps); }
      .corr-sum dd { margin: 2px 0 0; font-size: var(--fs-sm); overflow-wrap: anywhere; }
      .corr td, .corr th { vertical-align: top; overflow-wrap: anywhere; }
      .corr .badge { margin: 0 4px 4px 0; }
      .notes { margin: var(--sp-2) 0; padding-left: 18px; }
      @media (max-width: 520px) { .metrics { grid-template-columns: minmax(0, 1fr); } .att li { grid-template-columns: minmax(0, 1fr); } }
    `,
  ],
})
export class DashboardsComponent {
  private readonly knightApi = inject(KnightService);
  private readonly score = inject(AegisScoreService);
  private readonly nist = inject(NistService);
  private readonly memory = inject(NistSelectionService);
  private readonly auth = inject(AuthService);
  private readonly historyApi = inject(PostureHistoryService);
  private readonly remediation = inject(RemediationService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly agent = inject(AgentStateService);

  protected readonly scoreText = scoreText;
  protected readonly averageText = averageText;
  protected readonly gapText = gapText;
  protected readonly cycleLabel = cycleLabel;
  protected readonly knightViewKey = knightViewKey;
  protected readonly PLAN_ORIGIN_LABEL = PLAN_ORIGIN_LABEL;
  protected readonly planOrigins: (PlanOrigin | 'all')[] = ['all', 'knight', 'nist', 'other'];

  protected readonly generatedAt = signal<Date | null>(null);
  protected readonly knight = signal<Block<KnightSourceLatest[]>>(idle());
  protected readonly knightView = signal<KnightView | null>(null);
  protected readonly knightReason = signal('');
  protected readonly consolidatedBlock = signal<Block<KnightAssessment>>(idle());
  protected readonly nistList = signal<Block<NistAssessment[]>>(idle());
  protected readonly nistSel = signal<NistSelection | null>(null);
  protected readonly nistReason = signal('');
  protected readonly profile = signal<Block<NistProfile>>(idle());
  protected readonly plans = signal<Block<ActionPlan[]>>(idle());
  protected readonly planFilter = signal<PlanOrigin | 'all'>('all');
  protected readonly monthly = signal<Block<PostureMonthlyHistory>>(idle());
  protected readonly period = signal<HistoryWindowRequest>({ months: 12, until: null });
  protected readonly posture = signal<Block<WorkspacePosture>>(idle());
  /** [AEGIS-AUDITOR-CONTEXT-01] Evidências e correlação KNIGHT × NIST da rodada selecionada. */
  protected readonly overview = signal<Block<NistEvidenceOverview>>(idle());
  protected readonly corrAll = signal(false);
  protected readonly corrLimit = 8;
  protected readonly subRoute = subcategoryRoute;
  protected readonly knightQuery = (t: NistTechnicalEvidence) => knightResultQuery(t);
  protected readonly stateText = stateLabel;
  protected readonly corrRows = computed<NistCorrelationRow[]>(() => {
    const rows = correlationAttention(this.overview().value?.rows ?? []);
    return this.corrAll() ? rows : rows.slice(0, this.corrLimit);
  });

  // Sequências: cada nova leitura invalida a anterior (troca de seleção nunca mostra resposta atrasada).
  private consolidatedSeq = 0;
  private profileSeq = 0;
  private overviewSeq = 0;
  private monthlySeq = 0;
  private subs = new Subscription();

  /** Fontes com avaliação concluída (selecionáveis individualmente). */
  protected readonly assessedSources = computed(() => (this.knight().value ?? []).filter((s) => !!s.assessment));

  protected readonly consolidatedOption = computed(() => {
    const d = defaultKnightView(this.knight().value ?? []);
    if (!d || d.view.kind !== 'consolidated') return null;
    return { key: knightViewKey(d.view), label: `Consolidado — ${d.view.sources.length} fonte(s) elegíveis avaliadas` };
  });

  protected readonly knightAssessment = computed<KnightAssessment | null>(() => {
    const v = this.knightView();
    if (!v) return null;
    if (v.kind === 'consolidated') return this.consolidatedBlock().value;
    return (this.knight().value ?? []).find((s) => s.source === v.source)?.assessment ?? null;
  });
  protected readonly knightKpis = computed(() => {
    const a = this.knightAssessment();
    return a ? overviewKpis(a) : null;
  });
  protected readonly knightRead = computed(() => {
    const a = this.knightAssessment();
    return a ? knightReading(a, this.generatedAt() ?? new Date()) : null;
  });
  protected readonly knightState = computed(() => knightStateLabel(this.knightRead()));
  protected readonly knightCharts = computed(() => {
    const a = this.knightAssessment();
    return a
      ? { coverage: knightCoverageDonut(a), services: knightServiceStacked(a), severity: knightSeverityColumns(a), domains: knightDomainBars(a) }
      : null;
  });

  /** Composição: as fontes do consolidado (incluídas, disponíveis ou nunca avaliadas) e, à parte, as que não entram nele. */
  protected readonly compositionRows = computed(() => {
    const a = this.knightAssessment();
    const all = this.knight().value ?? [];
    const rows: { label: string; state: string; score: number | null; coverage: number | null; at: string | null; runId: string | null }[] = [];
    if (a?.sourceType === 'Consolidated' && a.sources) {
      for (const s of a.sources)
        rows.push({
          label: s.label,
          state: s.included ? 'Incluída' : s.availabilityState === 'Available' ? 'Disponível, não incluída' : 'Sem avaliação concluída',
          score: s.score,
          coverage: s.coverage,
          at: s.capturedAt,
          runId: s.sourceRunId,
        });
    }
    for (const s of all) {
      if (rows.some((r) => r.runId && r.runId === s.assessment?.id)) continue;
      if (a?.sourceType === 'Consolidated' && a.sources?.some((x) => x.source === s.source)) continue;
      rows.push({
        label: s.label,
        state: a?.sourceType !== 'Consolidated' && a?.id === s.assessment?.id ? 'Incluída' : s.assessment ? 'Fora do consolidado' : 'Sem avaliação concluída',
        score: s.assessment?.score ?? null,
        coverage: s.assessment?.coverage ?? null,
        at: s.assessment ? s.assessment.completedAt ?? s.assessment.startedAt : null,
        runId: s.assessment?.id ?? null,
      });
    }
    return rows;
  });

  protected readonly nistQuery = computed(() => selectionParams(this.nistSel()));
  protected readonly nistCoverage = computed(() => {
    const st = this.profile().value?.states;
    return st && st.subcategories > 0 ? Math.round((1000 * (st.evaluated + st.notApplicable)) / st.subcategories) / 10 : null;
  });
  protected readonly nistCharts = computed(() => {
    const p = this.profile().value;
    const sel = this.nistSel();
    if (!p || !sel?.scope) return null;
    const ctx = { avaliacao: sel.assessment.id, rodada: sel.cycle?.id ?? null, escopo: sel.scope.id };
    return {
      functions: nistFunctionColumns(p, ctx),
      states: nistStatesDonut(p),
      radar: nistRadar(p, ctx),
      progress: nistProgressStacked(p, ctx),
      gaps: nistGapBars(p, ctx),
    } satisfies Record<string, ChartSpec | null>;
  });

  protected readonly treatment = computed(() => planTreatment(this.plans().value ?? [], this.planFilter()));
  protected readonly attention = computed(() =>
    attentionItems(
      this.knightAssessment(),
      this.profile().value ? { gaps: this.profile().value!.gaps, query: this.nistQuery() } : null,
      this.plans().value ?? [],
      6,
      Object.fromEntries((this.knight().value ?? []).filter((s) => s.assessment).map((s) => [s.source, s.assessment!.id])),
    ),
  );

  protected readonly freshness = computed(() => {
    const now = this.generatedAt() ?? new Date();
    const a = this.knightAssessment();
    const kAt = a ? a.completedAt ?? a.startedAt : null;
    const sel = this.nistSel();
    const nAt = sel?.scope?.lastReviewedAt ?? sel?.assessment.lastReviewedAt ?? null;
    const txt = (iso: string | null, empty: string) => {
      const d = days(iso, now);
      return iso && d !== null ? `${new Date(iso).toLocaleDateString('pt-BR')} (há ${d} dia${d === 1 ? '' : 's'})` : empty;
    };
    return { knight: txt(kAt, 'sem coleta concluída'), nist: txt(nAt, 'sem revisão registrada') };
  });

  /** A evolução do painel mostra KNIGHT e maturidade NIST; o AEGIS Score tem seção própria (outro instrumento). */
  protected readonly monthlyFiltered = computed<PostureMonthlyHistory | null>(() => {
    const h = this.monthly().value;
    return h ? { ...h, series: h.series.filter((s) => s.type !== 'AegisScoreNist') } : null;
  });

  protected readonly postureReading = computed(() => nistPostureReading(this.posture().value?.overall ?? null, this.generatedAt() ?? new Date()));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subs.unsubscribe());
    // A seleção NIST vem da URL (compartilhável) — mesma regra da jornada: URL, depois a lembrança, depois a mais recente.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(() => {
      if (this.nistList().value) this.applyNistSelection();
    });
    this.loadAll();
  }

  loadAll(): void {
    // "Atualizar" recomeça tudo: as leituras anteriores ainda em voo são canceladas, nunca misturadas às novas.
    this.subs.unsubscribe();
    this.subs = new Subscription();
    this.generatedAt.set(new Date());
    this.loadKnight();
    this.loadNist();
    this.loadPlans();
    this.loadMonthly();
    this.track(this.score.fetchWorkspace(), (v) => this.posture.set(v), 'Não foi possível ler a postura do ambiente.');
  }

  // ---- KNIGHT -------------------------------------------------------------------------------------------------------

  loadKnight(): void {
    this.knight.set(idle());
    this.track(this.knightApi.getLatestBySource().pipe(map((r) => r.sources)), (b) => {
      this.knight.set(b);
      const d = defaultKnightView(b.value ?? []);
      const keep = this.knightView();
      const stillValid = keep && (keep.kind === 'consolidated' ? !!d && d.view.kind === 'consolidated' : (b.value ?? []).some((s) => s.source === keep.source && s.assessment));
      if (!stillValid) {
        this.knightView.set(d?.view ?? null);
        this.knightReason.set(d?.reason ?? '');
      } else if (keep.kind === 'consolidated' && d?.view.kind === 'consolidated') {
        this.knightView.set(d.view);
      }
      if (this.knightView()?.kind === 'consolidated') this.loadConsolidated();
    }, 'Não foi possível ler as avaliações do KNIGHT.');
  }

  protected selectKnight(key: string): void {
    if (key.startsWith('consolidated:')) {
      const d = defaultKnightView(this.knight().value ?? []);
      if (!d || d.view.kind !== 'consolidated') return;
      this.knightView.set(d.view);
      this.knightReason.set(d.reason);
      this.loadConsolidated();
      return;
    }
    const source = key.slice('source:'.length) as KnightSourceType;
    this.consolidatedSeq++; // descarta um consolidado ainda em voo
    this.knightView.set({ kind: 'source', source });
    this.knightReason.set(`Somente ${sourceTypeLabel(source)}: a avaliação concluída mais recente desta fonte.`);
  }

  private loadConsolidated(): void {
    const v = this.knightView();
    if (!v || v.kind !== 'consolidated') return;
    const seq = ++this.consolidatedSeq;
    this.consolidatedBlock.set(idle());
    this.subs.add(
      this.knightApi.getConsolidated(v.sources).subscribe({
        next: (a) => { if (seq === this.consolidatedSeq) this.consolidatedBlock.set({ value: a, error: null, loading: false }); },
        error: (e: Error) => { if (seq === this.consolidatedSeq) this.consolidatedBlock.set({ value: null, error: e.message || 'Não foi possível montar o consolidado.', loading: false }); },
      }),
    );
  }

  // ---- NIST ---------------------------------------------------------------------------------------------------------

  loadNist(): void {
    this.nistList.set(idle());
    this.profile.set(idle());
    this.track(this.nist.list(), (b) => {
      this.nistList.set(b);
      if (b.value) this.applyNistSelection();
    }, 'Não foi possível ler as avaliações NIST.');
  }

  private applyNistSelection(): void {
    const list = this.nistList().value ?? [];
    const q = this.route.snapshot.queryParamMap;
    const requested = { assessmentId: q.get('avaliacao'), cycleId: q.get('rodada'), scopeId: q.get('escopo') };
    const remembered = this.memory.read(this.auth.activeTenantId());
    const sel = resolveSelection(list, requested, remembered);
    const fromUrl = !!requested.assessmentId && sel?.assessment.id === requested.assessmentId;
    const fromMemory = !fromUrl && !!remembered && sel?.assessment.id === remembered.assessmentId;
    this.nistReason.set(
      fromUrl ? 'Seleção do endereço desta página.'
        : fromMemory ? 'Última avaliação, rodada e escopo usados no AEGIS NIST.'
          : 'Avaliação mais recente, na rodada em andamento.',
    );
    const prev = this.nistSel();
    this.nistSel.set(sel);
    if (!sel?.scope || !sel.cycle) {
      this.profile.set({ value: null, error: null, loading: false });
      return;
    }
    if (!prev || prev.assessment.id !== sel.assessment.id || prev.cycle?.id !== sel.cycle.id || prev.scope?.id !== sel.scope.id || !this.profile().value) {
      this.loadProfile();
      this.loadOverview();
    }
  }

  protected selectNist(assessmentId: string, cycleId: string | null, scopeId: string | null): void {
    const list = this.nistList().value ?? [];
    const sel = resolveSelection(list, { assessmentId, cycleId, scopeId }, null);
    if (!sel) return;
    if (sel.scope) this.memory.write(this.auth.activeTenantId(), sel.assessment.id, sel.scope.id, sel.cycle?.id ?? null);
    void this.router.navigate([], { relativeTo: this.route, queryParams: selectionParams(sel), replaceUrl: true });
  }

  loadProfile(): void {
    const sel = this.nistSel();
    if (!sel?.scope || !sel.cycle) return;
    const seq = ++this.profileSeq;
    this.profile.set(idle());
    this.subs.add(
      this.nist.profile({ assessmentId: sel.assessment.id, cycleId: sel.cycle.id, scopeId: sel.scope.id }).subscribe({
        next: (p) => { if (seq === this.profileSeq) this.profile.set({ value: p, error: null, loading: false }); },
        error: (e: Error) => { if (seq === this.profileSeq) this.profile.set({ value: null, error: e.message || 'Não foi possível ler o perfil.', loading: false }); },
      }),
    );
  }

  /** Correlação da rodada selecionada; troca de seleção descarta a resposta atrasada. */
  loadOverview(): void {
    const sel = this.nistSel();
    if (!sel?.scope || !sel.cycle) return;
    const seq = ++this.overviewSeq;
    this.overview.set(idle());
    this.corrAll.set(false);
    const ctx = { assessmentId: sel.assessment.id, cycleId: sel.cycle.id, scopeId: sel.scope.id };
    this.subs.add(
      this.nist.evidenceOverview(ctx).subscribe({
        next: (o) => { if (seq === this.overviewSeq && o.cycleId === ctx.cycleId && o.scopeId === ctx.scopeId) this.overview.set({ value: o, error: null, loading: false }); },
        error: (e: Error) => { if (seq === this.overviewSeq) this.overview.set({ value: null, error: e.message || 'Não foi possível ler a correlação.', loading: false }); },
      }),
    );
  }

  /** Interpretação pela IA, separada da correlação calculada: o Auditor responde no drawer, citando as fontes da rodada. */
  protected askAuditor(): void {
    this.agent.requestAudit(
      'Interprete a correlação KNIGHT × NIST desta rodada e escopo: o que as evidências técnicas vinculadas sustentam e o que não sustentam, ' +
        'quais achados disponíveis merecem revisão primeiro e quais lacunas de evidência precisam de documento, entrevista ou teste. ' +
        'Separe configuração observada, documentação e declaração do assessor; não proponha alterar níveis.',
    );
  }

  // ---- Comuns -------------------------------------------------------------------------------------------------------

  private loadPlans(): void {
    this.plans.set(idle());
    this.track(this.remediation.list({ origin: 'all' }), (b) => this.plans.set(b), 'Não foi possível ler os planos de ação.');
  }

  protected setPeriod(p: HistoryWindowRequest): void {
    this.period.set(p);
    this.loadMonthly();
  }

  private loadMonthly(): void {
    const seq = ++this.monthlySeq;
    const p = this.period();
    this.monthly.set(idle());
    this.subs.add(
      this.historyApi.monthly(p.months ?? 12, p.until).subscribe({
        next: (h) => { if (seq === this.monthlySeq) this.monthly.set({ value: h, error: null, loading: false }); },
        error: (e: Error) => { if (seq === this.monthlySeq) this.monthly.set({ value: null, error: e.message || 'Não foi possível ler a evolução mensal.', loading: false }); },
      }),
    );
  }

  protected pctText(v: number): string {
    return `${v.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
  }

  private track<T>(src: Observable<T>, done: (b: Block<T>) => void, message: string): void {
    this.subs.add(
      src
        .pipe(
          map((value) => ({ value, error: null, loading: false }) as Block<T>),
          catchError(() => of({ value: null, error: message, loading: false } as Block<T>)),
        )
        .subscribe(done),
    );
  }
}
