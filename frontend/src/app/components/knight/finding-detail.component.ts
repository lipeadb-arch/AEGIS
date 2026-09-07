import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import {
  FindingReading,
  KnightAffectedObject,
  KnightAffectedObjects,
  KnightAssessment,
  KnightIndicator,
  affectedKindLabel,
  affectedLabel,
  affectedNotice,
  affectedRequestKey,
  categoryLabel,
  findingReading,
  findingSituation,
  findingTitle,
  isCurrentAffectedResponse,
  hasAffectedTable,
  isUnnamed,
  severityLabel,
  sourceTypeLabel,
  statusLabel,
  totalPages,
} from '../../models/knight.models';
import { KnightService } from '../../services/knight.service';
import { ActionPlan, actionResult, actionSituation } from '../../models/remediation.models';
import { KnightActionPlanComponent } from './action-plan.component';

/**
 * [AEGIS-MVP-PRODUCT-02] Detalhe de UM achado do AEGIS KNIGHT, em três abas:
 *
 *   • RESUMO — o que o achado significa, o que ele explicitamente NÃO significa e a primeira ação. É aqui que
 *     a tela impede as leituras que a revisão apontou: "78 privilegiados" não é acusação de acesso
 *     desnecessário, registro de MFA não prova imposição, atividade desconhecida não prova inatividade;
 *   • AFETADOS — os objetos que sustentam o veredito, paginados e pesquisados NO SERVIDOR (o navegador nunca
 *     baixa a lista inteira para filtrar depois). A lista pertence SEMPRE à avaliação exibida;
 *   • EVIDÊNCIA — fonte, data, critério e limitações; NIST/MITRE em nível secundário.
 *
 * Componente PRÓPRIO (não um trecho da página) porque tem estado, ciclo de carga e superfície de erro
 * próprios — e porque separa o estilo do detalhe do estilo da página, mantendo os dois dentro do orçamento
 * de CSS por componente.
 */
