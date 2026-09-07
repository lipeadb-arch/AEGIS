import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AgentStateService } from '../services/agent-state.service';
import { PriorityService } from '../services/priority.service';
import { PriorityWorkspace } from '../models/priority.models';
import { postureLabel } from '../models/workspace.models';
import {
  findingSituation,
  findingTitle,
  scoreDisplay,
  severityLabel,
  statusLabel,
} from '../models/knight.models';
import { EXPOSURE_REACH_UNKNOWN, categoryPt, tierPt } from '../models/posture-exposure.models';
import {
  ActionPlan,
  actionResult,
  actionSituation,
  activePlanFor,
} from '../models/remediation.models';
import { RemediationService } from '../services/remediation.service';

/**
 * [AEGIS-MVP-PRIORITIES-01] Central de Prioridades — visão operacional que REÚNE, sem combinar num único
 * score, a postura NIST atual, a fila de exposições de configuração e a fila de vulnerabilidades em ativos.
 * Consome a superfície somente leitura `GET /api/v1/priorities` (read model composto).
 *
 * Invariante metodológica: postura (cobertura/maturidade), exposições de configuração (lacunas de fonte) e
 * vulnerabilidades (fraquezas em ativos) são dimensões DISTINTAS — apresentadas em DUAS FILAS separadas, cada
 * uma com a ordenação determinística já testada no backend. Provider-neutral: cada fila mostra a própria fonte
 * real. A IA é consultiva (reutiliza o Auditor Virtual) — não cria/altera score, CVE, exploit, lifecycle,
 * finding, evidência ou estado de remediação. Honestidade: "Ainda não coletado" ≠ "coletado sem achados";
 * estados loading/erro/vazio explícitos; ZERO fallback demonstrativo.
 */
