import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { AuthService } from '../services/auth.service';
import { PostureHistoryService } from '../services/posture-history.service';
import { KnightFrozenReportComponent } from '../components/knight/frozen-report.component';
import { SeverityLevel, severityLabel } from '../models/knight.models';
import {
  PostureComparisonResult,
  PostureExportFormat,
  PostureSnapshotDetail,
  PostureSnapshotSummary,
  PostureSnapshotType,
  evidenceKindLabel,
  incompatibilityLabel,
  itemStatusClass,
  itemStatusLabel,
  scoreDeltaStateLabel,
  scoreDisplay,
  signedDelta,
  snapshotTypeLabel,
  verdictSourceLabel,
} from '../models/posture-history.models';

/**
 * PostureHistoryComponent — histórico auditável COMPARTILHADO (AEGIS Score/NIST e AEGIS KNIGHT). Lista
 * cronológica de fotografias IMUTÁVEIS, publicação controlada por papel (Manager/TenantAdmin), detalhe e
 * comparação entre duas fotografias COMPATÍVEIS (senão, estado explícito de incompatibilidade — nunca um delta
 * enganoso). Não há score combinado entre instrumentos; "não avaliado" é sempre distinto de 0. No detalhe, exporta
 * a fotografia em PDF executivo ou CSV (AEGIS-AUD-034), sempre derivados da MESMA fotografia imutável.
 */