@Component({
  selector: 'app-knight-finding-detail',
  standalone: true,
  imports: [DatePipe, KnightActionPlanComponent],
  template: `
    <div class="panel detail">
      <div class="d-head">
        <div>
          <h3>{{ findingTitle(indicator()) }}</h3>
          <span class="d-meta">
            <span class="code">{{ indicator().indicatorId }}</span> ·
            {{ categoryLabel(indicator().category) }} ·
            <span class="sev" [class]="indicator().severity">{{ severityLabel(indicator().severity) }}</span> ·
            <span class="st" [class]="indicator().status">{{ statusLabel(indicator().status) }}</span>
          </span>
        </div>
        <button type="button" class="btn ghost" (click)="closed.emit()">Fechar</button>
      </div>

      <div class="tabs" role="tablist">
        <button type="button" role="tab" [class.on]="tab() === 'resumo'" (click)="tab.set('resumo')">Resumo</button>
        <button type="button" role="tab" [class.on]="tab() === 'afetados'" (click)="openAffected()">
          Afetados
          @if (indicator().status === 'Exposed' || indicator().status === 'Mitigated') {
            <span class="n">{{ indicator().affectedObjectCount }}</span>
          }
        </button>
        <button type="button" role="tab" [class.on]="tab() === 'evidencia'" (click)="tab.set('evidencia')">Evidência</button>
        <!-- [AEGIS-MVP-PRODUCT-03] O plano vive numa aba própria para não empurrar o Resumo para baixo; o
             Resumo mantém o ponto de ENTRADA (criar/abrir), que é onde o analista decide agir. -->
        <button type="button" role="tab" [class.on]="tab() === 'plano'" (click)="tab.set('plano')">
          Plano de ação
          @if (activePlan()) { <span class="n">1</span> }
        </button>
      </div>

      @if (tab() === 'resumo') {
        <div class="tabpane">
          <p class="lead">{{ findingSituation(indicator()) }}</p>
          @if (reading(); as r) {
            <p class="lead soft">{{ r.means }}</p>
            <p class="caveat"><b>O que isso não significa:</b> {{ r.doesNotMean }}</p>
          }
          <div class="kv"><span class="k">Primeira ação</span><span class="v">{{ indicator().recommendation }}</span></div>

          <!-- [AEGIS-MVP-PRODUCT-03] Entrada da jornada. Com ação ativa, ABRE a existente; sem ela, cria uma
               nova. Nunca oferece "criar" ao lado de uma ação que já está em curso para o mesmo achado. -->
          <div class="kv plan-entry">
            <span class="k">Plano de ação</span>
            <span class="v">
              @if (activePlan(); as ap) {
                <span>{{ ap.title }}</span>
                <span class="mono">{{ situation(ap) }} · {{ result(ap) }}</span>
                <button type="button" class="btn ghost" (click)="tab.set('plano')">Abrir plano</button>
              } @else {
                <span class="mono">Nenhuma ação ativa para este achado.</span>
                <button type="button" class="btn ghost" (click)="tab.set('plano')">Criar plano de ação</button>
              }
            </span>
          </div>

          @if (indicator().notEvaluatedReason) {
            <div class="kv">
              <span class="k">Por que não foi avaliado</span>
              <span class="v">{{ indicator().notEvaluatedReason }}</span>
            </div>
          }
        </div>
      }

      @if (tab() === 'afetados') {
        <div class="tabpane">
          @if (loading()) {
            <p class="pulse">Carregando objetos afetados…</p>
          } @else if (error()) {
            <div class="state err inline">
              <b>{{ error() }}</b>
              <button type="button" class="btn ghost" (click)="load()">Tentar novamente</button>
            </div>
            @if (affected(); as af) {
              <p class="notice warn">
                A lista abaixo e a pagina <b>carregada antes da falha</b>, com os mesmos filtros. Nao e a
                resposta do pedido que falhou.
              </p>
              <div class="stale">
                <table class="tbl">
                  <thead><tr><th>Objeto</th><th>Tipo</th></tr></thead>
                  <tbody>
                    @for (o of af.items; track o.externalId) {
                      <tr>
                        <td class="af-id"><span class="nm">{{ affectedLabel(o) }}</span></td>
                        <td><span class="kind" [class]="o.kind">{{ affectedKindLabel(o.kind) }}</span></td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          } @else {
            @if (affected(); as af) {
              @if (notice(); as msg) {
                <p class="notice" [class.warn]="af.state === 'Partial'">{{ msg }}</p>
              }
              @if (hasTable()) {
                <div class="af-tools">
                  <input
                    type="search"
                    class="af-search"
                    placeholder="Buscar por nome, conta ou identificador…"
                    [value]="search()"
                    (input)="onSearch($event)"
                    (keyup.enter)="load()" />
                  <button type="button" class="btn ghost" (click)="load()">Buscar</button>
                  <span class="af-count">{{ af.matchCount }} de {{ af.totalPreserved }} objeto(s)</span>
                </div>

                @if (af.items.length === 0) {
                  <p class="empty-line">Nenhum objeto corresponde à busca. Ajuste os termos ou limpe o filtro.</p>
                } @else {
                  <div class="tbl-wrap">
                    <table class="tbl">
                      <thead>
                        <tr><th>Objeto</th><th>Tipo</th><th>Papéis</th><th>Por que está aqui</th></tr>
                      </thead>
                      <tbody>
                        @for (o of af.items; track o.externalId) {
                          <tr>
                            <td class="af-id">
                              <span class="nm">{{ affectedLabel(o) }}</span>
                              @if (isUnnamed(o)) {
                                <span class="mono">identificador do objeto · a fonte não devolveu nome</span>
                              } @else if (o.userPrincipalName && o.displayName) {
                                <span class="mono">{{ o.userPrincipalName }}</span>
                              }
                            </td>
                            <td><span class="kind" [class]="o.kind">{{ affectedKindLabel(o.kind) }}</span></td>
                            <td class="af-roles">{{ o.roles.length ? o.roles.join(' · ') : '—' }}</td>
                            <td class="af-why">{{ o.detail || '—' }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>

                  @if (pages() > 1) {
                    <div class="pager">
                      <button type="button" class="btn ghost" [disabled]="af.page <= 1" (click)="load(af.page - 1)">Anterior</button>
                      <span class="p-of">Página {{ af.page }} de {{ pages() }}</span>
                      <button type="button" class="btn ghost" [disabled]="af.page >= pages()" (click)="load(af.page + 1)">Próxima</button>
                    </div>
                  }
                }
              }
            }
          }
        </div>
      }

      @if (tab() === 'plano') {
        <div class="tabpane">
          <app-knight-action-plan
            [indicatorId]="indicator().indicatorId"
            [affectedCount]="indicator().affectedObjectCount"
            [originRunId]="planOriginRunId()"
            [currentRunId]="assessment().id"
            [existing]="activePlan()"
            (changed)="planChanged.emit($event)" />
        </div>
      }

      @if (tab() === 'evidencia') {
        <div class="tabpane">
          <!-- Texto LITERAL gravado na avaliacao. Não é reescrito pela camada de apresentação: um snapshot
               antigo continua legível exatamente como foi registrado. -->
          <div class="kv">
            <span class="k">Texto registrado na avaliação</span>
            <span class="v literal">{{ indicator().evidence }}</span>
          </div>
          @if (reading(); as r) {
            <div class="kv"><span class="k">Critério</span><span class="v">{{ r.criterion }}</span></div>
          }
          <div class="kv">
            <span class="k">Fonte</span>
            <span class="v">{{ sourceTypeLabel(indicator().sourceType) }} · {{ assessment().source }}</span>
          </div>
          <div class="kv">
            <span class="k">Data da coleta</span>
            <span class="v">{{ indicator().collectedAt | date: 'dd/MM/yyyy HH:mm' }}</span>
          </div>
          <div class="kv">
            <span class="k">Detalhe dos afetados</span>
            <span class="v">
              @if (!indicator().hasAffectedDetail) {
                Não preservado nesta avaliação.
              } @else if (indicator().affectedDetailComplete) {
                Preservado e completo para o conjunto que produziu a contagem.
              } @else {
                Preservado, porém incompleto.
              }
              @if (indicator().affectedDetailLimitation) { {{ indicator().affectedDetailLimitation }} }
            </span>
          </div>
          <div class="kv tech">
            <span class="k">NIST / MITRE</span>
            <span class="v">
              {{ indicator().nistCodes.join(', ') || '—' }}
              @if (indicator().mitreTechniques.length) { · {{ indicator().mitreTechniques.join(' · ') }} }
            </span>
          </div>
          <div class="kv tech">
            <span class="k">Catálogo / fórmula</span>
            <span class="v">{{ assessment().catalogVersion }} · {{ assessment().scoreFormulaVersion }}</span>
          </div>
        </div>
      }
    </div>
  `,
  styles: [
    `
      .panel { border: 1px solid var(--line); border-radius: 14px; background: rgba(122, 145, 190, 0.03); padding: 18px; margin-top: 16px; }
      .d-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; }
      .d-head h3 { margin: 0 0 6px; font-size: 16px; color: var(--text); }
      .d-meta { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
      .d-meta, .af-count, .p-of, .af-id .mono { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .code { color: var(--cyan); }
      .sev, .st, .kind { font-family: var(--mono); font-size: 10px; letter-spacing: 0.06em; padding: 3px 8px; border-radius: 999px; border: 1px solid var(--line); white-space: nowrap; }
      .sev.Critical { color: #ff5c8a; border-color: rgba(255, 92, 138, 0.45); }
      .sev.High { color: var(--amber); border-color: rgba(255, 176, 32, 0.45); }
      .st.Exposed { color: #ff5c8a; }
      .st.Mitigated, .kind.ServicePrincipal { color: var(--amber); border-color: rgba(255, 176, 32, 0.4); }
      .kind, .st.NotEvaluated { color: var(--muted); }
      .kind.Guest { color: var(--cyan); border-color: rgba(38, 224, 255, 0.35); }
      .btn { cursor: pointer; font-family: var(--mono); font-size: 12px; font-weight: 600; border-radius: 11px; padding: 8px 14px; border: 1px solid var(--line); color: var(--text); background: rgba(122, 145, 190, 0.08); }
      .btn:disabled { opacity: 0.5; cursor: not-allowed; }
      .tabs { display: flex; gap: 6px; margin: 14px 0 0; border-bottom: 1px solid var(--line); }
      .tabs button { cursor: pointer; background: none; border: none; border-bottom: 2px solid transparent; color: var(--muted); font-family: var(--mono); font-size: 12px; padding: 8px 12px; }
      .tabs button.on { color: var(--cyan); border-bottom-color: var(--cyan); }
      .tabs .n { font-size: 10px; margin-left: 6px; opacity: 0.8; }
      .tabpane { padding: 16px 2px 4px; }
      .lead { margin: 0 0 10px; font-size: 13.5px; line-height: 1.6; color: var(--text); }
      .lead.soft, .kv .v.literal { color: var(--muted); }
      .stale { opacity: 0.6; }
      .caveat, .notice { margin: 0 0 14px; font-size: 12.5px; line-height: 1.6; color: var(--muted); padding: 8px 12px; border-left: 2px solid var(--line); border-radius: 0 8px 8px 0; }
      .caveat, .notice.warn { border-left-color: var(--amber); background: rgba(255, 176, 32, 0.05); }
      .caveat b { color: var(--amber); }
      .notice.warn { color: var(--text); }
      .kv { display: grid; grid-template-columns: 190px 1fr; gap: 12px; padding: 8px 0; border-top: 1px solid var(--line); }
      .kv .k { font-family: var(--mono); font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; }
      .kv .v { font-size: 13px; line-height: 1.55; color: var(--text); }
      .kv.tech .v { font-family: var(--mono); font-size: 11.5px; color: var(--muted); }
      .af-tools { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; margin-bottom: 12px; }
      .af-search { flex: 1 1 260px; min-width: 200px; background: rgba(122, 145, 190, 0.06); border: 1px solid var(--line); border-radius: 9px; padding: 8px 12px; color: var(--text); font-family: var(--sans); font-size: 13px; }
      .tbl-wrap { overflow-x: auto; }
      .tbl { width: 100%; border-collapse: collapse; font-size: 12px; }
      .tbl th { text-align: left; font-family: var(--mono); font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); padding: 8px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
      .tbl td { padding: 10px; border-bottom: 1px solid rgba(122, 145, 190, 0.12); color: var(--text); vertical-align: top; }
      .af-id .nm { display: block; font-size: 13px; }
      .af-id .mono { display: block; }
      .af-roles, .af-why, .empty-line { font-size: 12px; color: var(--muted); line-height: 1.5; }
      .pager { display: flex; gap: 12px; align-items: center; justify-content: flex-end; margin-top: 12px; }
      .state.inline { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
      .state.err b { color: #ff5c8a; }
      .pulse { font-family: var(--mono); font-size: 12px; color: var(--muted); }
      .kv.plan-entry .v { display: flex; flex-direction: column; gap: 6px; align-items: flex-start; }
      .kv.plan-entry .mono { font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      @media (max-width: 900px) { .kv { grid-template-columns: 1fr; gap: 4px; } }
    `,
  ],
})
export class KnightFindingDetailComponent {
  private readonly knight = inject(KnightService);