@Component({
  selector: 'app-priorities',
  standalone: true,
  imports: [RouterLink],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <h1>Central de Prioridades</h1>
          <p class="sub">
            Postura, exposições de configuração e vulnerabilidades são dimensões <strong>relacionadas, porém
            distintas</strong> — cobertura de postura, lacunas de configuração e fraquezas observadas em ativos.
            Elas <strong>não formam um único score</strong>: cada fila mantém a própria ordem e a própria fonte.
          </p>
          @if (data()) {
            <p class="freshness">Leitura de {{ fmtDate(data()!.generatedAt) }}</p>
          }
        </div>
        <div class="head-actions">
          <button type="button" class="primary" (click)="analyzeWithAi()" [disabled]="loading() || !!error()">
            Analisar prioridades com IA
          </button>
          <button type="button" class="ghost" (click)="reload()" [disabled]="loading()">
            {{ loading() ? 'Carregando…' : 'Atualizar' }}
          </button>
        </div>
      </header>

      @if (loading()) {
        <div class="panel"><p class="muted">Carregando prioridades…</p></div>
      } @else if (error()) {
        <div class="panel">
          <div class="state error">
            <p class="err">⚠ {{ error() }}</p>
            <button type="button" class="ghost" (click)="retry()">Tentar novamente</button>
          </div>
        </div>
      } @else if (data()) {
        <!-- ---------- Resumo (indicadores existentes, sem novo cálculo) ---------- -->
        <div class="cards">
          <div class="card">
            <span class="card-label">Postura NIST</span>
            <span class="card-value" [class.muted]="posture()!.percentage === null">
              {{ postureText() }}
            </span>
            <span class="card-meta">
              cobertura {{ num(posture()!.coveragePercentage) }}% ·
              {{ posture()!.evaluationState === 'Evaluated' ? 'avaliado' : 'não avaliado' }}
            </span>
          </div>
          <div class="card">
            <span class="card-label">Exposições de configuração</span>
            <span class="card-value">{{ exposures()!.summary.totalOpen }}</span>
            <span class="card-meta">abertas · fonte: {{ exposures()!.summary.sourceLabel }}</span>
          </div>
          <div class="card">
            <span class="card-label">Vulnerabilidades</span>
            <span class="card-value">{{ vulns()!.summary.totalOpen }}</span>
            <span class="card-meta">abertas · {{ vulns()!.summary.distinctCvesOpen }} CVE(s) distinto(s)</span>
          </div>
          <div class="card">
            <span class="card-label">Ativos afetados</span>
            <span class="card-value">{{ vulns()!.summary.affectedAssetsOpen }}</span>
            <span class="card-meta">por vulnerabilidades abertas</span>
          </div>
          <div class="card wide">
            <span class="card-label">Coleta das fontes</span>
            <div class="collect">
              <span class="collect-row">
                <span class="collect-k">Exposições</span>
                @if (exposuresEverCollected()) {
                  <span class="collect-v">{{ fmtDate(exposures()!.summary.lastCollectedAt) }}</span>
                } @else {
                  <span class="collect-v muted">Ainda não coletado</span>
                }
              </span>
              <span class="collect-row">
                <span class="collect-k">Ativos e CVEs</span>
                @if (!vulns()!.summary.neverCollected) {
                  <span class="collect-v">{{ fmtDate(vulns()!.summary.lastCollectedAt) }}</span>
                } @else {
                  <span class="collect-v muted">Ainda não coletado</span>
                }
              </span>
            </div>
          </div>
        </div>

        <!-- ---------- [AEGIS-MVP-PRODUCT-03] Sub-abas da Central ----------
             Achados e planos são leituras da MESMA central, não duas entradas de menu: acrescentar "Planos de
             ação" à navegação principal separaria o problema do trabalho que o endereça. -->
        <div class="subtabs" role="tablist">
          <button type="button" role="tab" [class.on]="tab() === 'achados'" (click)="tab.set('achados')">
            Achados
          </button>
          <button type="button" role="tab" [class.on]="tab() === 'planos'" (click)="tab.set('planos')">
            Planos de ação
            @if (plans().length) { <span class="n">{{ plans().length }}</span> }
          </button>
        </div>

        @if (tab() === 'planos') {
          <div class="queue">
            <div class="queue-head">
              <div>
                <h2>Planos de ação</h2>
                <p class="queue-sub">
                  Ações nascidas de achados de identidade. <strong>A etapa do plano e o resultado no achado
                  são coisas distintas</strong>: encerrar uma ação é uma decisão de gestão sobre o trabalho;
                  o que aconteceu com a exposição só uma validação com evidência pode dizer.
                </p>
              </div>
            </div>
            <div class="panel">
              @if (plansError(); as pe) {
                <div class="state error"><p class="err">⚠ {{ pe }}</p></div>
              } @else if (plans().length === 0) {
                <div class="state empty">
                  <p class="muted">
                    Nenhum plano de ação registrado. Abra um achado de identidade abaixo e crie a primeira ação.
                  </p>
                </div>
              } @else {
                <table class="grid-table">
                  <thead>
                    <tr>
                      <th>Ação</th>
                      <th class="c-tier">Responsável</th>
                      <th class="c-when">Prazo</th>
                      <th class="c-state">Situação do plano</th>
                      <th>Resultado no achado</th>
                      <th class="c-when">Próxima providência</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (p of plans(); track p.id) {
                      <tr class="row">
                        <td>
                          <a
                            class="title link"
                            [routerLink]="['/identity']"
                            [queryParams]="{ finding: p.knightIndicatorId, run: p.originRunId }">
                            {{ p.title }}
                          </a>
                          <span class="meta mono">{{ p.knightIndicatorId }}</span>
                        </td>
                        <td class="c-tier">{{ p.responsiblePerson || '—' }}</td>
                        <td class="c-when">
                          <span class="meta" [class.late]="p.isOverdue">{{ p.dueDate || 'sem prazo' }}</span>
                        </td>
                        <td class="c-state"><span class="badge" [class.bad]="p.isOverdue">{{ situation(p) }}</span></td>
                        <td><span class="meta">{{ result(p) }}</span></td>
                        <td class="c-when"><span class="meta">{{ p.nextStep }}</span></td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </div>
          </div>
        }

        @if (tab() === 'achados') {
        <!-- ---------- Fila de exposições de configuração ---------- -->
        <div class="queue">
          <div class="queue-head">
            <div>
              <h2>Exposições de configuração</h2>
              <p class="queue-sub">
                Lacunas de postura priorizadas pela fonte (rank, depois maior gap). Fonte:
                <strong>{{ exposures()!.summary.sourceLabel }}</strong>.
              </p>
            </div>
            <a class="linknav" routerLink="/exposures">Ver todas →</a>
          </div>
          <div class="panel">
            @if (!exposuresEverCollected()) {
              <div class="state empty">
                <p class="muted">
                  Ainda não coletado. Configure <strong>{{ exposures()!.summary.sourceLabel }}</strong> em
                  <strong>Configurações → Integrações</strong> e execute uma coleta.
                </p>
              </div>
            } @else if (exposures()!.top.length === 0) {
              <div class="state empty">
                <p class="muted">Nenhuma exposição de configuração aberta. Coletado sem achados abertos.</p>
              </div>
            } @else {
              <table class="grid-table">
                <thead>
                  <tr>
                    <th class="c-rank">Rank</th>
                    <th>Recomendação</th>
                    <th class="c-gap">Gap</th>
                    <th class="c-tier">Tier</th>
                    <th class="c-state">Estado</th>
                    <th class="c-when">Observado</th>
                  </tr>
                </thead>
                <tbody>
                  @for (x of exposures()!.top; track x.id) {
                    <tr class="row">
                      <td class="c-rank">{{ x.sourceRank ?? '—' }}</td>
                      <td>
                        <strong class="title">{{ x.displayTitle }}</strong>
                        <span class="meta">{{ x.service || '—' }} · {{ cat(x.category) || '—' }} · {{ reachUnknown }}</span>
                        @if (x.whyItMatters) {
                          <span class="rem">{{ x.whyItMatters }}</span>
                        }
                        @if (x.firstAction) {
                          <span class="rem"><em>Ação:</em> {{ x.firstAction }}</span>
                        }
                      </td>
                      <td class="c-gap"><span class="gap">{{ num(x.gap) }}</span></td>
                      <td class="c-tier">{{ tier(x.tier) || '—' }}</td>
                      <td class="c-state">
                        <span class="badge" [class.ok]="x.lifecycleState === 'Resolved'">
                          {{ x.lifecycleState === 'Resolved' ? 'Resolvida' : 'Aberta' }}
                        </span>
                      </td>
                      <td class="c-when">
                        <span class="meta">de {{ fmtDate(x.firstSeenAt) }}</span>
                        <span class="meta">até {{ fmtDate(x.lastSeenAt) }}</span>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
        </div>

        <!-- ---------- Fila de vulnerabilidades ---------- -->
        <div class="queue">
          <div class="queue-head">
            <div>
              <h2>Vulnerabilidades em ativos</h2>
              <p class="queue-sub">
                Exposições ativo×CVE priorizadas por fato da fonte e criticidade do ativo.
                @if (vulns()!.summary.sources.length > 0) {
                  Fontes: <strong>{{ sourceNames() }}</strong>.
                }
              </p>
            </div>
            <a class="linknav" routerLink="/vulnerabilities">Ver todas →</a>
          </div>
          <div class="panel">
            @if (vulns()!.summary.neverCollected) {
              <div class="state empty">
                <p class="muted">
                  Ainda não coletado. Configure um scanner de vulnerabilidades em
                  <strong>Configurações → Integrações</strong> e execute uma coleta para trazer ativos e CVEs.
                </p>
              </div>
            } @else if (vulns()!.top.length === 0) {
              <div class="state empty">
                <p class="muted">Nenhuma vulnerabilidade aberta. Coletado sem achados abertos.</p>
              </div>
            } @else {
              <table class="grid-table">
                <thead>
                  <tr>
                    <th>Problema</th>
                    <th>Por que importa</th>
                    <th class="c-cvss">Alcance</th>
                    <th>Exploit</th>
                    <th>Fontes</th>
                  </tr>
                </thead>
                <tbody>
                  @for (g of vulns()!.top; track g.cveId) {
                    <tr class="row" [class.resolved]="g.effectiveLifecycle === 'Resolved'">
                      <td>
                        <strong class="title">{{ g.displayTitle }}</strong>
                        <span class="meta mono">{{ g.cveId }} · {{ g.severityLabel }}</span>
                        <span class="rem"><em>Ação:</em> {{ g.firstAction }}</span>
                      </td>
                      <td><span class="meta">{{ g.whyItMatters }}</span></td>
                      <td class="c-cvss">
                        <strong>{{ g.openAssetCount }}</strong>
                        <span class="meta">ativo(s) aberto(s)</span>
                      </td>
                      <td class="c-exploit">
                        @if (g.exploitVerified) {
                          <span class="badge bad">{{ g.exploitLabel }}</span>
                        } @else if (g.publicExploit) {
                          <span class="badge warn">{{ g.exploitLabel }}</span>
                        } @else {
                          <span class="dim">{{ g.exploitLabel }}</span>
                        }
                      </td>
                      <td class="c-src">
                        @for (p of g.providers; track p) {
                          <span class="badge src">{{ p }}</span>
                        } @empty {
                          <span class="dim">—</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
        </div>

        <!-- ---------- [AEGIS-MVP-PRODUCT-02] Fila de achados de identidade (AEGIS KNIGHT) ---------- -->
        <div class="queue">
          <div class="queue-head">
            <div>
              <h2>Achados de identidade</h2>
              <p class="queue-sub">
                Vereditos do <strong>AEGIS KNIGHT</strong> sobre a postura de identidade — régua e score
                próprios, <strong>não somados</strong> à postura NIST nem às vulnerabilidades. Aqui aparecem os
                achados <strong>expostos</strong>; abrir um deles leva à mesma avaliação, com os objetos que
                sustentam o resultado.
              </p>
            </div>
            <a class="linknav" [routerLink]="['/identity']" [queryParams]="{ run: knight()!.runId }">
              Ver avaliação →
            </a>
          </div>
          <div class="panel">
            @if (!knight()!.runId) {
              <div class="state empty">
                <p class="muted">
                  Nenhuma avaliação de identidade executada ainda. Abra o <strong>AEGIS KNIGHT</strong> para
                  executar uma avaliação — ausência de avaliação não é ausência de risco.
                </p>
              </div>
            } @else {
              <div class="knight-bar">
                <span class="kb-item">
                  <span class="kb-k">Origem</span>
                  <span class="kb-v" [class.demo]="knight()!.isDemo">
                    {{ knight()!.isDemo ? 'Demonstração (dados sintéticos)' : knight()!.sourceLabel }}
                  </span>
                </span>
                <span class="kb-item">
                  <span class="kb-k">Score KNIGHT</span>
                  <span class="kb-v">{{ knightScore() }}<span class="kb-s"> · escala própria</span></span>
                </span>
                <span class="kb-item">
                  <span class="kb-k">Cobertura</span>
                  <span class="kb-v">{{ num(knight()!.coverage) }}%</span>
                </span>
                <span class="kb-item">
                  <span class="kb-k">Coleta</span>
                  <span class="kb-v">{{ fmtDate(knight()!.collectedAt) }}</span>
                </span>
              </div>

              @if (knight()!.top.length === 0) {
                <div class="state empty">
                  <p class="muted">
                    Nenhum achado de identidade exposto nesta avaliação.
                    @if (knight()!.notEvaluatedCount > 0) {
                      {{ knight()!.notEvaluatedCount }} indicador(es) seguem <strong>não avaliados</strong> —
                      isso reduz a cobertura e não é o mesmo que conformidade.
                    }
                  </p>
                </div>
              } @else {
                <table class="grid-table">
                  <thead>
                    <tr>
                      <th>Achado</th>
                      <th class="c-tier">Severidade</th>
                      <th class="c-state">Situação</th>
                      <th class="c-cvss">Afetados</th>
                      <th class="c-when">Coleta</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (f of knight()!.top; track f.indicatorId) {
                      <tr class="row">
                        <td>
                          <!-- [AEGIS-MVP-PRODUCT-02] O parametro run viaja junto: sem ele, o clique abriria o mesmo
                               indicador da avaliação MAIS RECENTE, que pode não ser a que produziu esta linha. -->
                          <a
                            class="title link"
                            [routerLink]="['/identity']"
                            [queryParams]="{ finding: f.indicatorId, run: knight()!.runId }">
                            {{ findingTitle(f) }}
                          </a>
                          <span class="meta mono">{{ f.indicatorId }}</span>
                          <span class="rem">{{ findingSituation(f) }}</span>
                        </td>
                        <td class="c-tier"><span class="badge sev" [class]="f.severity">{{ sev(f.severity) }}</span></td>
                        <td class="c-state"><span class="badge">{{ st(f.status) }}</span></td>
                        <td class="c-cvss">
                          <strong>{{ f.affectedObjectCount }}</strong>
                          @if (f.hasAffectedDetail) {
                            <a
                              class="meta link"
                              [routerLink]="['/identity']"
                              [queryParams]="{ finding: f.indicatorId, run: knight()!.runId }">
                              ver afetados
                            </a>
                          } @else {
                            <span class="meta dim">detalhe não preservado</span>
                          }
                        </td>
                        <td class="c-when">
                          <span class="meta">{{ fmtDate(f.collectedAt) }}</span>
                          <!-- [AEGIS-MVP-PRODUCT-03] Com ação ativa, ABRE a existente; sem ela, cria. O link
                               leva à MESMA avaliação do achado, com a origem preservada. -->
                          <a
                            class="meta link"
                            [routerLink]="['/identity']"
                            [queryParams]="{ finding: f.indicatorId, run: knight()!.runId }">
                            {{ planFor(f.indicatorId) ? 'Abrir plano' : 'Criar plano de ação' }}
                          </a>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
          </div>
        </div>

        }

        <p class="foot-note">
          Fatos vêm das fontes de cada fila; a IA apenas explica, correlaciona e recomenda — não altera score,
          CVE, exploit, lifecycle, finding, evidência ou estado de remediação.
        </p>
      }
    </section>
  `,
  styles: [
    `
      /* Alias local da cor de acento (dual-neon cyan): encurta os ~14 usos de color-mix e mantém o
         fallback #26e0ff quando --hud-cyan não está definido. Custom property herda para todo o componente. */
      :host { --c: var(--hud-cyan, #26e0ff); }
      .page { padding: 1.25rem 1.5rem 2rem; display: flex; flex-direction: column; gap: 1.1rem; }
      .page-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; flex-wrap: wrap; }
      h1 { margin: 0; font-size: 1.35rem; }
      h2 { margin: 0; font-size: 1.02rem; }
      .sub { margin: 0.35rem 0 0; max-width: 82ch; opacity: 0.72; font-size: 0.85rem; line-height: 1.45; }
      .freshness { margin: 0.4rem 0 0; font-size: 0.72rem; opacity: 0.55; }
      .head-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
      .muted { opacity: 0.65; font-size: 0.85rem; }
      .dim { opacity: 0.4; }
      .err { color: #ff6b8a; font-size: 0.85rem; }

      .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 0.75rem; }
      .card, .panel { background: color-mix(in srgb, var(--c) 4%, transparent); border: 1px solid color-mix(in srgb, var(--c) 22%, transparent); border-radius: 8px; }
      .card { padding: 0.8rem 0.95rem; display: flex; flex-direction: column; gap: 0.2rem; }
      .card.wide { grid-column: span 2; min-width: 0; }
      .card-label { font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.1em; opacity: 0.6; }
      .card-value { font-size: 1.6rem; font-weight: 600; }
      .card-value.muted { font-size: 1.05rem; opacity: 0.7; }
      .card-meta { font-size: 0.72rem; opacity: 0.6; }
      .collect { display: flex; flex-direction: column; gap: 0.25rem; margin-top: 0.15rem; }
      .collect-row { display: flex; justify-content: space-between; gap: 0.75rem; font-size: 0.78rem; }
      .collect-k { opacity: 0.62; }
      .collect-v { font-size: 0.76rem; }
      .collect-v.muted { font-family: inherit; }

      .queue { display: flex; flex-direction: column; gap: 0.5rem; }
      .subtabs { display: flex; gap: 0.4rem; border-bottom: 1px solid color-mix(in srgb, var(--c) 18%, transparent); }
      .subtabs button { cursor: pointer; background: none; border: none; border-bottom: 2px solid transparent; color: inherit; opacity: 0.6; font: inherit; font-size: 0.85rem; padding: 0.45rem 0.85rem; }
      .subtabs button.on { opacity: 1; color: var(--c); border-bottom-color: var(--c); }
      .subtabs .n { font-size: 0.68rem; margin-left: 0.35rem; opacity: 0.75; }
      .meta.late { color: #ff6b8a; }
      /* [AEGIS-MVP-PRODUCT-02] Barra de contexto da avaliação KNIGHT: origem, score PRÓPRIO e cobertura. */
      .knight-bar { display: flex; gap: 1.4rem; flex-wrap: wrap; padding: 0.55rem 0.7rem 0.75rem; }
      .kb-item { display: flex; flex-direction: column; gap: 0.1rem; }
      .kb-k { font-size: 0.64rem; text-transform: uppercase; letter-spacing: 0.09em; opacity: 0.55; }
      .kb-v { font-size: 0.86rem; }
      .kb-v.demo { color: #ffb020; }
      .kb-s { font-size: 0.68rem; opacity: 0.55; }
      .badge.sev.Critical { border-color: #ff6b8a; color: #ff6b8a; }
      .badge.sev.High { border-color: #ffb020; color: #ffb020; }
      a.link { color: var(--c); text-decoration: none; }
      a.link:hover { text-decoration: underline; }
      .queue-head { display: flex; justify-content: space-between; align-items: flex-end; gap: 1rem; flex-wrap: wrap; }
      .queue-sub { margin: 0.2rem 0 0; max-width: 78ch; opacity: 0.68; font-size: 0.8rem; }
      .linknav { color: var(--c); text-decoration: none; font-size: 0.8rem; white-space: nowrap; border: 1px solid color-mix(in srgb, var(--c) 30%, transparent); border-radius: 5px; padding: 0.35rem 0.7rem; }
      .linknav:hover { background: color-mix(in srgb, var(--c) 12%, transparent); }

      .panel { padding: 0.6rem; overflow-x: auto; }
      .state { padding: 1.4rem 1rem; text-align: center; display: flex; flex-direction: column; gap: 0.75rem; align-items: center; }

      .grid-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
      .grid-table th { text-align: left; font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.6; padding: 0.4rem 0.6rem; border-bottom: 1px solid color-mix(in srgb, var(--c) 18%, transparent); }
      .grid-table td { padding: 0.5rem 0.6rem; vertical-align: top; border-bottom: 1px solid color-mix(in srgb, var(--c) 8%, transparent); }
      .row.resolved { opacity: 0.6; }
      .title { display: block; line-height: 1.3; }
      .rem { display: block; font-size: 0.74rem; opacity: 0.66; margin-top: 0.15rem; max-width: 60ch; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .meta { font-size: 0.72rem; opacity: 0.62; display: block; }
      .asset { color: var(--c); text-decoration: none; }
      .asset:hover { text-decoration: underline; }
      .c-rank { width: 3.5rem; opacity: 0.85; }
      .c-cvss, .c-epss, .c-sev, .c-gap, .c-tier, .c-state, .c-when { white-space: nowrap; }
      .freshness, .collect-v, .mono { font-family: ui-monospace, monospace; }
      .gap, .badge.warn, .sev-medium { color: #f5a524; }
      .gap { font-weight: 600; }
      .mono { font-size: 0.82rem; }
      .badge { font-size: 0.62rem; padding: 0.05rem 0.4rem; border-radius: 3px; border: 1px solid currentColor; text-transform: uppercase; letter-spacing: 0.05em; color: #9aa7c7; }
      .badge.ok { color: var(--c); }
      .badge.bad { color: #ff3d6a; }
      .badge.src { margin-right: 0.2rem; }
      .badge.lc { margin-top: 0.2rem; display: inline-block; }
      .sev-tag { font-size: 0.72rem; padding: 0.1rem 0.45rem; border-radius: 3px; border: 1px solid currentColor; }
      .sev-critical { color: #ff3d6a; }
      .sev-high { color: #ff8a5c; }
      .sev-low { color: #26e0ff; }
      .sev-desconhecida { color: #9aa7c7; }

      .foot-note { margin: 0.2rem 0 0; font-size: 0.74rem; opacity: 0.55; max-width: 90ch; line-height: 1.4; }

      button.primary, button.ghost { color: inherit; border-radius: 5px; font: inherit; cursor: pointer; }
      button.primary { background: color-mix(in srgb, var(--c) 18%, transparent); border: 1px solid var(--c); padding: 0.45rem 1rem; font-size: 0.82rem; }
      button.ghost { background: transparent; border: 1px solid color-mix(in srgb, var(--c) 30%, transparent); padding: 0.4rem 0.8rem; font-size: 0.8rem; }
      button:disabled { opacity: 0.5; cursor: not-allowed; }

      @media (max-width: 720px) { .card.wide { grid-column: span 1; } }
    `,
  ],
})
export class PrioritiesComponent {
  private readonly api = inject(PriorityService);
  private readonly agent = inject(AgentStateService);

  private readonly remediation = inject(RemediationService);

  /** Sub-aba ativa: os achados (as três filas) ou os planos que os endereçam. */
  protected readonly tab = signal<'achados' | 'planos'>('achados');

  /**
   * Planos de ação do tenant. Uma leitura ÚNICA serve às duas sub-abas e ao botão de cada achado — a mesma
   * autoridade que a tela do KNIGHT usa, de modo que os dois lugares não discordem sobre "existe ação ativa?".
   */
  protected readonly plans = signal<ActionPlan[]>([]);
  protected readonly plansError = signal<string | null>(null);

  protected readonly situation = actionSituation;
  protected readonly result = actionResult;

  /** Ação ATIVA de um achado — decide entre "Criar plano de ação" e "Abrir plano". */
  protected planFor(indicatorId: string): ActionPlan | null {
    return activePlanFor(this.plans(), indicatorId);
  }

  protected readonly data = signal<PriorityWorkspace | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly posture = computed(() => this.data()?.posture ?? null);
  protected readonly exposures = computed(() => this.data()?.configurationExposures ?? null);
  protected readonly vulns = computed(() => this.data()?.vulnerabilities ?? null);
  /**
   * [AEGIS-MVP-PRODUCT-02] Fila de achados de identidade. A Central apenas EXIBE o que a autoridade KNIGHT já
   * decidiu — severidade, veredito, evidência e contagem não são recalculados aqui, e o score KNIGHT não é
   * combinado com postura ou vulnerabilidades.
   */
  protected readonly knight = computed(() => this.data()?.identityFindings ?? null);
  // Mesma camada de apresentação da tela do KNIGHT: as duas superfícies não podem chamar o mesmo achado por
  // nomes diferentes, nem uma afirmar mais forte do que a outra sobre a mesma coleta.
  protected readonly findingTitle = findingTitle;
  protected readonly findingSituation = findingSituation;

  /** Score do KNIGHT para exibição: "—" quando null (sem avaliação), nunca "0". */
  protected readonly knightScore = computed(() => scoreDisplay(this.knight()?.score ?? null));

  protected readonly sev = severityLabel;
  protected readonly st = statusLabel;

  protected readonly postureText = computed(() => postureLabel(this.posture()?.percentage ?? null));

  /** Distingue o onboarding (nunca coletado) de "coletado sem exposição aberta" — deriva do resumo do tenant. */
  protected readonly exposuresEverCollected = computed(() => {
    const s = this.exposures()?.summary;
    return !!s && (s.lastCollectedAt != null || s.totalOpen > 0 || s.totalResolved > 0);
  });

  /** Fontes distintas de vulnerabilidade configuradas (provider-neutral: nomes reais, não hardcoded). */
  protected readonly sourceNames = computed(() =>
    (this.vulns()?.summary.sources ?? []).map((s) => s.provider).join(', '));

  // [AEGIS-MVP-LANGUAGE-02 §5] A narrativa de vulnerabilidade (título/porquê/exploit/1ª ação/severidade) é
  // AUTORIDADE do backend e chega pronta em cada VulnerabilityGroup — o frontend NÃO recompõe. Restam helpers de
  // APRESENTAÇÃO puros que traduzem enums da fonte de EXPOSIÇÃO (categoria/tier) que não têm rótulo pronto.
  protected readonly cat = categoryPt;
  protected readonly tier = tierPt;
  protected readonly reachUnknown = EXPOSURE_REACH_UNKNOWN;

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.loadPlans();
    this.api.get().subscribe({
      next: (workspace) => {
        this.data.set(workspace);
        this.loading.set(false);
      },
      error: (err: Error) => {
        this.data.set(null);
        this.error.set(err.message);
        this.loading.set(false);
      },
    });
  }

  /**
   * Lê os planos. Uma falha aqui NÃO derruba a Central: as filas de achados continuam válidas, e a sub-aba de
   * planos mostra o erro em vez de uma lista vazia que se leria como "não há ação alguma".
   */
  private loadPlans(): void {
    this.plansError.set(null);
    this.remediation.list().subscribe({
      next: (plans) => this.plans.set(plans),
      error: (err: Error) => this.plansError.set(err.message),
    });
  }

  protected reload(): void {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  /**
   * Reutiliza o Auditor Virtual GLOBAL, semeando UMA pergunta contextual COMBINADA. O backend já inclui, no
   * contexto tenant-scoped, a postura, as principais exposições e as principais vulnerabilidades abertas — e
   * sabe que os fatos das fontes são AUTORITATIVOS e a resposta é CONSULTIVA. Nenhum código de IA é alterado.
   */
  protected analyzeWithAi(): void {
    this.agent.requestAudit(
      'Analise em conjunto a postura NIST, as exposições de configuração abertas e as vulnerabilidades dos ' +
        'ativos. Explique as relações entre elas e proponha uma sequência de investigação e remediação. ' +
        'Preserve separadamente fatos das fontes, inferências e recomendações. Não crie nem altere score, ' +
        'CVE, exploit, lifecycle, finding, evidência ou estado de remediação.',
    );
  }

  protected num(n: number): string {
    return Number.isInteger(n) ? String(n) : n.toFixed(1);
  }

  protected pctEpss(n: number): string {
    return `${Math.round(n * 100)}%`;
  }

  protected fmtDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '—' : d.toLocaleString('pt-BR');
  }
}