@Component({
  selector: 'app-posture-history',
  standalone: true,
  imports: [DatePipe, KnightFrozenReportComponent],
  template: `
    <section class="page hist">
      <header class="page-head">
        <div class="titles">
          <p class="page-eyebrow">Relatórios</p>
          <h1>Histórico de postura</h1>
          <p class="page-desc">
            Registros de postura publicados e imutáveis do AEGIS Score/NIST e do AEGIS KNIGHT. Compare dois
            registros compatíveis; instrumentos distintos nunca se somam.
          </p>
          <p class="page-meta">Histórico auditável · registros de postura imutáveis</p>
        </div>
        @if (canPublish()) {
          <div class="page-actions">
            <button type="button" class="btn primary" (click)="publish('AegisScoreNist')" [disabled]="busy()">
              {{ publishing() ? 'Publicando…' : 'Publicar AEGIS Score' }}
            </button>
            <button type="button" class="btn real" (click)="publish('Knight')" [disabled]="busy()">
              {{ publishing() ? 'Publicando…' : 'Publicar KNIGHT' }}
            </button>
          </div>
        }
      </header>

      @if (publishError()) {
        <div class="banner err">
          <span>{{ publishError() }}</span>
          <button type="button" class="btn ghost" (click)="publishError.set(null)">Fechar</button>
        </div>
      }

      <!-- Filtro por instrumento -->
      <div class="segmented" role="group" aria-label="Instrumento">
        <button type="button" [class.on]="filter() === null" [attr.aria-pressed]="filter() === null" (click)="setFilter(null)">Todas</button>
        <button type="button" [class.on]="filter() === 'AegisScoreNist'" [attr.aria-pressed]="filter() === 'AegisScoreNist'" (click)="setFilter('AegisScoreNist')">AEGIS Score / NIST</button>
        <button type="button" [class.on]="filter() === 'Knight'" [attr.aria-pressed]="filter() === 'Knight'" (click)="setFilter('Knight')">AEGIS KNIGHT</button>
      </div>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando registros de postura…</p></div></div>
      } @else if (error()) {
        <div class="panel state err">
          <b>{{ error() }}</b>
          <span>O serviço não respondeu agora. Tente novamente em alguns instantes.</span>
          <button type="button" class="btn ghost" (click)="reload()">Tentar novamente</button>
        </div>
      } @else if (snapshots().length === 0) {
        <div class="panel state empty">
          <b>Nenhum registro de postura publicado ainda.</b>
          <span>
            Publique um registro da postura atual para começar o histórico auditável.
            @if (!canPublish()) { Seu papel permite consultar, mas não publicar. }
          </span>
        </div>
      } @else {
        <!-- Barra de comparação -->
        <div class="cmp-bar" [class.ready]="selected().length === 2">
          <span class="lbl">Comparar dois registros de postura:</span>
          <span class="chips">
            @for (id of selected(); track id) {
              <span class="chip">{{ shortId(id) }} <button type="button" (click)="toggleSelect(id)">✕</button></span>
            }
            @if (selected().length === 0) { <span class="hint">selecione até 2 na lista</span> }
          </span>
          <button type="button" class="btn primary sm" [disabled]="selected().length !== 2 || comparing()" (click)="compare()">
            {{ comparing() ? 'Comparando…' : 'Comparar' }}
          </button>
          @if (selected().length > 0) {
            <button type="button" class="btn ghost sm" (click)="clearCompare()">Limpar</button>
          }
        </div>

        @if (compareError()) {
          <div class="banner err"><span>{{ compareError() }}</span><button type="button" class="btn ghost" (click)="compareError.set(null)">Fechar</button></div>
        }

        <!-- Resultado da comparação -->
        @if (comparison(); as cmp) {
          @if (!cmp.compatible) {
            <div class="panel incompat">
              <h3>Registros de postura incompatíveis</h3>
              <p>Estes registros de postura não podem ser comparados — nenhum delta é calculado para não enganar:</p>
              <ul>
                @for (r of cmp.incompatibilityReasons; track r) { <li>{{ incompatibilityLabel(r) }}</li> }
              </ul>
            </div>
          } @else if (cmp.delta) {
            @let d = cmp.delta;
            <div class="panel cmp-result">
              <div class="hd">
                <h3>Comparação</h3>
                <span class="range">
                  {{ cmp.previous?.capturedAt | date: 'dd/MM/yy HH:mm' }} → {{ cmp.current?.capturedAt | date: 'dd/MM/yy HH:mm' }}
                </span>
              </div>
              <div class="deltas">
                <div class="delta">
                  <span class="k">Score</span>
                  <span class="v" [class.up]="(d.scoreDelta ?? 0) > 0" [class.down]="(d.scoreDelta ?? 0) < 0">
                    {{ d.scoreDeltaState === 'Numeric' ? signedDelta(d.scoreDelta) : scoreDeltaStateLabel(d.scoreDeltaState) }}
                  </span>
                </div>
                <div class="delta">
                  <span class="k">Cobertura</span>
                  <span class="v" [class.up]="d.coverageDelta > 0" [class.down]="d.coverageDelta < 0">{{ signedDelta(d.coverageDelta) }} pp</span>
                </div>
                <div class="delta"><span class="k">Conformes</span><span class="v">{{ signedInt(d.counts.compliant) }}</span></div>
                <div class="delta"><span class="k">Não conformes</span><span class="v">{{ signedInt(d.counts.nonCompliant) }}</span></div>
                <div class="delta"><span class="k">Mitigados</span><span class="v">{{ signedInt(d.counts.mitigated) }}</span></div>
                <div class="delta"><span class="k">Não avaliados</span><span class="v">{{ signedInt(d.counts.notEvaluated) }}</span></div>
              </div>
              <div class="changes">
                <div class="chg up">
                  <h4>Melhoraram ({{ d.improved.length }})</h4>
                  @for (c of d.improved; track c.code) { <div class="row"><b>{{ c.code }}</b> {{ itemStatusLabel(c.previousStatus) }} → {{ itemStatusLabel(c.currentStatus) }}</div> }
                  @if (d.improved.length === 0) { <div class="none">—</div> }
                </div>
                <div class="chg down">
                  <h4>Pioraram ({{ d.worsened.length }})</h4>
                  @for (c of d.worsened; track c.code) { <div class="row"><b>{{ c.code }}</b> {{ itemStatusLabel(c.previousStatus) }} → {{ itemStatusLabel(c.currentStatus) }}</div> }
                  @if (d.worsened.length === 0) { <div class="none">—</div> }
                </div>
                <div class="chg neutral">
                  <h4>Passaram a ser avaliados ({{ d.nowEvaluated.length }})</h4>
                  @for (c of d.nowEvaluated; track c.code) { <div class="row"><b>{{ c.code }}</b> → {{ itemStatusLabel(c.currentStatus) }}</div> }
                  @if (d.nowEvaluated.length === 0) { <div class="none">—</div> }
                </div>
                <div class="chg neutral">
                  <h4>Deixaram de ser avaliados ({{ d.noLongerEvaluated.length }})</h4>
                  @for (c of d.noLongerEvaluated; track c.code) { <div class="row"><b>{{ c.code }}</b> {{ itemStatusLabel(c.previousStatus) }} →</div> }
                  @if (d.noLongerEvaluated.length === 0) { <div class="none">—</div> }
                </div>
              </div>
            </div>
          }
        }

        <!-- Lista cronológica -->
        <div class="list">
          @for (s of snapshots(); track s.id) {
            <div class="snap" [class.sel]="isSelected(s.id)" [class.open]="detailId() === s.id">
              <label class="pick">
                <input type="checkbox" [checked]="isSelected(s.id)" (change)="toggleSelect(s.id)" [disabled]="!isSelected(s.id) && selected().length >= 2" />
              </label>
              <span class="badge" [class.knight]="s.type === 'Knight'">{{ snapshotTypeLabel(s.type) }}</span>
              <div class="when">
                <span class="date">{{ s.capturedAt | date: 'dd/MM/yyyy HH:mm' }}</span>
                <span class="src">{{ s.sourceLabel || s.catalogVersion }}</span>
              </div>
              <div class="score">
                @if (s.score !== null) {
                  <!-- O score KNIGHT é uma escala 0–100 própria, não um percentual. -->
                  <span class="n">{{ scoreDisplay(s.score) }}@if (s.type !== 'Knight') {<i>%</i>}</span>
                } @else {
                  <span class="n na">—</span>
                }
                <span class="l">{{ s.score !== null ? 'score' : 'não avaliado' }}</span>
              </div>
              <div class="cov"><span class="n">{{ s.coverage }}<i>%</i></span><span class="l">cobertura</span></div>
              <div class="ver">
                <span class="mono">{{ s.formulaVersion }}</span>
                <span class="hash" title="hash do conteúdo publicado">#{{ shortHash(s.contentHash) }}</span>
              </div>
              <button type="button" class="btn ghost sm" (click)="openDetail(s)">
                {{ detailId() === s.id ? 'Fechar' : 'Detalhe' }}
              </button>
            </div>

            @if (detailId() === s.id) {
              <div class="detail">
                @if (detailLoading()) {
                  <span class="loading-line" role="status"><span class="spinner" aria-hidden="true"></span>Carregando detalhe…</span>
                } @else if (detailError()) {
                  <span class="err-txt">{{ detailError() }}</span>
                } @else if (detail()) {
                  @let d = detail()!;
                  <div class="meta">
                    <div class="mrow"><span class="k">Tipo</span><span class="v">{{ snapshotTypeLabel(d.summary.type) }}</span></div>
                    <!-- [AEGIS-MVP-PRODUCT-03] Cliente e avaliação de origem vêm CONGELADOS na fotografia: o
                         relatório não busca o nome de hoje nem adivinha de qual coleta ele saiu. -->
                    <div class="mrow">
                      <span class="k">Cliente</span>
                      <span class="v">{{ d.summary.clientName || 'não registrado nesta fotografia' }}</span>
                    </div>
                    @if (d.summary.sourceRunId) {
                      <div class="mrow"><span class="k">Avaliação</span><span class="v mono">{{ d.summary.sourceRunId }}</span></div>
                    }
                    @if (d.summary.sourceLabel) { <div class="mrow"><span class="k">Fonte</span><span class="v">{{ d.summary.sourceLabel }}</span></div> }
                    <div class="mrow"><span class="k">Fórmula</span><span class="v mono">{{ d.summary.formulaVersion }}</span></div>
                    <div class="mrow"><span class="k">Catálogo</span><span class="v mono">{{ d.summary.catalogVersion }}</span></div>
                    <div class="mrow"><span class="k">Schema</span><span class="v mono">{{ d.summary.schemaVersion }}</span></div>
                    <div class="mrow"><span class="k">Pontos</span><span class="v">{{ d.achievedPoints }} / {{ d.possiblePoints }} (elegível {{ d.eligiblePoints }})</span></div>
                    <div class="mrow"><span class="k">Recência</span><span class="v">{{ d.summary.dataRecency ? (d.summary.dataRecency | date: 'dd/MM/yyyy HH:mm') : '—' }}</span></div>
                    <div class="mrow"><span class="k">Hash</span><span class="v mono hashfull">{{ d.summary.contentHash }}</span></div>
                  </div>

                  <!-- Exportação executiva (AEGIS-AUD-034): PDF e CSV derivados desta MESMA fotografia. -->
                  <div class="dl">
                    <span class="dl-lbl">Exportar este registro de postura:</span>
                    <button type="button" class="btn real sm" (click)="download('pdf', d.summary.id)" [disabled]="downloading() !== null">
                      {{ downloading() === 'pdf' ? 'Baixando PDF…' : 'Baixar PDF' }}
                    </button>
                    <button type="button" class="btn ghost sm" (click)="download('csv', d.summary.id)" [disabled]="downloading() !== null">
                      {{ downloading() === 'csv' ? 'Baixando CSV…' : 'Baixar CSV' }}
                    </button>
                    <!-- [AEGIS-KNIGHT-MULTICLOUD-01] Relatório interativo autocontido — só para fotografias do KNIGHT. -->
                    @if (d.summary.type === 'Knight') {
                      <button type="button" class="btn ghost sm" (click)="download('html', d.summary.id)" [disabled]="downloading() !== null">
                        {{ downloading() === 'html' ? 'Baixando HTML…' : 'Baixar relatório HTML' }}
                      </button>
                    }
                    @if (downloadError()) {
                      <span class="dl-err">
                        {{ downloadError() }}
                        <button type="button" class="btn ghost xs" (click)="retryDownload()" [disabled]="downloading() !== null">Tentar novamente</button>
                      </span>
                    }
                  </div>

                  @if (d.controls.length) {
                    <div class="tbl-wrap">
                      <table class="data-table tbl">
                        <thead><tr><th>Controle</th><th>Função</th><th>Estado</th><th class="num">Pontos</th><th>Veredito</th><th>Evidência</th></tr></thead>
                        <tbody>
                          @for (c of d.controls; track c.subcategoryCode) {
                            <tr [class.dim]="!c.evaluated">
                              <td class="mono">{{ c.subcategoryCode }}</td>
                              <td>{{ c.functionCode }}</td>
                              <td><span class="st" [class]="itemStatusClass(c.status)">{{ itemStatusLabel(c.status) }}</span></td>
                              <td class="num">{{ c.achievedPoints }} / {{ c.maxPoints }}</td>
                              <td>{{ verdictSourceLabel(c.verdictSource) }}</td>
                              <td class="ev">
                                @if (c.evidenceRefs.length) {
                                  @for (e of c.evidenceRefs; track $index) {
                                    <span class="ref">{{ evidenceKindLabel(e.kind) }}: {{ e.reference }}</span>
                                  }
                                } @else { <span class="ref none">—</span> }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  }

                  <!-- [AEGIS-MVP-PRODUCT-03] O que ficou CONGELADO nesta publicação (limitações da coleta,
                       ações e validações). Componente próprio: é um bloco com tese própria — "isto é o
                       passado e não muda mais" — e não divide o orçamento de CSS desta página. -->
                  @if (d.summary.type === 'Knight') {
                    <app-knight-frozen-report [snapshot]="d" />
                  }

                  @if (d.indicators.length) {
                    <div class="tbl-wrap">
                      <table class="data-table tbl">
                        <thead><tr><th>Indicador</th><th>Severidade</th><th>Estado</th><th class="num">Afetados</th><th>Evidência</th></tr></thead>
                        <tbody>
                          @for (i of d.indicators; track i.indicatorId) {
                            <tr>
                              <td><span class="mono">{{ i.indicatorId }}</span><span class="tt">{{ i.title }}</span></td>
                              <td>{{ severityText(i.severity) }}</td>
                              <td><span class="st" [class]="itemStatusClass(i.status)">{{ itemStatusLabel(i.status) }}</span></td>
                              <td class="num">{{ i.affectedObjectCount }}</td>
                              <td class="ev">{{ i.evidence }}</td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  }
                }
              </div>
            }
          }
        </div>
      }
    </section>
  `,
  styles: [
    `
      /* Página, cabeçalho, painéis, botões, controle segmentado, tabelas e estados: sistema visual global. */
      .btn.real {
        color: var(--cyan);
        background: var(--tint-cyan);
        border-color: rgba(38, 224, 255, 0.45);
      }
      .segmented {
        align-self: flex-start;
      }
      .banner {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
        padding: var(--sp-3) var(--sp-4);
        border: 1px solid rgba(255, 45, 111, 0.35);
        border-left: 3px solid var(--red);
        border-radius: var(--radius);
        background: var(--tint-red);
        color: #ffc2d4;
        font-size: var(--fs-sm);
      }

      /* Barra de comparação: duas fotografias compatíveis por vez. */
      .cmp-bar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
        padding: var(--sp-3) var(--sp-4);
        border: 1px dashed var(--line-strong);
        border-radius: var(--radius-lg);
      }
      .cmp-bar.ready {
        border-style: solid;
        border-color: rgba(38, 224, 255, 0.45);
        background: var(--tint-cyan);
      }
      .cmp-bar .lbl {
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .cmp-bar .chips {
        display: flex;
        flex: 1;
        flex-wrap: wrap;
        gap: 6px;
      }
      .chip {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        padding: 2px var(--sp-2);
        border: 1px solid rgba(38, 224, 255, 0.4);
        border-radius: 6px;
        font-family: var(--mono);
        font-size: var(--fs-meta);
        color: var(--cyan);
      }
      .chip button {
        padding: 0;
        border: 0;
        background: none;
        color: var(--muted);
        cursor: pointer;
      }
      .chip button:hover {
        color: var(--text);
      }

      .incompat {
        border-color: rgba(255, 176, 32, 0.5);
        background: var(--tint-amber);
      }
      .incompat h3 {
        margin-bottom: 6px;
        font-size: var(--fs-panel);
        color: var(--amber);
      }
      .incompat p {
        margin-bottom: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .incompat ul {
        margin: 0;
        padding-left: 18px;
      }
      .incompat li {
        font-size: var(--fs-sm);
      }
      .cmp-result .range {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .deltas {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
        gap: var(--sp-2);
        margin-bottom: var(--sp-4);
      }
      .delta {
        display: flex;
        flex-direction: column;
        gap: 2px;
        padding: 10px var(--sp-3);
        border: 1px solid var(--line);
        border-radius: var(--radius);
      }
      .delta .k,
      .chg h4,
      .score .l,
      .cov .l {
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .delta .v {
        font-size: var(--fs-panel);
        font-weight: 700;
      }
      .delta .v.up {
        color: var(--cyan);
      }
      .delta .v.down {
        color: var(--red-text);
      }
      .changes {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: var(--sp-3);
      }
      .chg h4 {
        margin: 0 0 6px;
      }
      .chg.up h4 {
        color: var(--cyan);
      }
      .chg.down h4 {
        color: var(--red-text);
      }
      .chg .row {
        font-size: var(--fs-sm);
        line-height: var(--lh);
      }
      .chg .row b {
        font-family: var(--mono);
        font-weight: 500;
        color: var(--cyan);
      }
      .chg .none {
        color: var(--muted);
      }

      /* Lista cronológica de fotografias. */
      .list {
        display: flex;
        flex-direction: column;
        gap: var(--sp-2);
      }
      .snap {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3) var(--sp-4);
        padding: var(--sp-3) var(--sp-4);
        border: 1px solid var(--line);
        border-radius: var(--radius);
        background: var(--panel);
      }
      .snap.sel {
        border-color: rgba(38, 224, 255, 0.45);
      }
      .snap.open {
        border-color: rgba(38, 224, 255, 0.3);
        background: linear-gradient(180deg, rgba(38, 224, 255, 0.04), transparent), var(--panel);
      }
      .pick {
        display: flex;
        align-items: center;
      }
      .snap .badge {
        color: var(--cyan);
      }
      .snap .badge.knight {
        color: var(--amber);
      }
      .when {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 150px;
      }
      .when .date {
        font-size: var(--fs-sm);
        font-weight: 500;
      }
      .when .src {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .score,
      .cov {
        display: flex;
        flex-direction: column;
        gap: 1px;
        min-width: 80px;
        text-align: right;
      }
      .score .n,
      .cov .n {
        font-size: 20px;
        font-weight: 700;
      }
      .score .n i,
      .cov .n i {
        font-size: var(--fs-meta);
        font-style: normal;
        color: var(--muted);
      }
      .score .n.na {
        color: var(--muted);
      }
      .ver {
        display: flex;
        flex: 1;
        flex-direction: column;
        gap: 2px;
        min-width: 140px;
      }
      .ver .mono,
      .ver .hash {
        font-family: var(--mono);
        font-size: var(--fs-meta);
      }
      .ver .mono {
        color: var(--cyan);
      }
      .ver .hash {
        color: var(--muted);
      }

      .detail {
        margin: -4px 0 var(--sp-1);
        padding: var(--sp-4);
        border: 1px solid var(--line);
        border-top: none;
        border-radius: 0 0 var(--radius) var(--radius);
        background: rgba(122, 145, 190, 0.03);
      }
      .detail .meta {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: 6px 20px;
        margin-bottom: 14px;
      }
      .mrow {
        display: flex;
        justify-content: space-between;
        gap: 10px;
        padding-bottom: 4px;
        border-bottom: 1px solid var(--line-2);
        font-size: var(--fs-meta);
      }
      .mrow .k {
        color: var(--text-2);
      }
      .mrow .v.mono {
        color: var(--cyan);
      }
      .mrow .v.hashfull {
        word-break: break-all;
      }
      .dl {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        margin: var(--sp-1) 0 10px;
        padding: 10px var(--sp-3);
        border: 1px dashed var(--line-strong);
        border-radius: var(--radius);
      }
      .dl-lbl {
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .dl-err {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        font-size: var(--fs-meta);
        color: var(--red-text);
      }
      .tbl-wrap {
        overflow-x: auto;
        margin-top: var(--sp-3);
      }
      .tbl {
        font-size: var(--fs-meta);
      }
      .tbl th.num,
      .tbl td.num {
        text-align: right;
      }
      .tbl tr.dim td {
        opacity: 0.6;
      }
      .tbl .mono {
        color: var(--cyan);
      }
      .tbl .tt {
        display: block;
        max-width: 220px;
      }
      .tbl .ev {
        max-width: 260px;
        color: var(--text-2);
      }
      .tbl .ref {
        display: block;
      }
      .tbl .ref.none {
        color: var(--muted);
      }
      .st {
        padding: 2px var(--sp-2);
        border-radius: var(--radius-pill);
        font-size: var(--fs-caps);
        font-weight: 600;
        white-space: nowrap;
      }
      .st.ok {
        color: var(--cyan);
        background: rgba(38, 224, 255, 0.1);
      }
      .st.bad {
        color: var(--red-text);
        background: rgba(255, 45, 111, 0.12);
      }
      .st.warn {
        color: var(--amber);
        background: rgba(255, 176, 32, 0.12);
      }
      .st.err {
        color: #ff9a3d;
        background: rgba(255, 154, 61, 0.12);
      }
      .st.mute {
        color: var(--text-2);
        background: rgba(122, 145, 190, 0.12);
      }

      /* Estados que ocupam o painel inteiro (falha, vazio): alinhados à esquerda, com a ação logo abaixo. */
      .panel.state {
        align-items: flex-start;
        gap: 10px;
        padding: var(--sp-5);
        text-align: left;
      }
      .panel.state b {
        font-size: var(--fs-body);
        color: var(--text);
      }
      .panel.state span {
        color: var(--text-2);
      }
      .panel.state.err {
        border-color: rgba(255, 45, 111, 0.4);
      }
      .panel.state.err b {
        color: var(--red-text);
      }
      .loading-line {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .loading-line .spinner {
        width: 14px;
        height: 14px;
      }
      .err-txt {
        font-size: var(--fs-sm);
        color: var(--red-text);
      }
      code {
        padding: 1px 5px;
        border-radius: var(--radius-xs);
        background: rgba(255, 255, 255, 0.06);
        color: var(--text);
      }
      @media (max-width: 720px) {
        .score,
        .cov {
          text-align: left;
        }
      }
    `,
  ],
})
export class PostureHistoryComponent implements OnInit {
  private readonly svc = inject(PostureHistoryService);
  private readonly auth = inject(AuthService);