  /** Avaliação à qual o achado pertence — o vínculo que impede mostrar o presente como prova do passado. */
  readonly assessment = input.required<KnightAssessment>();
  readonly indicator = input.required<KnightIndicator>();
  /**
   * [AEGIS-MVP-PRODUCT-03] Ação ATIVA deste achado, quando existe. Vem da página (uma única leitura da lista
   * de ações) em vez de uma consulta por achado — evita N chamadas e mantém uma autoridade só sobre "existe
   * ação ativa?", compartilhada com a Central de Prioridades.
   */
  readonly activePlan = input<ActionPlan | null>(null);
  readonly closed = output<void>();
  /** Emite quando o plano muda, para a página recarregar o mapa de ações ativas. */
  readonly planChanged = output<ActionPlan>();

  protected readonly categoryLabel = categoryLabel;
  protected readonly severityLabel = severityLabel;
  protected readonly statusLabel = statusLabel;
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly affectedKindLabel = affectedKindLabel;
  protected readonly affectedLabel = affectedLabel;
  protected readonly isUnnamed = isUnnamed;
  protected readonly findingTitle = findingTitle;
  protected readonly findingSituation = findingSituation;
  protected readonly situation = actionSituation;
  protected readonly result = actionResult;

  /**
   * A avaliação de ORIGEM de um plano novo é a que está aberta na tela — é o resultado que o analista está
   * olhando quando decide agir. Um plano já existente conserva a sua própria origem, que não é reescrita
   * quando o analista abre uma coleta mais nova.
   */
  readonly planOriginRunId = computed(() => this.activePlan()?.originRunId ?? this.assessment().id);

