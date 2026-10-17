import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, HostListener, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ActivatedRouteSnapshot, CanDeactivateFn, Router, RouterLink } from '@angular/router';
import { Observable, Subscription, combineLatest } from 'rxjs';
import { GovernanceDocument } from '../../models/governance.models';
import { NIST_INTERVIEW_GUIDE_NOTES, hasInterviewGuides, interviewGuideFor } from '../../models/nist-interview-guide';
import { NIST_DETAIL_SECTIONS, trackingSteps } from '../../models/nist-workspace.models';
import {
  MATURITY_LEVEL_HINTS,
  MATURITY_LEVEL_LABELS,
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
  evidenceOriginLink,
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
import {
  NistAssistApplyEvent,
  SUBCATEGORY_APPLY,
  applyAssistToDraft,
  assistanceRef,
  assistedFieldsText,
  assistedFieldsToSend,
} from '../../models/nist-assist.models';
import { AuthService } from '../../services/auth.service';
import { GovernanceService } from '../../services/governance.service';
import { NistApiError, NistCtx, NistService } from '../../services/nist.service';
import { NistAssistPanelComponent } from './nist-assist-panel.component';
import { NistAuditPanelComponent } from './nist-audit-panel.component';
import { NistFindingsWorkComponent } from './nist-findings-work.component';
import { NistProceduresComponent } from './nist-procedures.component';
import { NistSubcategoryNavComponent, NistWorkspaceContext } from './nist-subcategory-nav.component';

/**
 * [AEGIS-NIST-JOURNEY-01/02] Uma subcategoria do NIST CSF 2.0 numa RODADA da avaliação: o resultado esperado, a situação
 * atual e o alvo, a justificativa, as lacunas e o risco, o responsável, os procedimentos (examinar, entrevistar, testar),
 * as evidências, os achados com o tratamento, a revisão e a trilha. Toda gravação vai ao servidor com a versão em que o
 * rascunho se baseia (conflito = 409, nada é sobrescrito, a edição fica no formulário); atualizar evidências,
 * procedimentos ou achados não muda essa base — só o recarregamento explícito. Conteúdo trazido de outra rodada ou
 * importado aparece como rascunho a confirmar, nunca como revisão humana. Respostas pedidas para outra subcategoria,
 * rodada, escopo, avaliação ou tenant são descartadas.
 *
 * [AEGIS-NIST-AI-ASSIST-01] A assistência de IA (painel próprio) só PREENCHE o rascunho por escolha da pessoa, campo a campo;
 * a gravação leva a referência da sugestão para a procedência. Sugestão desatualizada na gravação é 409 com motivo próprio:
 * a pessoa gera de novo ou declara que revisou o texto diante do estado atual. O rascunho nunca é perdido.
 *
 * [AEGIS-NIST-RECOVER-GUIDE-01] Funções com roteiro editorial (hoje RECOVER) abrem numa ÁREA DE TRABALHO: lista contextual das
 * subcategorias ao lado do detalhe, faixa de acompanhamento (Avaliação · Revisão · Tratamento, derivada dos estados
 * existentes), atalhos para as seções e, na avaliação, pergunta principal + aprofundamentos + tabela de maturidade com as
 * situações ilustrativas da subcategoria. Os campos, a gravação, a lacuna e as validações são os mesmos; trocar de
 * subcategoria com rascunho não gravado pede confirmação (nada é descartado em silêncio). As demais funções seguem como antes.
 */
@Component({
  selector: 'app-nist-subcategory',
  standalone: true,
  imports: [
    RouterLink, FormsModule, DatePipe, NgTemplateOutlet, NistProceduresComponent, NistFindingsWorkComponent, NistAuditPanelComponent,
    NistAssistPanelComponent, NistSubcategoryNavComponent,
  ],
  template: `
    <section class="page">
      @if (fn(); as f) {
        <nav class="crumbs" aria-label="Você está em">
          <a [routerLink]="['/nist']" [queryParams]="params()">AEGIS NIST</a><span aria-hidden="true">›</span>
          <a [routerLink]="['/nist', f.slug]" [queryParams]="params()">{{ nistFunctionTitle(f) }}</a><span aria-hidden="true">›</span>
          <span aria-current="page">{{ code() }}</span>
        </nav>
      }

      @if (workspace()) {
        <div class="ws-c">
          <div class="ws">
            <app-nist-subcategory-nav class="ws-nav" [fn]="fn()!" [ctx]="ctx()!" [code]="code()" [refresh]="navTick()" (context)="wsContext.set($event)" />
            <div class="ws-main-c"><div class="ws-main"><ng-container *ngTemplateOutlet="body" /></div></div>
          </div>
        </div>
      } @else {
        <ng-container *ngTemplateOutlet="body" />
      }
    </section>

    <ng-template #outcome let-d>
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
    </ng-template>

    <ng-template #body>
      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a subcategoria…</p></div></div>
      } @else if (error()) {
        <div class="panel"><div class="state error" role="alert"><p class="err">{{ error() }}</p>
          <a class="ghost" [routerLink]="['/nist']">Voltar ao AEGIS NIST</a></div></div>
      } @else if (detail()) {
        @let d = detail()!;
        @let e = d.evaluation;
        @let g = guide();
        <header class="page-head">
          <div>
            <p class="page-eyebrow">{{ nistCategoryLabel(d.categoryCode, d.categoryName) }} · {{ d.categoryCode }}</p>
            <h1>@if (g) { <span class="mono h-code">{{ d.code }}</span> }{{ d.title }}</h1>
            @if (g) {
              @let w = contextNames();
              <p class="page-meta">Avaliação <strong>{{ w?.assessment ?? '—' }}</strong> · Rodada <strong>{{ w?.cycle ?? d.cycleName ?? '—' }}</strong>
                @if (d.cycleStatus === 'Closed') { <span class="badge neutral">encerrada</span> } · Escopo <strong>{{ w?.scope ?? '—' }}</strong> ·
                <a [routerLink]="['/nist']" [queryParams]="params()">trocar</a></p>
            } @else {
              <p class="page-meta"><span class="mono">{{ d.code }}</span> ·
                <span [class]="'badge ' + stateBadgeClass(e?.state ?? 'NotEvaluated')">{{ stateLabel(e?.state ?? 'NotEvaluated') }}</span>
                @if (e?.reviewState && e!.reviewState !== 'None') { <span [class]="'badge ' + reviewBadgeClass(e!.reviewState)">{{ reviewStateLabel(e!.reviewState) }}</span> }
                · rodada <strong>{{ d.cycleName ?? '—' }}</strong>@if (d.cycleStatus === 'Closed') { <span class="badge neutral">encerrada</span> }</p>
            }
          </div>
        </header>

        @if (g) {
          <div class="track-wrap">
            <ul class="track" aria-label="Acompanhamento da subcategoria nesta rodada">
              @for (t of tracking(d); track t.key) {
                <li>
                  <button type="button" class="track-item" (click)="goTo(t.target)" [attr.aria-label]="t.label + ': ' + t.status + (t.detail ? ' — ' + t.detail : '') + '. Ir para a seção.'">
                    <span class="track-k">{{ t.label }}</span>
                    <span [class]="'badge ' + t.tone">{{ t.status }}</span>
                    @if (t.detail) { <span class="track-d">{{ t.detail }}</span> }
                  </button>
                </li>
              }
            </ul>
            <nav class="sections" aria-label="Seções da subcategoria">
              @for (s of sections; track s.id) { <button type="button" class="filter-chip" (click)="goTo(s.id)">{{ s.label }}</button> }
            </nav>
          </div>
        }

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

        @if (!g) { <ng-container *ngTemplateOutlet="outcome; context: { $implicit: d }" /> }

        <section class="panel" aria-labelledby="sc-eval">
          <div class="hd"><h3 id="sc-eval">Avaliação</h3>
            <span class="hint">escala 1–5 autoral do AEGIS · metodologia {{ d.methodologyVersion }}</span></div>
          @if (!canWrite()) { <p class="notice">Seu papel permite consultar, mas não registrar (requer Manager ou TenantAdmin).</p> }
          <form (ngSubmit)="save(d)">
            <fieldset [disabled]="!canEdit() || saving()">
              @if (g) {
                <div class="guide-grid">
                  <div class="guide-q">
                    <p class="q-k">Pergunta principal <span class="hint">· {{ g.title }}</span></p>
                    <p class="q-main">{{ g.mainQuestion }}</p>
                    <details class="deep">
                      <summary>Aprofundar a entrevista <span class="hint">({{ g.followUps.length }} perguntas)</span></summary>
                      <div class="table-wrap">
                        <table class="data-table fu">
                          <caption class="sr-only">Perguntas complementares de {{ d.code }}</caption>
                          <thead><tr><th scope="col">Pergunta complementar</th><th scope="col">O que investigar</th></tr></thead>
                          <tbody>
                            @for (f of g.followUps; track $index) { <tr><td>{{ f.question }}</td><td class="muted">{{ f.focus }}</td></tr> }
                          </tbody>
                        </table>
                      </div>
                      <p class="hint">Orientam a conversa conforme as respostas; não são campos obrigatórios nem um checklist cuja soma dá o nível.</p>
                    </details>
                    <details class="help"><summary>Atenção na interpretação</summary><p>{{ g.interpretationNote }}</p></details>
                    <details class="help"><summary>Como usar o roteiro</summary>
                      <ul>@for (n of guideNotes; track $index) { <li>{{ n }}</li> }</ul></details>
                    <p class="hint">Roteiro editorial do AEGIS, estável e sem IA: perguntas e exemplos não são evidência do ambiente nem geram nota.
                      Sugestões da IA ficam no painel “Assistência de IA”.</p>
                  </div>
                  <div class="guide-lv">
                    <label class="check"><input type="checkbox" name="na" [(ngModel)]="draft.notApplicable" /> Este resultado não se aplica ao escopo</label>
                    @if (!draft.notApplicable) {
                      <div class="table-wrap mat-wrap">
                        <table class="data-table mat">
                          <caption>Maturidade — situações ilustrativas de {{ d.code }}</caption>
                          <thead><tr><th scope="col">Nível</th><th scope="col">Situação ilustrativa</th><th scope="col" class="c">Atual</th><th scope="col" class="c">Alvo</th></tr></thead>
                          <tbody>
                            <tr [class.is-cur]="draft.currentLevel === null" [class.is-tgt]="draft.targetLevel === null">
                              <th scope="row">Sem nível
                                @if (draft.currentLevel === null) { <span class="tag">atual</span> }
                                @if (draft.targetLevel === null) { <span class="tag">alvo</span> }</th>
                              <td class="muted">Nenhum nível registrado — ausência de nível não equivale a zero.</td>
                              <td class="c" data-label="Atual"><label class="pick"><input type="radio" name="cur" [ngModel]="draft.currentLevel" (ngModelChange)="draft.currentLevel = $event" [value]="null" /><span class="sr-only">Atual: sem nível</span></label></td>
                              <td class="c" data-label="Alvo"><label class="pick"><input type="radio" name="tgt" [ngModel]="draft.targetLevel" (ngModelChange)="draft.targetLevel = $event" [value]="null" /><span class="sr-only">Alvo: sem nível</span></label></td>
                            </tr>
                            @for (l of levels; track l) {
                              <tr [class.is-cur]="draft.currentLevel === l" [class.is-tgt]="draft.targetLevel === l">
                                <th scope="row"><span class="lv-n">{{ l }}</span> {{ labels[l] }}
                                  @if (draft.currentLevel === l) { <span class="tag">atual</span> }
                                  @if (draft.targetLevel === l) { <span class="tag">alvo</span> }</th>
                                <td>{{ g.levelExamples[l - 1] }}</td>
                                <td class="c" data-label="Atual"><label class="pick"><input type="radio" name="cur" [ngModel]="draft.currentLevel" (ngModelChange)="draft.currentLevel = $event" [value]="l" /><span class="sr-only">Atual: nível {{ l }} — {{ labels[l] }}</span></label></td>
                                <td class="c" data-label="Alvo"><label class="pick"><input type="radio" name="tgt" [ngModel]="draft.targetLevel" (ngModelChange)="draft.targetLevel = $event" [value]="l" /><span class="sr-only">Alvo: nível {{ l }} — {{ labels[l] }}</span></label></td>
                              </tr>
                            }
                          </tbody>
                        </table>
                      </div>
                      <p class="hint">As situações orientam o julgamento: o nível é decisão do analista, com base nas evidências.</p>
                      <p>Lacuna: <strong>{{ gapText(gap()) }}</strong>
                        @if (gap() === null) { <span class="muted"> — registre atual e alvo para determiná-la.</span> }</p>
                    }
                  </div>
                </div>
              } @else {
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
              @if (conflict()) { <button type="button" class="ghost sm" (click)="reload()">Recarregar a versão atual</button> }
              @if (assistStale()) {
                <button type="button" class="ghost sm" (click)="save(d, true)" [disabled]="saving()">Revisei o texto diante do estado atual — gravar assim mesmo</button>
              }</div>
          }
          @if (appliedFields().length) {
            <p class="hint" role="status">Campos do rascunho vindos da sugestão: {{ appliedFields().join(', ') }} — a gravação registra a procedência.</p>
          }
          @if (savedNote()) { <p class="notice" role="status">{{ savedNote() }}</p> }
          @if (staleBase()) {
            <p class="notice warn" role="status">Outra gravação (versão {{ e?.version }}) ocorreu depois que este rascunho
              foi aberto (versão {{ baseVersion }}). Ao gravar, o AEGIS acusará conflito; a sua edição continua no formulário.
              <button type="button" class="ghost sm" (click)="reload()">Recarregar a versão atual</button></p>
          }
          @if (assistedText(e?.assistedFields); as at) { <p class="hint">Conteúdo assistido: {{ at }}</p> }
          @if (e) {
            <p class="hint">Responsável: {{ responsibleText(e.owner, e.ownerName) }} ·
              @if (e.humanConfirmed) { confirmada por {{ e.reviewedByName ?? 'autor não identificado' }}@if (e.reviewedAt) { em {{ e.reviewedAt | date: 'dd/MM/yyyy HH:mm' }} } }
              @else { ainda não confirmada nesta rodada }
              · versão {{ e.version }}</p>
          } @else { <p class="hint">Ainda sem avaliação registrada nesta rodada.</p> }
        </section>

        @if (g) { <ng-container *ngTemplateOutlet="outcome; context: { $implicit: d }" /> }

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

        <div id="assistencia" class="anchor">
          <app-nist-assist-panel kind="Subcategory" [ctx]="ctx()!" [code]="d.code" [canGenerate]="canWrite()" [canIncorporate]="canEdit()"
            [refresh]="auditTick()" [applyTargets]="assistTargets" [allowLevel]="true" [allowProcedures]="canEdit()" [linkParams]="params()"
            (apply)="applyAssist($event)" (changed)="refreshSoft()" />
        </div>

        <section class="panel" aria-labelledby="sc-ev">
          <div class="hd"><h3 id="sc-ev">Evidências</h3><span class="hint">{{ d.evidence.length }} vinculada(s) nesta rodada</span></div>
          @if (d.evidence.length === 0) { <p class="muted">Nenhuma evidência vinculada ainda.</p> }
          <ul class="ev-list">
            @for (ev of d.evidence; track ev.id) {
              <li [id]="'ev-' + ev.id" tabindex="-1">
                <div class="ev-top"><span class="badge info">{{ evidenceOriginLabel(ev.originKind) }}</span><strong>{{ ev.title }}</strong>
                  @if (ev.uri) { <a [href]="ev.uri" target="_blank" rel="noopener noreferrer">abrir link<span class="sr-only"> (nova aba)</span></a> }
                  @if (originLink(ev, params()); as ol) { <a [routerLink]="ol.commands" [queryParams]="ol.queryParams">{{ ol.label }}</a> }</div>
                <p class="muted">{{ ev.originLabel ?? '' }} · data na origem {{ ev.collectedAt | date: 'dd/MM/yyyy' : 'UTC' }} · vinculada por {{ ev.recordedByName ?? '—' }} em {{ ev.linkedAt | date: 'dd/MM/yyyy' }}</p>
                @if (ev.originScope) { <p class="muted">Escopo da coleta: {{ ev.originScope }}</p> }
                @if (ev.notes) { <p>{{ ev.notes }}</p> }
                @if (canEdit()) { <button type="button" class="ghost xs" (click)="remove(d, ev.id)" [disabled]="busy() || saving()">Retirar vínculo</button> }
              </li>
            }
          </ul>

          <h4>Evidências disponíveis na plataforma</h4>
          <p class="hint">Disponível não é vinculada: vincular registra a procedência e não aprova a subcategoria.
            <a [routerLink]="['/nist', 'gv', 'documentos']" [queryParams]="params()">Biblioteca de documentos</a>
            @if (d.code.startsWith('ID.AM')) { · <a [routerLink]="['/nist', 'id', 'ativos']" [queryParams]="params()">Inventário de ativos</a> }</p>
          @if (d.availableEvidence.length === 0) {
            <p class="muted">Nenhuma evidência técnica ou documental mapeada para esta subcategoria.</p>
          }
          <ul class="ev-list">
            @for (a of d.availableEvidence; track a.originKind + (a.knightRunId ?? '') + (a.knightIndicatorId ?? '') + (a.documentId ?? '')) {
              <li>
                <div class="ev-top"><span class="badge neutral">{{ evidenceOriginLabel(a.originKind) }}</span><strong>{{ a.title }}</strong>
                  @if (a.status) { <span class="badge violet">{{ a.status }}</span> }
                  @if (a.isDemo) { <span class="badge warn">Demonstração</span> }
                  @if (originLink(a, params()); as ol) { <a [routerLink]="ol.commands" [queryParams]="ol.queryParams">{{ ol.label }}</a> }</div>
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
              @else if (documents()!.length === 0) { <p class="muted">A biblioteca não tem documentos. <a [routerLink]="['/nist', 'gv', 'documentos']" [queryParams]="params()">Enviar documento</a></p> }
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

        <div id="historico" class="anchor">
          <app-nist-audit-panel [assessmentId]="d.assessmentId" [cycleId]="ctx()!.cycleId" [scopeId]="d.scopeId" [code]="d.code" [refresh]="auditTick()" />
        </div>
      }
    </ng-template>
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
      .ref { border-style: dashed; }
      h4 { margin: var(--sp-4) 0 var(--sp-2); font-size: var(--fs-body); }
      .anchor { display: flex; flex-direction: column; min-width: 0; }

      /* ---- [AEGIS-NIST-RECOVER-GUIDE-01] Área de trabalho: lista contextual + detalhe ---- */
      /* Contêineres de consulta em invólucros: @container não estiliza o próprio contêiner. */
      .ws-c { container: nistws / inline-size; min-width: 0; }
      .ws { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--sp-5); align-items: start; }
      .ws-main-c { container: nistmain / inline-size; min-width: 0; }
      .ws-main { display: flex; flex-direction: column; gap: var(--sp-6); min-width: 0; }
      @container nistws (min-width: 900px) {
        .ws { grid-template-columns: 272px minmax(0, 1fr); }
        .ws-nav { position: sticky; top: calc(var(--header-h) + var(--sp-4)); max-height: calc(100vh - var(--header-h) - var(--sp-8)); overflow-y: auto; }
      }
      .h-code { font-size: 0.62em; color: var(--cyan); margin-right: var(--sp-2); vertical-align: middle; }
      .track-wrap { display: flex; flex-direction: column; gap: var(--sp-3); }
      .track { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 200px), 1fr)); gap: var(--sp-2); }
      .track-item { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; width: 100%; height: 100%; padding: var(--sp-3);
        border: 1px solid var(--line); border-radius: var(--radius); background: var(--panel); color: var(--text); text-align: left; cursor: pointer; }
      .track-item:hover { border-color: var(--line-strong); background: var(--panel-2); }
      .track-item:focus-visible { outline: none; box-shadow: var(--focus); }
      .track-k { font-size: var(--fs-caps); font-weight: 600; letter-spacing: var(--tracking-caps); text-transform: uppercase; color: var(--muted); }
      .track-d { font-size: var(--fs-meta); color: var(--text-2); }
      .sections { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
      .guide-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--sp-5); margin-bottom: var(--sp-3); }
      @container nistmain (min-width: 980px) {
        .guide-grid { grid-template-columns: minmax(0, 0.9fr) minmax(0, 1.1fr); align-items: start; }
      }
      .guide-q, .guide-lv { display: flex; flex-direction: column; gap: var(--sp-3); min-width: 0; }
      .q-k { margin: 0; font-size: var(--fs-caps); font-weight: 600; letter-spacing: var(--tracking-caps); text-transform: uppercase; color: var(--cyan); }
      .q-k .hint { text-transform: none; letter-spacing: 0; font-weight: 400; }
      .q-main { margin: 0; padding: var(--sp-3) var(--sp-4); border-left: 3px solid var(--cyan); border-radius: var(--radius-sm);
        background: var(--tint-cyan); font-size: var(--fs-panel); line-height: var(--lh-relaxed); color: var(--text); }
      .deep, .help { border: 1px solid var(--line); border-radius: var(--radius-sm); padding: var(--sp-2) var(--sp-3); }
      .deep summary, .help summary { cursor: pointer; font-weight: 500; font-size: var(--fs-sm); }
      .deep summary:focus-visible, .help summary:focus-visible { outline: none; box-shadow: var(--focus); border-radius: var(--radius-xs); }
      .deep[open] summary, .help[open] summary { margin-bottom: var(--sp-2); }
      .help p, .help ul { margin: 0; font-size: var(--fs-sm); color: var(--text-2); line-height: var(--lh); }
      .help ul { padding-left: var(--sp-5); display: flex; flex-direction: column; gap: 4px; }
      .fu td { font-size: var(--fs-sm); }
      .fu th { white-space: normal; }
      .mat th, .mat td { vertical-align: top; }
      .mat thead th { white-space: normal; }
      .mat tbody th { font-size: var(--fs-sm); font-weight: 600; color: var(--text); text-transform: none; letter-spacing: 0; white-space: nowrap; }
      .mat td { font-size: var(--fs-sm); }
      .mat .c { text-align: center; width: 64px; }
      .mat tbody tr.is-cur, .mat tbody tr.is-tgt { background: var(--hover); }
      .mat tbody tr.is-cur th { box-shadow: inset 3px 0 0 var(--cyan); }
      .lv-n { display: inline-grid; place-items: center; width: 22px; height: 22px; margin-right: 4px; border: 1px solid var(--line-strong);
        border-radius: 50%; font-size: var(--fs-meta); }
      .tag { display: inline-block; margin-left: 4px; padding: 0 6px; border-radius: var(--radius-pill); border: 1px solid var(--cyan);
        font-size: 11px; font-weight: 600; color: var(--cyan); text-transform: uppercase; letter-spacing: var(--tracking-caps); }
      .pick { display: inline-grid; place-items: center; min-width: 40px; min-height: 40px; cursor: pointer; border-radius: var(--radius-sm); }
      .pick:focus-within { box-shadow: var(--focus); }
      .pick input { width: 18px; height: 18px; margin: 0; accent-color: var(--cyan); cursor: pointer; }
      fieldset:disabled .pick { cursor: not-allowed; }
      /* Detalhe estreito: cada nível vira um bloco (rótulo, situação e as duas escolhas), sem rolagem horizontal da página. */
      @container nistmain (max-width: 560px) {
        .mat thead { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0, 0, 0, 0); }
        .mat, .mat tbody, .mat tr, .mat caption { display: block; width: 100%; }
        .mat caption { padding-left: 0; }
        .mat tr { display: grid; grid-template-columns: 1fr 1fr; padding: var(--sp-2) 0; border-bottom: 1px solid var(--line-2); }
        .mat tbody th, .mat td:not(.c) { grid-column: 1 / -1; display: block; border: 0; padding: 4px var(--sp-2); white-space: normal; }
        .mat td.c { display: flex; align-items: center; justify-content: flex-start; gap: var(--sp-2); width: auto; border: 0; padding: 0 var(--sp-2); text-align: left; }
        .mat td.c::before { content: attr(data-label); font-size: var(--fs-meta); color: var(--muted); }
      }
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
  protected readonly assistTargets = SUBCATEGORY_APPLY;
  protected readonly assistedText = assistedFieldsText;
  protected readonly originLink = evidenceOriginLink;
  protected readonly tracking = trackingSteps;
  protected readonly sections = NIST_DETAIL_SECTIONS;
  protected readonly guideNotes = NIST_INTERVIEW_GUIDE_NOTES;

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
  /** [AEGIS-NIST-AI-ASSIST-01] A gravação foi recusada porque a sugestão aplicada envelheceu (409 com motivo próprio). */
  protected readonly assistStale = signal(false);
  /** Campos do rascunho aplicados de UMA sugestão (a gravação leva a referência; recarregar ou gravar limpa). */
  protected readonly appliedFields = signal<string[]>([]);
  private appliedAssistanceId: string | null = null;
  protected readonly documents = signal<GovernanceDocument[] | null>(null);
  protected readonly assignees = signal<NistAssignee[]>([]);
  protected readonly openFinding = signal<string | null>(null);
  protected readonly auditTick = signal(0);
  /** [AEGIS-NIST-RECOVER-GUIDE-01] Muda a cada gravação (avaliação, designação, revisão): a lista contextual relê a situação. */
  protected readonly navTick = signal(0);
  /** Nomes da avaliação, rodada e escopo lidos pela lista contextual (só valem para o contexto aberto). */
  protected readonly wsContext = signal<NistWorkspaceContext | null>(null);

  /** Roteiro editorial da subcategoria aberta (nulo nas funções que ainda não o têm: a tela segue como antes). */
  protected readonly guide = computed(() => interviewGuideFor(this.code()));
  /** Área de trabalho (lista contextual + detalhe): função com roteiro e contexto completo. */
  protected readonly workspace = computed(() => hasInterviewGuides(this.fn()?.code) && !!this.ctx());
  protected readonly contextNames = computed(() => {
    const w = this.wsContext();
    const c = this.ctx();
    return w && c && w.assessmentId === c.assessmentId && w.cycleId === c.cycleId && w.scopeId === c.scopeId ? w : null;
  });

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
    this.assistStale.set(false);
    this.clearApplied();
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

  /**
   * [AEGIS-NIST-RECOVER-GUIDE-01] Saída da subcategoria na área de trabalho (outra subcategoria da lista, menu, link): com
   * rascunho não gravado, pergunta antes — recusar mantém a tela e o rascunho. Fora da área de trabalho, nada muda.
   */
  canLeave(): boolean {
    if (!this.workspace() || !this.detail() || !this.dirty()) return true;
    return confirm(`A avaliação de ${this.code()} tem alterações não gravadas. Sair e descartar o rascunho?`);
  }

  /** Recarregar ou fechar a aba com rascunho não gravado na área de trabalho: o navegador pede confirmação. */
  @HostListener('window:beforeunload', ['$event'])
  protected onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.workspace() && this.detail() && this.dirty()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  /** Atalho para uma seção do detalhe: rola até ela (abaixo do cabeçalho fixo) e leva o foco junto. */
  protected goTo(id: string): void {
    const el = document.getElementById(id);
    if (!el) return;
    const header = document.querySelector('.shell-header')?.getBoundingClientRect().height ?? 60;
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    window.scrollTo({ top: el.getBoundingClientRect().top + window.scrollY - header - 16, behavior: reduce ? 'auto' : 'smooth' });
    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
    el.focus({ preventScroll: true });
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

  /**
   * Grava o rascunho com a versão-base dele. Se há campos aplicados de uma sugestão, a referência vai junto (procedência);
   * `acknowledgeStale` só depois de a pessoa declarar que revisou o texto diante do estado atual.
   */
  protected save(d: NistSubcategoryDetail, acknowledgeStale = false): void {
    const c = this.ctx();
    if (!c || this.saving() || this.busy() || this.problem()) return;
    const gen = this.gen;
    this.saving.set(true);
    this.saveError.set(null);
    this.conflict.set(false);
    this.assistStale.set(false);
    this.savedNote.set(null);
    const request = toSaveRequest(this.draft, this.baseVersion);
    if (this.appliedAssistanceId)
      request.assistance = assistanceRef(this.appliedAssistanceId, assistedFieldsToSend(this.draft, this.appliedFields()), acknowledgeStale);
    this.nist.save(c, d.code, request).subscribe({
      next: (r) => {
        if (!this.current(gen)) return;
        this.saving.set(false);
        // Leitura pedida antes da gravação traria o estado anterior: descartada.
        this.readTicket++;
        this.readSub?.unsubscribe();
        this.accept(r);
        this.navTick.update((n) => n + 1);
        this.savedNote.set('Avaliação gravada como registro humano desta rodada.');
      },
      error: (e: NistApiError) => {
        if (!this.current(gen)) return;
        this.saving.set(false);
        const staleAssist = e.status === 409 && e.reason === 'AssistanceStale';
        this.assistStale.set(staleAssist);
        this.conflict.set(e.status === 409 && !staleAssist);
        this.saveError.set(e.message);
      },
    });
  }

  /**
   * [AEGIS-NIST-AI-ASSIST-01] Aplica um campo da sugestão ao RASCUNHO — nada é gravado. Conteúdo de duas sugestões diferentes não
   * se mistura num mesmo rascunho (a procedência precisa apontar uma só): grave ou recarregue antes.
   */
  protected applyAssist(event: NistAssistApplyEvent): void {
    if (!this.canEdit()) return;
    const v = event.view;
    const c = this.ctx();
    if (!c || v.assessmentId !== c.assessmentId || v.cycleId !== c.cycleId || v.scopeId !== c.scopeId || v.subcategoryCode !== this.code()) return;
    if (this.appliedAssistanceId && this.appliedAssistanceId !== v.id) {
      this.savedNote.set(null);
      this.saveError.set('O rascunho já tem conteúdo de outra sugestão. Grave ou recarregue antes de aplicar uma sugestão diferente.');
      return;
    }
    const next = applyAssistToDraft(this.draft, v, event.field);
    if (!next) return;
    this.draft = next;
    this.appliedAssistanceId = v.id;
    this.appliedFields.set([...new Set([...this.appliedFields(), event.field])]);
    this.saveError.set(null);
    this.savedNote.set('Sugestão aplicada ao rascunho. Revise e grave: aplicar não confirma a avaliação nem conta como revisão.');
  }

  private clearApplied(): void {
    this.appliedAssistanceId = null;
    this.appliedFields.set([]);
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
        this.navTick.update((n) => n + 1);
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
    this.clearApplied();
    this.assistStale.set(false);
    this.baseDraft = JSON.stringify(this.draft);
    this.baseVersion = d.evaluation?.version ?? 0;
    this.assessorChoice = d.evaluation?.assessorUserId ?? '';
    this.reviewerChoice = d.evaluation?.reviewerUserId ?? '';
    this.loading.set(false);
    this.auditTick.update((n) => n + 1);
  }
}

/** [AEGIS-NIST-RECOVER-GUIDE-01] Rota da subcategoria: a tela decide se pode sair (rascunho não gravado na área de trabalho). */
export const nistSubcategoryLeaveGuard: CanDeactivateFn<NistSubcategoryComponent> = (c) => c.canLeave();

/**
 * Quando a rota da subcategoria roda seus guards ao ser reaproveitada: só quando muda o CONTEXTO que a tela usa — função,
 * subcategoria, avaliação, rodada ou escopo (o mesmo que faz a tela chamar `enter()` e descartar o rascunho). A política
 * padrão olha só os parâmetros de caminho; trocar rodada/avaliação/escopo (consulta) passaria sem confirmação. Fragmento
 * (#achado-…) e outros parâmetros de consulta não substituem o contexto e não acionam a pergunta.
 */
export function nistSubcategoryContextChanged(from: ActivatedRouteSnapshot, to: ActivatedRouteSnapshot): boolean {
  const key = (r: ActivatedRouteSnapshot) =>
    [
      (r.paramMap.get('fn') ?? '').toLowerCase(),
      (r.paramMap.get('code') ?? '').toUpperCase(),
      r.queryParamMap.get('avaliacao'),
      r.queryParamMap.get('rodada'),
      r.queryParamMap.get('escopo'),
    ].join('|');
  return key(from) !== key(to);
}
