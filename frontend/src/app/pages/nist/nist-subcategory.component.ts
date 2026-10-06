import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, Subscription, combineLatest } from 'rxjs';
import { GovernanceDocument } from '../../models/governance.models';
import {
  MATURITY_LEVEL_HINTS,
  MATURITY_LEVEL_LABELS,
  NistAiSuggestion,
  NistAssignee,
  NistAvailableEvidence,
  NistEvaluationDraft,
  NistFunctionMeta,
  NistSubcategoryDetail,
  contentOriginLabel,
  cyclePeriodText,
  draftFrom,
  draftGap,
  draftProblem,
  evidenceOriginLabel,
  gapText,
  levelLabel,
  newerEvaluation,
  nistCategoryLabel,
  nistFunctionBySlug,
  nistFunctionTitle,
  resolveSelection,
  responsibleText,
  reviewBadgeClass,
  reviewStateLabel,
  selectionParams,
  stateBadgeClass,
  stateLabel,
  toSaveRequest,
} from '../../models/nist.models';
import { AuthService } from '../../services/auth.service';
import { GovernanceService } from '../../services/governance.service';
import { NistApiError, NistCtx, NistService } from '../../services/nist.service';
import { NistAuditPanelComponent } from './nist-audit-panel.component';
import { NistFindingsWorkComponent } from './nist-findings-work.component';
import { NistProceduresComponent } from './nist-procedures.component';

/**
 * [AEGIS-NIST-JOURNEY-01/02] Uma subcategoria do NIST CSF 2.0 numa RODADA da avaliação: o resultado esperado, a situação
 * atual e o alvo, a justificativa, as lacunas e o risco, o responsável, os procedimentos (examinar, entrevistar, testar),
 * as evidências, os achados com o tratamento, a revisão e a trilha. Toda gravação vai ao servidor com a versão em que o
 * rascunho se baseia (conflito = 409, nada é sobrescrito, a edição fica no formulário); atualizar evidências,
 * procedimentos ou achados não muda essa base — só o recarregamento explícito. Conteúdo trazido de outra rodada ou
 * importado aparece como rascunho a confirmar, nunca como revisão humana. A sugestão da IA só preenche o rascunho.
 * Respostas pedidas para outra subcategoria, rodada, escopo, avaliação ou tenant são descartadas.
 */