  readonly tab = signal<'resumo' | 'afetados' | 'evidencia' | 'plano'>('resumo');
  readonly affected = signal<KnightAffectedObjects | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly search = signal('');

  private readonly PAGE_SIZE = 25;

  /**
   * Chave do pedido ABERTO agora (avaliação × indicador × página × busca). Uma resposta só pode escrever no
   * estado se corresponder a esta chave — limpar signals não basta, porque a requisição anterior continua
   * viva e chegaria depois preenchendo o contexto novo com objetos velhos.
   */
  private requestKey: string | null = null;
  /** Assinatura em voo: cancelada (abortando o HTTP) sempre que o contexto ou o pedido muda. */
  private inFlight: Subscription | null = null;
  /** Termo da última página efetivamente carregada, para não exibi-la como resposta de outra busca. */
  private loadedTerm: string | null = null;

  constructor() {
    // Trocar de achado (ou de avaliação) DESCARTA a lista carregada E CANCELA a leitura em voo: exibir a
    // lista de um achado ao lado do veredito de outro seria a pior forma de mentir com dados verdadeiros.
    effect(() => {
      this.indicator();
      this.assessment();
      this.cancelInFlight();
      this.tab.set('resumo');
      this.search.set('');
      this.affected.set(null);
      this.error.set(null);
      this.loading.set(false);
      this.loadedTerm = null;
    });

    // Destruir o componente também cancela: nenhuma resposta chega a um detalhe que já não existe.
    inject(DestroyRef).onDestroy(() => this.cancelInFlight());
  }

