import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import {
  FindingReading,
  KnightAffectedObject,
  KnightAffectedObjects,
  KnightAssessment,
  KnightIndicator,
  affectedKindLabel,
  affectedLabel,
  affectedNotice,
  categoryLabel,
  findingReading,
  hasAffectedTable,
  isUnnamed,
  severityLabel,
  sourceTypeLabel,
  statusLabel,
  totalPages,
} from '../../models/knight.models';
import { KnightService } from '../../services/knight.service';

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
  imports: [DatePipe],
  template: `
    <div class="panel detail">
      <div class="d-head">
        <div>
          <h3>{{ indicator().title }}</h3>
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
      </div>

      @if (tab() === 'resumo') {
        <div class="tabpane">
          @if (reading(); as r) {
            <p class="lead">{{ r.means }}</p>
            <p class="caveat"><b>O que isso não significa:</b> {{ r.doesNotMean }}</p>
          } @else {
            <p class="lead">{{ indicator().evidence }}</p>
          }
          <div class="kv"><span class="k">Primeira ação</span><span class="v">{{ indicator().recommendation }}</span></div>
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

      @if (tab() === 'evidencia') {
        <div class="tabpane">
          <div class="kv"><span class="k">Evidência da regra</span><span class="v">{{ indicator().evidence }}</span></div>
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
      @media (max-width: 900px) { .kv { grid-template-columns: 1fr; gap: 4px; } }
    `,
  ],
})
export class KnightFindingDetailComponent {
  private readonly knight = inject(KnightService);

  /** Avaliação à qual o achado pertence — o vínculo que impede mostrar o presente como prova do passado. */
  readonly assessment = input.required<KnightAssessment>();
  readonly indicator = input.required<KnightIndicator>();
  readonly closed = output<void>();

  protected readonly categoryLabel = categoryLabel;
  protected readonly severityLabel = severityLabel;
  protected readonly statusLabel = statusLabel;
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly affectedKindLabel = affectedKindLabel;
  protected readonly affectedLabel = affectedLabel;
  protected readonly isUnnamed = isUnnamed;

  readonly tab = signal<'resumo' | 'afetados' | 'evidencia'>('resumo');
  readonly affected = signal<KnightAffectedObjects | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly search = signal('');

  private readonly PAGE_SIZE = 25;

  constructor() {
    // Trocar de achado (ou de avaliação) DESCARTA a lista carregada: exibir a lista de um achado ao lado do
    // veredito de outro seria a pior forma de mentir com dados verdadeiros.
    effect(() => {
      this.indicator();
      this.assessment();
      this.tab.set('resumo');
      this.search.set('');
      this.affected.set(null);
      this.error.set(null);
    });
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
    this.loading.set(true);
    this.error.set(null);
    this.knight
      .getAffected(this.assessment().id, this.indicator().indicatorId, page, this.PAGE_SIZE, this.search())
      .subscribe({
        next: (p) => {
          this.affected.set(p);
          this.loading.set(false);
        },
        error: (e: Error) => {
          // Preserva a página anterior: erro de rede não pode virar "nenhum afetado".
          this.error.set(e.message);
          this.loading.set(false);
        },
      });
  }

  /** Reexportado para o template — o objeto afetado é sempre lido pelo modelo, nunca formatado ad hoc. */
  protected trackObject(o: KnightAffectedObject): string {
    return o.externalId;
  }
}