@Component({
  selector: 'app-nist-subcategory',
  standalone: true,
  imports: [RouterLink, FormsModule, DatePipe, DecimalPipe, NistProceduresComponent, NistFindingsWorkComponent, NistAuditPanelComponent],
  template: `
    <section class="page">
      @if (fn(); as f) {
        <nav class="crumbs" aria-label="Você está em">
          <a [routerLink]="['/nist']" [queryParams]="params()">AEGIS NIST</a><span aria-hidden="true">›</span>
          <a [routerLink]="['/nist', f.slug]" [queryParams]="params()">{{ nistFunctionTitle(f) }}</a><span aria-hidden="true">›</span>
          <span aria-current="page">{{ code() }}</span>
        </nav>
      }

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a subcategoria…</p></div></div>
      } @else if (error()) {
        <div class="panel"><div class="state error" role="alert"><p class="err">{{ error() }}</p>
          <a class="ghost" [routerLink]="['/nist']">Voltar ao AEGIS NIST</a></div></div>
      } @else if (detail()) {
        @let d = detail()!;
        @let e = d.evaluation;
        <header class="page-head">
          <div>
            <p class="page-eyebrow">{{ nistCategoryLabel(d.categoryCode, d.categoryName) }} · {{ d.categoryCode }}</p>
            <h1>{{ d.title }}</h1>
            <p class="page-meta"><span class="mono">{{ d.code }}</span> ·
              <span [class]="'badge ' + stateBadgeClass(e?.state ?? 'NotEvaluated')">{{ stateLabel(e?.state ?? 'NotEvaluated') }}</span>
              @if (e?.reviewState && e!.reviewState !== 'None') { <span [class]="'badge ' + reviewBadgeClass(e!.reviewState)">{{ reviewStateLabel(e!.reviewState) }}</span> }
              · rodada <strong>{{ d.cycleName ?? '—' }}</strong>@if (d.cycleStatus === 'Closed') { <span class="badge neutral">encerrada</span> }</p>
          </div>
        </header>

        @if (d.cycleStatus === 'Closed') {
          <p class="notice">Esta rodada está encerrada: avaliação, evidências, procedimentos e novos achados ficam só para consulta. Planos de tratamento continuam.</p>
        }
        @if (e && !e.humanConfirmed) {
          <section class="notice warn" role="status" aria-label="Conteúdo a confirmar">
            <strong>{{ contentOriginLabel(e.contentOrigin ?? 'Analyst') }}.</strong>
            @if (e.originNote) { {{ e.originNote }} }
            Este conteúdo não conta como avaliação desta rodada nem entra nas médias até alguém revisá-lo e gravá-lo nesta tela.
          </section>
        }

        <section class="panel" aria-labelledby="sc-outcome">
          <div class="hd"><h3 id="sc-outcome">Resultado esperado</h3></div>
          @if (d.summary) { <p><strong>{{ d.summary }}</strong></p> }
          <p class="muted" lang="en"><span class="lbl">Texto oficial do NIST CSF 2.0 (inglês):</span> {{ d.officialOutcome }}</p>
          @if (d.impact) { <p><span class="lbl">Por que importa:</span> {{ d.impact }}</p> }
          @if (d.initialAction) { <p><span class="lbl">Primeiro passo de melhoria:</span> {{ d.initialAction }}</p> }
          @if (d.implementationExamples) {
            <details><summary>Exemplos de implementação (NIST, inglês)</summary><p class="pre" lang="en">{{ d.implementationExamples }}</p></details>
          }
          <p class="hint">Título e explicação em linguagem clara: redação do AEGIS, não tradução oficial.</p>
        </section>

        @if (d.reference; as r) {
          <section class="panel ref" aria-labelledby="sc-ref">
            <div class="hd"><h3 id="sc-ref">Referência: rodada {{ r.cycleName }}</h3><span class="hint">somente leitura · não é a avaliação desta rodada</span></div>
            <p>{{ stateLabel(r.state) }} · atual {{ levelLabel(r.currentLevel) }} · alvo {{ levelLabel(r.targetLevel) }}@if (r.notApplicable) { · não se aplicava }
              · {{ r.evidenceCount }} evidência(s) · {{ r.proceduresPerformed }} procedimento(s) realizado(s) · {{ r.findings }} achado(s)</p>
            @if (r.rationale) { <p class="muted pre">Justificativa: {{ r.rationale }}</p> }
            @if (r.gaps) { <p class="muted pre">Lacunas: {{ r.gaps }}</p> }
            <p class="hint">Confirmada por {{ r.reviewedByName ?? '—' }}@if (r.reviewedAt) { em {{ r.reviewedAt | date: 'dd/MM/yyyy' }} }</p>
          </section>
        }

        <section class="panel" aria-labelledby="sc-eval">
          <div class="hd"><h3 id="sc-eval">Avaliação</h3>
            <span class="hint">escala 1–5 autoral do AEGIS · metodologia {{ d.methodologyVersion }}</span></div>
          @if (!canWrite()) { <p class="notice">Seu papel permite consultar, mas não registrar (requer Manager ou TenantAdmin).</p> }
          <form (ngSubmit)="save(d)">
            <fieldset [disabled]="!canEdit() || saving()">
              <label class="check"><input type="checkbox" name="na" [(ngModel)]="draft.notApplicable" /> Este resultado não se aplica ao escopo</label>
              @if (!draft.notApplicable) {
                <div class="levels">
                  <fieldset class="lv-set">
                    <legend>Situação atual</legend>
                    <label class="lv-opt"><input type="radio" name="cur" [ngModel]="draft.currentLevel" (ngModelChange)="draft.currentLevel = $event" [value]="null" /> Sem nível</label>
                    @for (l of levels; track l) {
                      <label class="lv-opt" [title]="hints[l]"><input type="radio" name="cur" [ngModel]="draft.currentLevel" (ngModelChange)="draft.currentLevel = $event" [value]="l" /> {{ l }} · {{ labels[l] }}</label>
                    }
                  </fieldset>
                  <fieldset class="lv-set">
                    <legend>Alvo</legend>
                    <label class="lv-opt"><input type="radio" name="tgt" [ngModel]="draft.targetLevel" (ngModelChange)="draft.targetLevel = $event" [value]="null" /> Sem nível</label>
                    @for (l of levels; track l) {
                      <label class="lv-opt" [title]="hints[l]"><input type="radio" name="tgt" [ngModel]="draft.targetLevel" (ngModelChange)="draft.targetLevel = $event" [value]="l" /> {{ l }} · {{ labels[l] }}</label>
                    }
                  </fieldset>
                </div>
                <p>Lacuna: <strong>{{ gapText(gap()) }}</strong>
                  @if (gap() === null) { <span class="muted"> — registre atual e alvo para determiná-la.</span> }</p>
              }
              <div class="grid">
                <label class="field wide"><span class="field-label">{{ draft.notApplicable ? 'Por que não se aplica (obrigatório)' : 'Justificativa' }}</span>
                  <textarea name="rat" rows="3" maxlength="4000" [(ngModel)]="draft.rationale"></textarea></label>
                @if (!draft.notApplicable) {
                  <label class="field"><span class="field-label">Observações sobre a situação atual</span><textarea name="cc" rows="3" maxlength="4000" [(ngModel)]="draft.currentComments"></textarea></label>
                  <label class="field"><span class="field-label">Observações sobre o alvo</span><textarea name="tc" rows="3" maxlength="4000" [(ngModel)]="draft.targetComments"></textarea></label>
                  <label class="field"><span class="field-label">Lacunas observadas</span><textarea name="gp" rows="3" maxlength="4000" [(ngModel)]="draft.gaps"></textarea></label>
                  <label class="field"><span class="field-label">Risco ou impacto (quando fundamentado)</span><textarea name="rk" rows="3" maxlength="2000" [(ngModel)]="draft.riskImpact"></textarea></label>
                  <label class="field wide"><span class="field-label">Orientação de melhoria / ação de tratamento</span><textarea name="ig" rows="2" maxlength="4000" [(ngModel)]="draft.improvementGuidance"></textarea></label>
                }
                <label class="field"><span class="field-label">Responsável pela prática</span>
                  <select name="om" [(ngModel)]="draft.ownerMode">
                    <option value="none">Sem responsável</option><option value="user">Usuário do tenant</option>
                    <option value="external">Contato externo</option><option value="text">Texto livre (registro anterior)</option></select></label>
                @if (draft.ownerMode === 'user') {
                  <label class="field"><span class="field-label">Usuário</span>
                    <select name="ou" [(ngModel)]="draft.ownerUserId"><option value="">Escolha…</option>
                      @for (a of assignees(); track a.userId) { <option [value]="a.userId">{{ a.displayName }}</option> }
                      @if (draft.ownerUserId && !knownUser(draft.ownerUserId)) { <option [value]="draft.ownerUserId">{{ e?.owner?.name ?? 'Usuário' }} (inativo)</option> }
                    </select></label>
                } @else if (draft.ownerMode !== 'none') {
                  <label class="field"><span class="field-label">Nome</span><input name="own" maxlength="200" [(ngModel)]="draft.ownerName" /></label>
                  @if (draft.ownerMode === 'external') { <label class="field"><span class="field-label">Contato</span><input name="oc" maxlength="200" [(ngModel)]="draft.ownerContact" /></label> }
                }
              </div>
            </fieldset>
            @if (canEdit()) {
              <div class="actions">
                <button type="submit" class="primary" [disabled]="saving() || busy() || !!problem()">
                  {{ saving() ? 'Gravando…' : e && !e.humanConfirmed ? 'Confirmar e gravar como avaliação desta rodada' : 'Gravar avaliação' }}</button>
                @if (problem(); as pb) { <span class="muted">{{ pb }}</span> }
              </div>
            }
          </form>
          @if (saveError(); as se) {
            <div class="notice error" role="alert">{{ se }}
              @if (conflict()) { <button type="button" class="ghost sm" (click)="reload()">Recarregar a versão atual</button> }</div>
          }
          @if (savedNote()) { <p class="notice" role="status">{{ savedNote() }}</p> }
          @if (staleBase()) {
            <p class="notice warn" role="status">Outra gravação (versão {{ e?.version }}) ocorreu depois que este rascunho
              foi aberto (versão {{ baseVersion }}). Ao gravar, o AEGIS acusará conflito; a sua edição continua no formulário.
              <button type="button" class="ghost sm" (click)="reload()">Recarregar a versão atual</button></p>
          }
          @if (e) {
            <p class="hint">Responsável: {{ responsibleText(e.owner, e.ownerName) }} ·
              @if (e.humanConfirmed) { confirmada por {{ e.reviewedByName ?? 'autor não identificado' }}@if (e.reviewedAt) { em {{ e.reviewedAt | date: 'dd/MM/yyyy HH:mm' }} } }
              @else { ainda não confirmada nesta rodada }
              · versão {{ e.version }}</p>
          } @else { <p class="hint">Ainda sem avaliação registrada nesta rodada.</p> }
        </section>

        <section class="panel" aria-labelledby="sc-rev">
          <div class="hd"><h3 id="sc-rev">Avaliador e revisor</h3><span class="hint">usuários ativos do tenant · designar não concede privilégio</span></div>
          <p>Avaliador: <strong>{{ e?.assessorName ?? 'não designado' }}</strong> · Revisor: <strong>{{ e?.reviewerName ?? 'não designado' }}</strong></p>
          <p>Decisão do revisor: <span [class]="'badge ' + reviewBadgeClass(e?.reviewState)">{{ reviewStateLabel(e?.reviewState ?? 'None') }}</span>
            @if (e?.reviewDecisionByName) { <span class="muted"> · {{ e!.reviewDecisionByName }} em {{ e!.reviewDecisionAt | date: 'dd/MM/yyyy HH:mm' }}</span> }</p>
          @if (e?.reviewDecisionNote) { <p class="muted pre">“{{ e!.reviewDecisionNote }}”</p> }
          @if (canEdit()) {
            @if (dirty()) { <p class="hint">Grave ou descarte o rascunho da avaliação antes de designar ou revisar.</p> }
            <div class="grid">
              <label class="field"><span class="field-label">Avaliador</span>
                <select name="as" [(ngModel)]="assessorChoice"><option value="">Sem designação</option>@for (a of assignees(); track a.userId) { <option [value]="a.userId">{{ a.displayName }}</option> }</select></label>
              <label class="field"><span class="field-label">Revisor</span>
                <select name="rv" [(ngModel)]="reviewerChoice"><option value="">Sem designação</option>@for (a of assignees(); track a.userId) { <option [value]="a.userId">{{ a.displayName }}</option> }</select></label>
              <button type="button" class="ghost sm" (click)="assign(d)" [disabled]="busy() || saving() || dirty()">Designar</button>
            </div>
            @if (e && e.humanConfirmed) {
              <div class="grid">
                <label class="field wide"><span class="field-label">Nota do revisor (obrigatória para devolver)</span>
                  <textarea name="rn" rows="2" maxlength="2000" [(ngModel)]="reviewNote"></textarea></label>
                <div class="actions wide">
                  <button type="button" class="ghost sm" (click)="review(d, 'Approved')" [disabled]="busy() || saving() || dirty()">Aprovar esta versão</button>
                  <button type="button" class="ghost sm" (click)="review(d, 'ChangesRequested')" [disabled]="busy() || saving() || dirty() || reviewNote.trim().length < 10">Devolver para ajuste</button>
                  <span class="hint">Quem gravou a versão vigente não a revisa.</span>
                </div>
              </div>
            }
          }
          @if (roleError(); as re) {
            <div class="notice error" role="alert">{{ re }} @if (roleConflict()) { <button type="button" class="ghost sm" (click)="reload()">Recarregar a versão atual</button> }</div>
          }
        </section>

        <app-nist-procedures [ctx]="ctx()!" [code]="d.code" [procedures]="d.procedures ?? []" [evidence]="d.evidence" [canEdit]="canEdit()" (changed)="refreshSoft()" />

        <section class="panel" aria-labelledby="sc-ai">
          <div class="hd"><h3 id="sc-ai">Sugestão da IA</h3><span class="hint">apoio à interpretação — não é revisão</span></div>
          <p class="muted">A IA lê as observações e as evidências vinculadas e sugere uma situação atual. Nada é gravado: use a sugestão como rascunho e grave só depois de revisar.</p>
          @if (canEdit()) {
            <button type="button" class="ghost sm" (click)="suggest(d)" [disabled]="suggesting()">{{ suggesting() ? 'Consultando…' : 'Pedir sugestão' }}</button>
          }
          @if (aiError()) { <p class="notice warn" role="status">{{ aiError() }}</p> }
          @if (suggestion(); as s) {
            <div class="ai">
              <p><strong>Sugestão: {{ s.suggestedCurrentLevel }} · {{ labels[s.suggestedCurrentLevel] }}</strong> · confiança {{ s.confidence * 100 | number: '1.0-0' }}%
                @if (s.simulated) { <span class="badge warn">Simulada — sem IA real neste ambiente</span> }</p>
              <p class="pre">{{ s.rationale }}</p>
              <button type="button" class="ghost sm" (click)="useSuggestion(s)">Usar como rascunho da situação atual</button>
            </div>
          }
        </section>

        <section class="panel" aria-labelledby="sc-ev">
          <div class="hd"><h3 id="sc-ev">Evidências</h3><span class="hint">{{ d.evidence.length }} vinculada(s) nesta rodada</span></div>
          @if (d.evidence.length === 0) { <p class="muted">Nenhuma evidência vinculada ainda.</p> }
          <ul class="ev-list">
            @for (ev of d.evidence; track ev.id) {
              <li>
                <div class="ev-top"><span class="badge info">{{ evidenceOriginLabel(ev.originKind) }}</span><strong>{{ ev.title }}</strong>
                  @if (ev.uri) { <a [href]="ev.uri" target="_blank" rel="noopener noreferrer">abrir link<span class="sr-only"> (nova aba)</span></a> }</div>
                <p class="muted">{{ ev.originLabel ?? '' }} · data na origem {{ ev.collectedAt | date: 'dd/MM/yyyy' : 'UTC' }} · vinculada por {{ ev.recordedByName ?? '—' }} em {{ ev.linkedAt | date: 'dd/MM/yyyy' }}</p>
                @if (ev.originScope) { <p class="muted">Escopo da coleta: {{ ev.originScope }}</p> }
                @if (ev.notes) { <p>{{ ev.notes }}</p> }
                @if (canEdit()) { <button type="button" class="ghost xs" (click)="remove(d, ev.id)" [disabled]="busy() || saving()">Retirar vínculo</button> }
              </li>
            }
          </ul>

          <h4>Evidências disponíveis na plataforma</h4>
          @if (d.availableEvidence.length === 0) {
            <p class="muted">Nenhuma evidência técnica ou documental mapeada para esta subcategoria.</p>
          }
          <ul class="ev-list">
            @for (a of d.availableEvidence; track a.originKind + (a.knightRunId ?? '') + (a.knightIndicatorId ?? '') + (a.documentId ?? '')) {
              <li>
                <div class="ev-top"><span class="badge neutral">{{ evidenceOriginLabel(a.originKind) }}</span><strong>{{ a.title }}</strong>
                  @if (a.status) { <span class="badge violet">{{ a.status }}</span> }
                  @if (a.isDemo) { <span class="badge warn">Demonstração</span> }</div>
                <p class="muted">{{ a.originLabel }} @if (a.collectedAt) { · {{ a.collectedAt | date: 'dd/MM/yyyy' : 'UTC' }} } @if (a.originScope) { · {{ a.originScope }} }</p>
                <p class="muted">Critério: {{ a.criterion }} @if (a.limitation) { <strong>{{ a.limitation }}</strong> }</p>
                @if (canEdit()) {
                  @if (a.alreadyLinked) { <span class="hint">Já vinculada</span> }
                  @else if (linkable(a)) { <button type="button" class="ghost xs" (click)="linkAvailable(d, a)" [disabled]="busy() || saving()">Vincular</button> }
                }
              </li>
            }
          </ul>

          @if (canEdit()) {
            <details class="add">
              <summary>Vincular documento da biblioteca</summary>
              @if (documents() === null) { <button type="button" class="ghost xs" (click)="loadDocuments()">Listar documentos</button> }
              @else if (documents()!.length === 0) { <p class="muted">A biblioteca não tem documentos. <a routerLink="/nist/gv/documentos">Enviar documento</a></p> }
              @else {
                <div class="row">
                  <label class="field"><span class="field-label">Documento</span>
                    <select name="doc" [(ngModel)]="docChoice">
                      @for (doc of documents()!; track doc.id) { <option [value]="doc.id">{{ doc.title }}</option> }
                    </select></label>
                  <label class="field"><span class="field-label">O que este documento demonstra</span><input name="docnote" maxlength="2000" [(ngModel)]="docNote" /></label>
                  <button type="button" class="primary sm" (click)="linkDocument(d)" [disabled]="busy() || saving() || !docChoice">Vincular documento</button>
                </div>
              }
            </details>
            <details class="add">
              <summary>Registrar evidência (link, entrevista, observação)</summary>
              <div class="row">
                <label class="field"><span class="field-label">Título</span><input name="mt" maxlength="300" [(ngModel)]="manual.title" /></label>
                <label class="field"><span class="field-label">Tipo</span>
                  <select name="mty" [(ngModel)]="manual.type"><option value="Interview">Entrevista</option><option value="Link">Link</option><option value="Document">Documento externo</option><option value="Screenshot">Captura</option></select></label>
                <label class="field"><span class="field-label">Link (opcional)</span><input name="mu" type="url" maxlength="2000" [(ngModel)]="manual.uri" placeholder="https://" /></label>
                <label class="field"><span class="field-label">Data</span><input name="md" type="date" [(ngModel)]="manual.date" /></label>
                <label class="field wide"><span class="field-label">Observação</span><textarea name="mn" rows="2" maxlength="2000" [(ngModel)]="manual.notes"></textarea></label>
                <button type="button" class="primary sm" (click)="linkManual(d)" [disabled]="busy() || saving() || manual.title.trim().length === 0">Registrar evidência</button>
              </div>
            </details>
          }
          @if (evidenceError()) { <p class="notice error" role="alert">{{ evidenceError() }}</p> }
        </section>

        <app-nist-findings-work [ctx]="ctx()!" [detail]="d" [assignees]="assignees()" [canEdit]="canEdit()" [canWrite]="canWrite()" [openId]="openFinding()" (changed)="refreshSoft()" />

        <section class="panel" aria-labelledby="sc-posture">
          <div class="hd"><h3 id="sc-posture">Postura do ambiente (AEGIS Score)</h3><span class="hint">outro conceito · 0–100 · não entra na maturidade</span></div>
          @if (d.posture; as p) {
            <p>Controle {{ postureLabel(p.status) }} por {{ p.verdictSource === 'Telemetry' ? 'telemetria' : 'análise documental' }} ·
              {{ p.achievedPoints }} de {{ p.maxPoints }} pontos · em {{ p.lastEvaluatedAt | date: 'dd/MM/yyyy' }}</p>
          } @else {
            <p class="muted">Sem leitura de postura para este controle no ambiente.</p>
          }
        </section>

        <app-nist-audit-panel [assessmentId]="d.assessmentId" [cycleId]="ctx()!.cycleId" [scopeId]="d.scopeId" [code]="d.code" [refresh]="auditTick()" />
      }
    </section>
  `,
  styles: [
    `
      .crumbs { display: flex; flex-wrap: wrap; gap: 6px; font-size: var(--fs-sm); color: var(--muted); }
      .crumbs a { color: var(--text-2); }
      .lbl { color: var(--muted); font-weight: 600; }
      .pre { white-space: pre-line; }
      fieldset { border: 0; margin: 0; padding: 0; min-width: 0; }
      .levels { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: var(--sp-4); margin: var(--sp-3) 0; }
      .lv-set { display: flex; flex-direction: column; gap: 6px; }
      .lv-set legend { font-weight: 600; margin-bottom: 6px; }
      .lv-opt, .check { display: flex; align-items: center; gap: var(--sp-2); font-size: var(--fs-sm); }
      .grid, .row { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: var(--sp-3); margin-top: var(--sp-3); align-items: end; }
      .wide { grid-column: 1 / -1; }
      .actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-3); margin-top: var(--sp-3); }
      .ev-list { list-style: none; padding: 0; margin: 0 0 var(--sp-3); display: flex; flex-direction: column; }
      .ev-list li { padding: var(--sp-3) 0; border-top: 1px solid var(--line-2); display: flex; flex-direction: column; gap: 4px; align-items: flex-start; }
      .ev-top { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-2); }
      .ev-list p, section p { overflow-wrap: anywhere; }
      .ev-list p { margin: 0; }
      .add { margin-top: var(--sp-3); }
      .add summary { cursor: pointer; font-weight: 500; }
      .ai { margin-top: var(--sp-3); }
      .ref { border-style: dashed; }
      h4 { margin: var(--sp-4) 0 var(--sp-2); font-size: var(--fs-body); }
    `,
  ],
})
export class NistSubcategoryComponent {
  private readonly nist = inject(NistService);
  private readonly governance = inject(GovernanceService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly levels = [1, 2, 3, 4, 5];
  protected readonly labels = MATURITY_LEVEL_LABELS;
  protected readonly hints = MATURITY_LEVEL_HINTS;
  protected readonly gapText = gapText;
  protected readonly levelLabel = levelLabel;
  protected readonly stateLabel = stateLabel;
  protected readonly stateBadgeClass = stateBadgeClass;
  protected readonly reviewStateLabel = reviewStateLabel;
  protected readonly reviewBadgeClass = reviewBadgeClass;
  protected readonly contentOriginLabel = contentOriginLabel;
  protected readonly responsibleText = responsibleText;
  protected readonly cyclePeriodText = cyclePeriodText;
  protected readonly nistFunctionTitle = nistFunctionTitle;
  protected readonly nistCategoryLabel = nistCategoryLabel;
  protected readonly evidenceOriginLabel = evidenceOriginLabel;

  protected readonly fn = signal<NistFunctionMeta | null>(null);
  protected readonly code = signal('');
  protected readonly ctx = signal<NistCtx | null>(null);
  protected readonly detail = signal<NistSubcategoryDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly busy = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly conflict = signal(false);
  protected readonly savedNote = signal<string | null>(null);
  protected readonly evidenceError = signal<string | null>(null);
  protected readonly roleError = signal<string | null>(null);
  protected readonly roleConflict = signal(false);
  protected readonly suggesting = signal(false);
  protected readonly suggestion = signal<NistAiSuggestion | null>(null);
  protected readonly aiError = signal<string | null>(null);
  protected readonly documents = signal<GovernanceDocument[] | null>(null);
  protected readonly assignees = signal<NistAssignee[]>([]);
  protected readonly openFinding = signal<string | null>(null);
  protected readonly auditTick = signal(0);

  /** Rascunho do formulário (ngModel); os derivados (lacuna, pendência) são relidos a cada detecção de mudanças. */
  protected draft: NistEvaluationDraft = draftFrom(null);
  /** Versão da avaliação em que o rascunho se baseia: só muda ao carregar/recarregar ou ao gravar com sucesso. */
  protected baseVersion = 0;
  private baseDraft = JSON.stringify(draftFrom(null));
  protected docChoice = '';
  protected docNote = '';
  protected manual = { title: '', type: 'Interview', uri: '', date: '', notes: '' };
  protected assessorChoice = '';
  protected reviewerChoice = '';
  protected reviewNote = '';

  /**
   * Contexto da tela (tenant · avaliação · rodada · escopo · subcategoria). Toda resposta confere o contexto em que foi
   * pedida: trocar de seleção descarta leituras, gravações, evidências e sugestões ainda em curso — uma escrita enviada pode
   * terminar no servidor, mas sua resposta não repovoa outra tela. A leitura substituída também é cancelada.
   */
  private gen = 0;
  private ctxKey: string | null = null;
  private ctxTenant: string | null = null;
  private readTicket = 0;
  private readSub: Subscription | null = null;
  private assigneesTenant: string | null | undefined = undefined;

  protected readonly canWrite = computed(() => ['Manager', 'TenantAdmin'].includes(this.auth.activeRole() ?? ''));
  /** Pode editar a avaliação desta rodada: papel de escrita E rodada aberta. */
  protected readonly canEdit = computed(() => this.canWrite() && this.detail()?.cycleStatus !== 'Closed');
  protected readonly params = computed((): Record<string, string> => {
    const c = this.ctx();
    return c ? { avaliacao: c.assessmentId, rodada: c.cycleId, escopo: c.scopeId } : {};
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.leave());
    combineLatest([this.route.paramMap, this.route.queryParamMap, this.route.fragment])
      .pipe(takeUntilDestroyed())
      .subscribe(([p, q, frag]) => {
        const code = (p.get('code') ?? '').toUpperCase();
        const a = q.get('avaliacao');
        const c = q.get('rodada');
        const s = q.get('escopo');
        const tenant = this.auth.activeTenantId();
        const key = [tenant, a, c, s, code].join('|');
        this.fn.set(nistFunctionBySlug(p.get('fn')));
        this.openFinding.set(frag?.startsWith('achado-') ? frag.slice(7) : null);
        if (key === this.ctxKey) return;
        this.enter(key, tenant);
        this.code.set(code);
        if (!a || !s) {
          this.ctx.set(null);
          this.loading.set(false);
          this.error.set('Abra a subcategoria a partir de uma avaliação, de uma rodada e de um escopo.');
          return;
        }
        this.loadAssignees(tenant);
        if (!c) {
          // Link anterior às rodadas (#87): resolve a rodada mais recente da avaliação e reescreve a URL.
          this.resolveCycle(a, s);
          return;
        }
        this.ctx.set({ assessmentId: a, cycleId: c, scopeId: s });
        this.reload();
      });
  }

  /** Nova seleção: invalida o que estava em curso e limpa todo o estado da anterior. */
  private enter(key: string, tenant: string | null): void {
    this.leave();
    this.ctxKey = key;
    this.ctxTenant = tenant;
    this.detail.set(null);
    this.draft = draftFrom(null);
    this.baseVersion = 0;
    this.baseDraft = JSON.stringify(this.draft);
    this.loading.set(true);
    this.error.set(null);
    this.saving.set(false);
    this.busy.set(false);
    this.saveError.set(null);
    this.conflict.set(false);
    this.savedNote.set(null);
    this.evidenceError.set(null);
    this.roleError.set(null);
    this.roleConflict.set(false);
    this.suggesting.set(false);
    this.suggestion.set(null);
    this.aiError.set(null);
    this.documents.set(null);
    this.docChoice = '';
    this.docNote = '';
    this.manual = { title: '', type: 'Interview', uri: '', date: '', notes: '' };
    this.reviewNote = '';
  }

  private leave(): void {
    this.gen++;
    this.ctxKey = null;
    this.readTicket++;
    this.readSub?.unsubscribe();
    this.readSub = null;
  }

  /** A resposta pedida na geração `gen` ainda pertence à tela (mesma seleção, mesmo tenant, tela viva)? */
  private current(gen: number): boolean {
    return gen === this.gen && this.ctxTenant === this.auth.activeTenantId();
  }

  private resolveCycle(assessmentId: string, scopeId: string): void {
    const gen = this.gen;
    this.readSub = this.nist.list().subscribe({
      next: (list) => {
        if (!this.current(gen)) return;
        const sel = resolveSelection(list, { assessmentId, cycleId: null, scopeId }, null);
        if (!sel || sel.assessment.id !== assessmentId || !sel.cycle) {
          this.loading.set(false);
          this.error.set('Avaliação ou rodada não encontrada.');
          return;
        }
        void this.router.navigate([], { relativeTo: this.route, queryParams: selectionParams(sel), queryParamsHandling: 'merge', replaceUrl: true });
      },
      error: (e: Error) => {
        if (!this.current(gen)) return;
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  private loadAssignees(tenant: string | null): void {
    if (this.assigneesTenant === tenant) return;
    this.assigneesTenant = tenant;
    this.assignees.set([]);
    this.nist.assignees().subscribe({
      next: (list) => {
        if (tenant === this.auth.activeTenantId()) this.assignees.set(list);
      },
      error: () => {
        /* sem a lista, o responsável continua podendo ser externo ou texto; a designação fica indisponível */
      },
    });
  }

  protected knownUser(id: string): boolean {
    return this.assignees().some((a) => a.userId === id);
  }

  /** O servidor já devolveu uma versão posterior àquela em que o rascunho se baseia (outra gravação no meio). */
  protected staleBase(): boolean {
    const v = this.detail()?.evaluation?.version;
    return v !== undefined && v > this.baseVersion;
  }

  /** O rascunho difere do que foi carregado (há edição não gravada). */
  protected dirty(): boolean {
    return JSON.stringify(this.draft) !== this.baseDraft;
  }

  protected gap(): number | null {
    return draftGap(this.draft);
  }

  protected problem(): string | null {
    return draftProblem(this.draft);
  }

  protected postureLabel(status: string): string {
    return status === 'Compliant' ? 'conforme' : status === 'MitigatedByThirdParty' ? 'mitigado por terceiro' : 'não conforme';
  }

  /** Recarregamento explícito: o único caminho (além de gravar) que adota a versão do servidor como base do rascunho. */
  reload(): void {
    const c = this.ctx();
    if (!c) return;
    const gen = this.gen;
    const ticket = ++this.readTicket;
    this.readSub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.saveError.set(null);
    this.conflict.set(false);
    this.roleError.set(null);
    this.roleConflict.set(false);
    this.savedNote.set(null);
    this.readSub = this.nist.subcategory(c, this.code()).subscribe({
      next: (d) => {
        if (!this.current(gen) || ticket !== this.readTicket || !this.sameContext(d)) return;
        this.accept(d);
      },
      error: (e: Error) => {
        if (!this.current(gen) || ticket !== this.readTicket) return;
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  /**
   * Procedimentos, achados ou planos mudaram: relê o detalhe SEM tocar no rascunho nem na sua versão-base (como nas
   * evidências). A avaliação exibida nunca recua para uma versão anterior à já conhecida.
   */
  protected refreshSoft(): void {
    const c = this.ctx();
    if (!c) return;
    const gen = this.gen;
    const ticket = ++this.readTicket;
    this.readSub?.unsubscribe();
    this.readSub = this.nist.subcategory(c, this.code()).subscribe({
      next: (d) => {
        if (!this.current(gen) || ticket !== this.readTicket || !this.sameContext(d)) return;
        this.acceptEvidence(d);
      },
      error: (e: Error) => {
        if (this.current(gen) && ticket === this.readTicket) this.evidenceError.set(e.message);
      },
    });
  }

  protected save(d: NistSubcategoryDetail): void {
    const c = this.ctx();
    if (!c || this.saving() || this.busy() || this.problem()) return;
    const gen = this.gen;
    this.saving.set(true);
    this.saveError.set(null);
    this.conflict.set(false);
    this.savedNote.set(null);
    this.nist.save(c, d.code, toSaveRequest(this.draft, this.baseVersion)).subscribe({
      next: (r) => {
        if (!this.current(gen)) return;
        this.saving.set(false);
        // Leitura pedida antes da gravação traria o estado anterior: descartada.
        this.readTicket++;
        this.readSub?.unsubscribe();
        this.accept(r);
        this.savedNote.set('Avaliação gravada como registro humano desta rodada.');
      },
      error: (e: NistApiError) => {
        if (!this.current(gen)) return;
        this.saving.set(false);
        this.conflict.set(e.status === 409);
        this.saveError.set(e.message);
      },
    });
  }

  protected assign(d: NistSubcategoryDetail): void {
    const c = this.ctx();
    if (!c || this.dirty()) return;
    this.roleWrite(() =>
      this.nist.assign(c, d.code, this.assessorChoice || null, this.reviewerChoice || null, d.evaluation?.version ?? 0),
      'Designação gravada.',
    );
  }

  protected review(d: NistSubcategoryDetail, decision: 'Approved' | 'ChangesRequested'): void {
    const c = this.ctx();
    if (!c || this.dirty() || !d.evaluation) return;
    this.roleWrite(
      () => this.nist.review(c, d.code, decision, this.reviewNote.trim() || null, d.evaluation!.version),
      decision === 'Approved' ? 'Versão aprovada pelo revisor.' : 'Avaliação devolvida para ajuste.',
      () => (this.reviewNote = ''),
    );
  }

  /** Designação e revisão mudam a versão da avaliação: só correm sem rascunho pendente, e adotam a resposta por inteiro. */
  private roleWrite(request: () => Observable<NistSubcategoryDetail>, note: string, done?: () => void): void {
    if (this.busy() || this.saving()) return;
    const gen = this.gen;
    this.busy.set(true);
    this.roleError.set(null);
    this.roleConflict.set(false);
    this.savedNote.set(null);
    request().subscribe({
      next: (r) => {
        if (!this.current(gen)) return;
        this.busy.set(false);
        this.readTicket++;
        this.readSub?.unsubscribe();
        done?.();
        this.accept(r);
        this.savedNote.set(note);
      },
      error: (e: NistApiError) => {
        if (!this.current(gen)) return;
        this.busy.set(false);
        this.roleConflict.set(e.status === 409);
        this.roleError.set(e.message);
      },
    });
  }

  protected suggest(d: NistSubcategoryDetail): void {
    const c = this.ctx();
    if (!c || this.suggesting()) return;
    const gen = this.gen;
    this.suggesting.set(true);
    this.aiError.set(null);
    this.suggestion.set(null);
    this.nist.suggest(c, d.code).subscribe({
      next: (s) => {
        if (!this.current(gen)) return;
        this.suggestion.set(s);
        this.suggesting.set(false);
      },
      error: (e: Error) => {
        if (!this.current(gen)) return;
        this.aiError.set(e.message);
        this.suggesting.set(false);
      },
    });
  }

  protected useSuggestion(s: NistAiSuggestion): void {
    this.draft = { ...this.draft, notApplicable: false, currentLevel: s.suggestedCurrentLevel };
    this.savedNote.set('Sugestão aplicada ao rascunho — revise e grave para registrar a avaliação.');
  }

  protected linkable(a: NistAvailableEvidence): boolean {
    return a.originKind === 'AssetInventory' || a.originKind === 'GovernanceDocument'
      || (a.originKind === 'KnightIndicator' && !a.limitation?.startsWith('Sem resultado'));
  }

  protected linkAvailable(d: NistSubcategoryDetail, a: NistAvailableEvidence): void {
    this.link(d, {
      kind: a.originKind,
      documentId: a.documentId,
      knightRunId: a.knightRunId,
      knightIndicatorId: a.knightIndicatorId,
    });
  }

  protected loadDocuments(): void {
    const gen = this.gen;
    this.governance.listDocuments().subscribe({
      next: (docs) => {
        if (!this.current(gen)) return;
        this.documents.set(docs);
        this.docChoice = docs[0]?.id ?? '';
      },
      error: () => {
        if (this.current(gen)) this.evidenceError.set('Não foi possível listar os documentos da biblioteca.');
      },
    });
  }

  protected linkDocument(d: NistSubcategoryDetail): void {
    this.link(d, { kind: 'GovernanceDocument', documentId: this.docChoice, notes: this.docNote.trim() || null }, () => (this.docNote = ''));
  }

  protected linkManual(d: NistSubcategoryDetail): void {
    const m = this.manual;
    if (m.title.trim().length === 0) return;
    this.link(
      d,
      { kind: 'Manual', title: m.title.trim(), uri: m.uri.trim() || null, notes: m.notes.trim() || null, collectedOn: m.date || null, manualType: m.type },
      () => (this.manual = { title: '', type: 'Interview', uri: '', date: '', notes: '' }),
    );
  }

  protected remove(d: NistSubcategoryDetail, evidenceId: string): void {
    const c = this.ctx();
    if (c) this.evidenceWrite(() => this.nist.removeEvidence(c, d.code, evidenceId));
  }

  private link(d: NistSubcategoryDetail, request: Parameters<NistService['linkEvidence']>[2], done?: () => void): void {
    const c = this.ctx();
    if (c) this.evidenceWrite(() => this.nist.linkEvidence(c, d.code, request), done);
  }

  /** Gravação e evidência não correm juntas: cada resposta chega sobre um detalhe que a outra não está alterando. */
  private evidenceWrite(request: () => Observable<NistSubcategoryDetail>, done?: () => void): void {
    if (this.busy() || this.saving()) return;
    const gen = this.gen;
    this.busy.set(true);
    this.evidenceError.set(null);
    request().subscribe({
      next: (r) => {
        if (!this.current(gen)) return;
        done?.();
        this.busy.set(false);
        this.acceptEvidence(r);
      },
      error: (e: Error) => {
        if (!this.current(gen)) return;
        this.busy.set(false);
        this.evidenceError.set(e.message);
      },
    });
  }

  /** A resposta é desta avaliação, rodada, escopo e subcategoria (defesa extra além da geração do contexto). */
  private sameContext(d: NistSubcategoryDetail): boolean {
    const c = this.ctx();
    return !!c && d.assessmentId === c.assessmentId && d.scopeId === c.scopeId && (!d.cycleId || d.cycleId === c.cycleId) && d.code === this.code();
  }

  /**
   * Evidência, procedimento ou achado mudou: atualiza as listas sem tocar no rascunho nem na sua versão-base. A avaliação
   * exibida nunca recua para uma versão anterior à já conhecida; se avançou (outra gravação), `staleBase` avisa e a
   * gravação dará conflito.
   */
  private acceptEvidence(d: NistSubcategoryDetail): void {
    const known = this.detail()?.evaluation ?? null;
    this.detail.set({ ...d, evaluation: newerEvaluation(known, d.evaluation) });
    this.auditTick.update((n) => n + 1);
  }

  /** Carregamento, recarregamento explícito ou gravação concluída: rascunho e base passam a ser os do servidor. */
  private accept(d: NistSubcategoryDetail): void {
    this.detail.set(d);
    this.draft = draftFrom(d.evaluation);
    this.baseDraft = JSON.stringify(this.draft);
    this.baseVersion = d.evaluation?.version ?? 0;
    this.assessorChoice = d.evaluation?.assessorUserId ?? '';
    this.reviewerChoice = d.evaluation?.reviewerUserId ?? '';
    this.loading.set(false);
    this.auditTick.update((n) => n + 1);
  }
}