  /** Aborta a leitura em voo e invalida sua chave — o que chegar depois disso é descartado. */
  private cancelInFlight(): void {
    this.inFlight?.unsubscribe();
    this.inFlight = null;
    this.requestKey = null;
  }

  readonly reading = computed<FindingReading | null>(() => findingReading(this.indicator().indicatorId));
  readonly notice = computed(() => {
    const af = this.affected();
    return af ? affectedNotice(af) : null;
  });
  readonly hasTable = computed(() => {
    const af = this.affected();
    return af ? hasAffectedTable(af) : false;
  });
  readonly pages = computed(() => {
    const af = this.affected();
    return af ? totalPages(af) : 1;
  });

  openAffected(): void {
    this.tab.set('afetados');
    if (!this.affected() && !this.loading()) this.load(1);
  }

  onSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }

  /** Busca a página do achado aberto, sempre vinculada à avaliação exibida. Leitura pura: não coleta nada. */
  load(page = 1): void {
    const runId = this.assessment().id;
    const indicatorId = this.indicator().indicatorId;
    const term = this.search();
    const key = affectedRequestKey(runId, indicatorId, page, term);

    // Um pedido novo invalida o anterior antes de começar — inclusive quando só a busca mudou.
    this.cancelInFlight();
    this.requestKey = key;

    // A página exibida deixa de valer se a BUSCA mudou: resultado da busca anterior não é resposta da nova.
    if (this.loadedTerm !== null && this.loadedTerm !== term.trim()) {
      this.affected.set(null);
      this.loadedTerm = null;
    }

    this.loading.set(true);
    this.error.set(null);
    this.inFlight = this.knight.getAffected(runId, indicatorId, page, this.PAGE_SIZE, term).subscribe({
      next: (p) => {
        if (!isCurrentAffectedResponse(this.requestKey ?? '', key)) return;
        this.affected.set(p);
        this.loadedTerm = term.trim();
        this.loading.set(false);
        this.inFlight = null;
      },
      error: (e: Error) => {
        if (!isCurrentAffectedResponse(this.requestKey ?? '', key)) return;
        // Preserva a página anterior — quando ela é do MESMO contexto e dos mesmos filtros, e identificada
        // como tal na tela. Erro de rede não pode virar "nenhum afetado".
        this.error.set(e.message);
        this.loading.set(false);
        this.inFlight = null;
      },
    });
  }

  /** Reexportado para o template — o objeto afetado é sempre lido pelo modelo, nunca formatado ad hoc. */
  protected trackObject(o: KnightAffectedObject): string {
    return o.externalId;
  }
}