  readonly snapshots = signal<PostureSnapshotSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly filter = signal<PostureSnapshotType | null>(null);

  readonly publishing = signal(false);
  readonly publishError = signal<string | null>(null);

  readonly selected = signal<string[]>([]);
  readonly comparison = signal<PostureComparisonResult | null>(null);
  readonly comparing = signal(false);
  readonly compareError = signal<string | null>(null);

  readonly detailId = signal<string | null>(null);
  readonly detail = signal<PostureSnapshotDetail | null>(null);
  readonly detailLoading = signal(false);
  readonly detailError = signal<string | null>(null);

  // Exportação PDF/CSV: qual formato está baixando (bloqueia cliques duplicados) e o último erro.
  readonly downloading = signal<PostureExportFormat | null>(null);
  readonly downloadError = signal<string | null>(null);
  private lastDownload: { id: string; format: PostureExportFormat } | null = null;

  protected readonly apiBase = environment.apiBase;

  // Helpers de apresentação expostos ao template.
  protected readonly snapshotTypeLabel = snapshotTypeLabel;
  protected readonly itemStatusLabel = itemStatusLabel;
  protected readonly itemStatusClass = itemStatusClass;
  protected readonly verdictSourceLabel = verdictSourceLabel;
  protected readonly evidenceKindLabel = evidenceKindLabel;
  protected readonly incompatibilityLabel = incompatibilityLabel;
  protected readonly scoreDeltaStateLabel = scoreDeltaStateLabel;
  protected readonly scoreDisplay = scoreDisplay;
  protected readonly signedDelta = signedDelta;

