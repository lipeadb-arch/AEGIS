import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { AuthService } from '../services/auth.service';
import { PostureHistoryService } from '../services/posture-history.service';
import {
  PostureComparisonResult,
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
 * enganoso). Não há score combinado entre instrumentos; "não avaliado" é sempre distinto de 0. Sem PDF/CSV.
 */
@Component({
  selector: 'app-posture-history',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="hist">
      <p class="eyebrow">AEGIS · Histórico Auditável de Postura · Fotografias imutáveis</p>

      <header class="head">
        <div class="titles">
          <h1>Histórico de Postura</h1>
          <p class="blurb">
            Fotografias publicadas e imutáveis do AEGIS Score/NIST e do AEGIS KNIGHT. Compare duas fotografias
            compatíveis; instrumentos distintos nunca se somam.
          </p>
        </div>
        @if (canPublish()) {
          <div class="actions">
            <button type="button" class="btn run" (click)="publish('AegisScoreNist')" [disabled]="busy()">
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
      <div class="tabs">
        <button type="button" [class.on]="filter() === null" (click)="setFilter(null)">Todas</button>
        <button type="button" [class.on]="filter() === 'AegisScoreNist'" (click)="setFilter('AegisScoreNist')">AEGIS Score / NIST</button>
        <button type="button" [class.on]="filter() === 'Knight'" (click)="setFilter('Knight')">AEGIS KNIGHT</button>
      </div>

      @if (loading()) {
        <div class="panel state"><span class="pulse">Carregando fotografias…</span></div>
      } @else if (error()) {
        <div class="panel state err">
          <b>{{ error() }}</b>
          <span>Verifique se a API está no ar em <code>{{ apiBase }}</code> e tente novamente.</span>
          <button type="button" class="btn ghost" (click)="reload()">Tentar novamente</button>
        </div>
      } @else if (snapshots().length === 0) {
        <div class="panel state empty">
          <b>Nenhuma fotografia publicada ainda.</b>
          <span>
            Publique uma fotografia da postura atual para começar o histórico auditável.
            @if (!canPublish()) { Seu papel permite consultar, mas não publicar. }
          </span>
        </div>
      } @else {
        <!-- Barra de comparação -->
        <div class="cmp-bar" [class.ready]="selected().length === 2">
          <span class="lbl">Comparar duas fotografias:</span>
          <span class="chips">
            @for (id of selected(); track id) {
              <span class="chip">{{ shortId(id) }} <button type="button" (click)="toggleSelect(id)">✕</button></span>
            }
            @if (selected().length === 0) { <span class="hint">selecione até 2 na lista</span> }
          </span>
          <button type="button" class="btn run sm" [disabled]="selected().length !== 2 || comparing()" (click)="compare()">
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
              <h3>Fotografias incompatíveis</h3>
              <p>Estas fotografias não podem ser comparadas — nenhum delta é calculado para não enganar:</p>
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
                  <span class="n">{{ scoreDisplay(s.score) }}<i>%</i></span>
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
                  <span class="pulse">Carregando detalhe…</span>
                } @else if (detailError()) {
                  <span class="err-txt">{{ detailError() }}</span>
                } @else if (detail()) {
                  @let d = detail()!;
                  <div class="meta">
                    <div class="mrow"><span class="k">Tipo</span><span class="v">{{ snapshotTypeLabel(d.summary.type) }}</span></div>
                    @if (d.summary.sourceLabel) { <div class="mrow"><span class="k">Fonte</span><span class="v">{{ d.summary.sourceLabel }}</span></div> }
                    <div class="mrow"><span class="k">Fórmula</span><span class="v mono">{{ d.summary.formulaVersion }}</span></div>
                    <div class="mrow"><span class="k">Catálogo</span><span class="v mono">{{ d.summary.catalogVersion }}</span></div>
                    <div class="mrow"><span class="k">Schema</span><span class="v mono">{{ d.summary.schemaVersion }}</span></div>
                    <div class="mrow"><span class="k">Pontos</span><span class="v">{{ d.achievedPoints }} / {{ d.possiblePoints }} (elegível {{ d.eligiblePoints }})</span></div>
                    <div class="mrow"><span class="k">Recência</span><span class="v">{{ d.summary.dataRecency ? (d.summary.dataRecency | date: 'dd/MM/yyyy HH:mm') : '—' }}</span></div>
                    <div class="mrow"><span class="k">Hash</span><span class="v mono hashfull">{{ d.summary.contentHash }}</span></div>
                  </div>

                  @if (d.controls.length) {
                    <div class="tbl-wrap">
                      <table class="tbl">
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

                  @if (d.indicators.length) {
                    <div class="tbl-wrap">
                      <table class="tbl">
                        <thead><tr><th>Indicador</th><th>Severidade</th><th>Estado</th><th class="num">Afetados</th><th>Evidência</th></tr></thead>
                        <tbody>
                          @for (i of d.indicators; track i.indicatorId) {
                            <tr>
                              <td><span class="mono">{{ i.indicatorId }}</span><span class="tt">{{ i.title }}</span></td>
                              <td>{{ i.severity }}</td>
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
      :host { display: block; padding: 28px 32px 60px; }
      .eyebrow { font-family: var(--mono); font-size: 11px; letter-spacing: 0.14em; color: var(--muted); text-transform: uppercase; margin: 0 0 8px; }
      .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 18px; flex-wrap: wrap; margin: 0 0 18px; }
      .head h1 { font-family: var(--sans); font-size: 24px; color: var(--text); margin: 0 0 4px; }
      .head .blurb { color: var(--muted); font-size: 13px; margin: 0; font-family: var(--mono); letter-spacing: 0.02em; max-width: 640px; }
      .actions { display: flex; gap: 10px; flex-wrap: wrap; }
      .btn { cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600; border-radius: 11px; padding: 9px 16px; transition: 0.15s; border: 1px solid transparent; }
      .btn.sm { padding: 6px 12px; font-size: 11px; }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .btn.run { color: #05070f; background: var(--neon-h); box-shadow: 0 0 14px -3px rgba(38, 224, 255, 0.6); }
      .btn.real { color: var(--cyan); background: rgba(38, 224, 255, 0.08); border-color: rgba(38, 224, 255, 0.45); }
      .btn.ghost { color: var(--text); background: rgba(122, 145, 190, 0.08); border-color: var(--line); }
      .panel { border: 1px solid var(--line); border-radius: 14px; background: rgba(122, 145, 190, 0.03); padding: 18px; }

      .tabs { display: flex; gap: 8px; margin-bottom: 16px; flex-wrap: wrap; }
      .tabs button { font-family: var(--mono); font-size: 11px; color: var(--muted); background: rgba(122, 145, 190, 0.05); border: 1px solid var(--line); border-radius: 999px; padding: 6px 14px; cursor: pointer; }
      .tabs button.on { color: var(--cyan); border-color: rgba(38, 224, 255, 0.45); background: rgba(38, 224, 255, 0.08); }

      .banner { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 16px; padding: 12px 16px; border-radius: 10px; }
      .banner.err { border: 1px solid rgba(255, 45, 111, 0.4); background: rgba(255, 45, 111, 0.06); color: #ffe3ee; font-family: var(--mono); font-size: 12px; }

      .cmp-bar { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 10px 14px; border: 1px dashed var(--line); border-radius: 12px; margin-bottom: 14px; }
      .cmp-bar.ready { border-color: rgba(38, 224, 255, 0.45); }
      .cmp-bar .lbl { font-family: var(--mono); font-size: 11.5px; color: var(--muted); }
      .cmp-bar .chips { display: flex; gap: 6px; flex-wrap: wrap; flex: 1; }
      .cmp-bar .hint { font-family: var(--mono); font-size: 11px; color: var(--muted); opacity: 0.7; }
      .chip { font-family: var(--mono); font-size: 11px; color: var(--cyan); border: 1px solid rgba(38, 224, 255, 0.4); border-radius: 6px; padding: 2px 6px; display: inline-flex; gap: 6px; align-items: center; }
      .chip button { background: none; border: none; color: var(--muted); cursor: pointer; padding: 0; }

      .incompat { border-color: rgba(255, 176, 32, 0.5); background: rgba(255, 176, 32, 0.05); margin-bottom: 16px; }
      .incompat h3 { margin: 0 0 6px; font-size: 14px; color: var(--amber); }
      .incompat p { font-family: var(--mono); font-size: 12px; color: var(--muted); margin: 0 0 8px; }
      .incompat ul { margin: 0; padding-left: 18px; } .incompat li { font-family: var(--mono); font-size: 12px; color: var(--text); }

      .cmp-result { margin-bottom: 16px; }
      .cmp-result .hd { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin-bottom: 12px; flex-wrap: wrap; }
      .cmp-result h3 { margin: 0; font-size: 14px; color: var(--text); }
      .cmp-result .range { font-family: var(--mono); font-size: 11px; color: var(--muted); }
      .deltas { display: grid; grid-template-columns: repeat(auto-fit, minmax(110px, 1fr)); gap: 8px; margin-bottom: 14px; }
      .delta { border: 1px solid var(--line); border-radius: 9px; padding: 8px 10px; display: flex; flex-direction: column; gap: 2px; }
      .delta .k { font-family: var(--mono); font-size: 9px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); }
      .delta .v { font-family: var(--mono); font-size: 14px; font-weight: 700; color: var(--text); }
      .delta .v.up { color: var(--cyan); } .delta .v.down { color: var(--red); }
      .changes { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; }
      .chg h4 { margin: 0 0 6px; font-family: var(--mono); font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; }
      .chg.up h4 { color: var(--cyan); } .chg.down h4 { color: var(--red); } .chg.neutral h4 { color: var(--muted); }
      .chg .row { font-family: var(--mono); font-size: 11px; color: var(--text); line-height: 1.5; }
      .chg .row b { color: var(--cyan); } .chg .none { font-family: var(--mono); font-size: 11px; color: var(--muted); }

      .list { display: flex; flex-direction: column; gap: 8px; }
      .snap { display: flex; align-items: center; gap: 14px; padding: 10px 14px; border: 1px solid var(--line); border-radius: 11px; background: rgba(122, 145, 190, 0.03); }
      .snap.sel { border-color: rgba(38, 224, 255, 0.45); }
      .snap.open { border-color: rgba(38, 224, 255, 0.3); background: rgba(38, 224, 255, 0.03); }
      .pick { display: flex; align-items: center; }
      .badge { font-family: var(--mono); font-size: 9.5px; font-weight: 700; letter-spacing: 0.06em; padding: 3px 9px; border-radius: 999px; white-space: nowrap; color: var(--cyan); border: 1px solid rgba(38, 224, 255, 0.4); background: rgba(38, 224, 255, 0.06); }
      .badge.knight { color: var(--amber); border-color: rgba(255, 176, 32, 0.4); background: rgba(255, 176, 32, 0.06); }
      .when { display: flex; flex-direction: column; gap: 2px; min-width: 150px; }
      .when .date { font-family: var(--mono); font-size: 12px; color: var(--text); }
      .when .src { font-family: var(--mono); font-size: 10px; color: var(--muted); }
      .score, .cov { display: flex; flex-direction: column; gap: 1px; min-width: 74px; text-align: right; }
      .score .n, .cov .n { font-family: var(--display); font-weight: 700; font-size: 18px; color: var(--text); }
      .score .n i, .cov .n i { font-size: 11px; font-style: normal; color: var(--muted); }
      .score .n.na { color: var(--muted); }
      .score .l, .cov .l { font-family: var(--mono); font-size: 9px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); }
      .ver { display: flex; flex-direction: column; gap: 2px; flex: 1; }
      .ver .mono { font-family: var(--mono); font-size: 10.5px; color: var(--cyan); }
      .ver .hash { font-family: var(--mono); font-size: 10px; color: var(--muted); }

      .detail { border: 1px solid var(--line); border-top: none; border-radius: 0 0 11px 11px; padding: 16px; margin: -8px 0 4px; background: rgba(122, 145, 190, 0.02); }
      .detail .meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 6px 18px; margin-bottom: 14px; }
      .mrow { display: flex; justify-content: space-between; gap: 10px; font-family: var(--mono); font-size: 11px; border-bottom: 1px solid rgba(122, 145, 190, 0.1); padding-bottom: 3px; }
      .mrow .k { color: var(--muted); } .mrow .v { color: var(--text); } .mrow .v.mono { color: var(--cyan); }
      .mrow .v.hashfull { word-break: break-all; font-size: 9.5px; }
      .tbl-wrap { overflow-x: auto; margin-top: 10px; }
      .tbl { width: 100%; border-collapse: collapse; font-size: 11.5px; }
      .tbl th { text-align: left; font-family: var(--mono); font-size: 9.5px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); padding: 6px 8px; border-bottom: 1px solid var(--line); white-space: nowrap; }
      .tbl th.num, .tbl td.num { text-align: right; }
      .tbl td { padding: 7px 8px; border-bottom: 1px solid rgba(122, 145, 190, 0.1); color: var(--text); vertical-align: top; }
      .tbl tr.dim td { opacity: 0.6; }
      .tbl .mono { font-family: var(--mono); color: var(--cyan); }
      .tbl .tt { display: block; font-size: 10.5px; color: var(--text); max-width: 200px; }
      .tbl .ev { max-width: 240px; color: var(--muted); }
      .tbl .ref { display: block; font-size: 10px; } .tbl .ref.none { color: var(--muted); }
      .st { font-family: var(--mono); font-size: 10px; font-weight: 700; padding: 2px 7px; border-radius: 6px; white-space: nowrap; }
      .st.ok { color: var(--cyan); background: rgba(38, 224, 255, 0.1); }
      .st.bad { color: var(--red); background: rgba(255, 45, 111, 0.12); }
      .st.warn { color: var(--amber); background: rgba(255, 176, 32, 0.12); }
      .st.err { color: #ff9a3d; background: rgba(255, 154, 61, 0.12); }
      .st.mute { color: var(--muted); background: rgba(122, 145, 190, 0.1); }

      .state { display: flex; flex-direction: column; gap: 10px; align-items: flex-start; }
      .state b { color: var(--text); font-size: 14px; } .state span { font-family: var(--mono); font-size: 12px; color: var(--muted); line-height: 1.5; }
      .state.err { border-color: rgba(255, 45, 111, 0.4); } .state.err b { color: #ffe3ee; }
      .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); letter-spacing: 0.08em; animation: pulse 1.4s ease-in-out infinite; }
      .err-txt { font-family: var(--mono); font-size: 12px; color: #ffb3c9; }
      code { color: var(--text); background: rgba(255, 255, 255, 0.06); padding: 1px 5px; border-radius: 4px; }
      @keyframes pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 0.75; } }
      @media (prefers-reduced-motion: reduce) { .pulse { animation: none; } .btn { transition: none; } }
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