  /** Publicar exige papel Manager/TenantAdmin (o servidor também recusa Analyst — defesa em profundidade). */
  readonly canPublish = computed(() => {
    const role = this.auth.activeRole();
    return role === 'Manager' || role === 'TenantAdmin';
  });

  readonly busy = computed(() => this.publishing() || this.loading());

  ngOnInit(): void {
    this.reload();
  }

  setFilter(type: PostureSnapshotType | null): void {
    this.filter.set(type);
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.svc.list(this.filter() ?? undefined).subscribe({
      next: (rows) => {
        this.snapshots.set(rows);
        this.loading.set(false);
        // Remove seleção/detalhe que não existe mais no conjunto filtrado.
        const ids = new Set(rows.map((r) => r.id));
        this.selected.update((sel) => sel.filter((id) => ids.has(id)));
        if (this.detailId() && !ids.has(this.detailId()!)) this.closeDetail();
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  publish(type: PostureSnapshotType): void {
    this.publishing.set(true);
    this.publishError.set(null);
    // Publicação a partir do histórico segue sem runId: aqui o usuário pede explicitamente "a postura
    // atual". Quem quer publicar UMA avaliação específica faz isso na tela do AEGIS KNIGHT, onde a
    // avaliação aberta é inequívoca.
    this.svc.publish({ type }).subscribe({
      next: (d) => {
        this.publishing.set(false);
        this.reload();
        this.detailId.set(d.summary.id);
        this.detail.set(d);
      },
      error: (e: Error) => {
        this.publishError.set(e.message);
        this.publishing.set(false);
      },
    });
  }

  isSelected(id: string): boolean {
    return this.selected().includes(id);
  }

  toggleSelect(id: string): void {
    this.comparison.set(null);
    this.selected.update((sel) => {
      if (sel.includes(id)) return sel.filter((x) => x !== id);
      if (sel.length >= 2) return sel; // teto de 2
      return [...sel, id];
    });
  }

  clearCompare(): void {
    this.selected.set([]);
    this.comparison.set(null);
    this.compareError.set(null);
  }

  compare(): void {
    const [a, b] = this.selected();
    if (!a || !b) return;
    this.comparing.set(true);
    this.compareError.set(null);
    this.comparison.set(null);
    this.svc.compare(a, b).subscribe({
      next: (res) => {
        this.comparison.set(res);
        this.comparing.set(false);
      },
      error: (e: Error) => {
        this.compareError.set(e.message);
        this.comparing.set(false);
      },
    });
  }

  openDetail(s: PostureSnapshotSummary): void {
    if (this.detailId() === s.id) {
      this.closeDetail();
      return;
    }
    this.detailId.set(s.id);
    this.detail.set(null);
    this.detailError.set(null);
    this.downloadError.set(null);
    this.detailLoading.set(true);
    this.svc.get(s.id).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.detailLoading.set(false);
      },
      error: (e: Error) => {
        this.detailError.set(e.message);
        this.detailLoading.set(false);
      },
    });
  }

  closeDetail(): void {
    this.detailId.set(null);
    this.detail.set(null);
    this.detailError.set(null);
    this.downloadError.set(null);
  }

  /**
   * Baixa PDF ou CSV da fotografia aberta. Impede cliques duplicados (só um download por vez), mostra estado de
   * progresso, exibe erro claro com opção de nova tentativa e dispara o download por Blob via object URL — sempre
   * revogado depois. O arquivo nunca é carregado como string.
   */
  download(format: PostureExportFormat, id: string): void {
    if (this.downloading() !== null) return; // um download por vez
    this.lastDownload = { id, format };
    this.downloading.set(format);
    this.downloadError.set(null);
    this.svc.exportSnapshot(id, format).subscribe({
      next: (file) => {
        this.triggerBlobDownload(file.blob, file.filename);
        this.downloading.set(null);
      },
      error: (e: Error) => {
        this.downloadError.set(e.message);
        this.downloading.set(null);
      },
    });
  }

  retryDownload(): void {
    if (this.lastDownload && this.downloading() === null)
      this.download(this.lastDownload.format, this.lastDownload.id);
  }

  private triggerBlobDownload(blob: Blob, filename: string): void {
    const url = URL.createObjectURL(blob);
    try {
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
    } finally {
      // Sempre revoga; adia um instante para o navegador capturar o download antes de liberar o object URL.
      setTimeout(() => URL.revokeObjectURL(url), 1500);
    }
  }

  /** Severidade do indicador congelado em português; valor desconhecido aparece como veio. */
  severityText(severity: string): string {
    return severityLabel(severity as SeverityLevel) ?? severity;
  }

  shortHash(hash: string): string {
    return hash ? hash.slice(0, 10) : '—';
  }

  shortId(id: string): string {
    return id.slice(0, 8);
  }

  signedInt(value: number): string {
    return (value > 0 ? '+' : '') + value;
  }
}
