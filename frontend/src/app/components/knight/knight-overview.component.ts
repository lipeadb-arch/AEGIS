import { DatePipe } from '@angular/common';
import { Component, computed, input, output, signal } from '@angular/core';
import {
  KnightAffectedSummary,
  KnightAssessment,
  KnightConsolidatedSource,
  KnightSourceState,
  KnightControlFilters,
  KnightControlRef,
  KnightIndicatorStatus,
  KnightReferenceCoverage,
  affectedKindLabel,
  consolidatedCollection,
  controlRefs,
  distributionBy,
  readableLimitation,
  isEvaluated,
  knightUnitsLine,
  overviewKpis,
  sourceStateLabel,
  sourceTypeLabel,
  statusLabel,
  threeMeasures,
} from '../../models/knight.models';
import { approvalBasis, limitationGroups, priorityViews, scoreCoverage, sourceInclusion } from '../../models/knight-summary.models';
import { KnightControlRefsComponent } from './knight-control-refs.component';

/** A mesma ordem (e as mesmas cores) dos estados nos gráficos do painel e do relatório: o que pede atenção primeiro. */
const STATUS_ORDER: KnightIndicatorStatus[] = ['Exposed', 'Mitigated', 'Error', 'NotEvaluated', 'Passed', 'NotApplicable'];

/**
 * [AEGIS-KNIGHT-MULTICLOUD-01] Aba "Visão geral" do assessment. Tudo sai da avaliação exibida — nenhuma série
 * histórica, nenhum número estimado. Score, aprovação e cobertura aparecem SEPARADOS (medem coisas diferentes e
 * não se somam), e controles, ocorrências e objetos únicos nunca se confundem. Todo gráfico é um botão que leva
 * à lista correspondente na aba de controles.
 *
 * [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Ordem de leitura: identificação (fontes incluídas × com controles avaliados),
 * indicadores (a nota ao lado da cobertura, com aviso quando parcial), riscos e ações prioritárias, resultados por
 * serviço, limitações agrupadas e, por último e recolhido, o detalhamento técnico, a cobertura do catálogo e a
 * metodologia. As leituras consultivas da página entram pelo conteúdo projetado, antes do detalhamento.
 */
@Component({
  selector: 'app-knight-overview',
  standalone: true,
  imports: [DatePipe, KnightControlRefsComponent],
  template: `
    @let a = assessment();
    @let k = kpis();
    @let sc = scoreCov();
    @let inc = inclusion();

    <!-- 1. Identificação: o que é este resultado e quanto dele foi de fato avaliado. -->
    <section class="panel ident" aria-labelledby="ov-ident">
      <div class="hd"><h3 id="ov-ident">{{ a.sourceType === 'Consolidated' ? 'Composição avaliada' : 'Fonte avaliada' }}</h3>
        <span class="hint">{{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }} · {{ stateLabel(a.sourceState) }}</span></div>
      <p class="ident-line"><b>{{ a.sourceType === 'Consolidated' ? 'Consolidado' : a.source }}</b> · {{ inc.text }}</p>
      @if (inc.withoutEvaluated.length) {
        <details class="more">
          <summary>{{ inc.withoutEvaluated.length === 1 ? '1 fonte incluída sem controle avaliado' : inc.withoutEvaluated.length + ' fontes incluídas sem controle avaliado' }}</summary>
          <ul class="plain">
            @for (s of inc.withoutEvaluated; track s.source) { <li><b>{{ s.label }}</b>@if (s.state) { · {{ s.state }} } · nenhum controle avaliado</li> }
          </ul>
          <p class="muted small">Incluir uma fonte não é avaliá-la: os controles dela ficam não avaliados e não contam como aprovados. A causa está em “Limitações relevantes”.</p>
        </details>
      }
    </section>

    <!-- 2. Indicadores principais: a nota sempre ao lado da cobertura e da base de cada número. -->
    <div class="metric-grid">
      <div class="metric" [class.is-void]="sc.score === null" [class.is-partial]="sc.score !== null && sc.partial">
        <span class="metric-label">Nota KNIGHT</span>
        <span class="metric-value" [class.is-na]="sc.score === null">{{ sc.score === null ? '—' : round(sc.score) }}@if (sc.score !== null) {<small class="of">/100</small>}</span>
        <span class="metric-unit">{{ sc.score === null ? 'Sem nota: nenhum controle avaliado.' : 'Sobre os ' + sc.evaluated + ' controles avaliados (cobertura ' + pct(sc.coverage) + ').' }}</span>
        <details class="help">
          <summary>Como é calculada</summary>
          Pondera a severidade e o resultado de cada controle avaliado (fórmula {{ a.scoreFormulaVersion }}). Controles
          não avaliados ficam fora da nota: nunca contam como zero nem como aprovados.
        </details>
      </div>
      <div class="metric" [class.is-partial]="sc.partial">
        <span class="metric-label">Cobertura da avaliação</span>
        <span class="metric-value">{{ pct(sc.coverage) }}</span>
        <span class="metric-unit">{{ sc.fraction }}</span>
        <details class="help">
          <summary>O que significa</summary>
          Controles avaliados ÷ controles aplicáveis ({{ k.notApplicable }} não aplicável(is) ficam fora da base). Mostra quanto foi
          possível verificar neste ambiente, não se ele está conforme.
        </details>
      </div>
      <div class="metric">
        <span class="metric-label">Controles reprovados</span>
        <span class="metric-value">{{ k.failed }}</span>
        <span class="metric-unit">{{ k.mitigated }} mitigado(s) com atenção · {{ unitsLine() }}</span>
        <details class="help">
          <summary>Como contar</summary>
          Reprovado: a configuração exposta foi comprovada. Mitigado: exposição com controle compensatório comprovado. Uma mesma conta,
          aplicativo, papel ou política pode aparecer em mais de um controle: cada aparição é uma ocorrência; itens distintos contam uma vez.
        </details>
      </div>
      <div class="metric">
        <span class="metric-label">Aprovação</span>
        <span class="metric-value" [class.is-na]="k.approvalPercent === null">{{ pct(k.approvalPercent) }}</span>
        <span class="metric-unit">{{ approval() }}</span>
        <details class="help">
          <summary>Base de cálculo</summary>
          Aprovados ÷ controles avaliados. Não é a nota: a nota também pesa a severidade de cada controle.
        </details>
      </div>
    </div>
    @if (sc.warning) {
      <p class="notice warn partial" role="note">@if (sc.score !== null) { <b>Leitura parcial.</b> }{{ sc.warning }}</p>
    }

    <!-- 3. Riscos e ações prioritárias: critério verificável e o que o modelo sustenta para cada controle. -->
    <section class="panel" aria-labelledby="ov-prio">
      <div class="hd"><h3 id="ov-prio">Riscos e ações prioritárias</h3>
        <span class="hint">controles reprovados · severidade, depois quantidade afetada</span></div>
      @if (priorities().length === 0) {
        <p class="muted">Nenhum controle reprovado nesta avaliação.@if (k.notEvaluated + k.errors > 0) { {{ k.notEvaluated + k.errors }} controle(s) não foram avaliados: isso não indica ausência de risco. }</p>
      } @else {
        <ol class="prio">
          @for (p of priorities(); track p.id) {
            <li>
              <div class="prio-hd">
                <span class="sev" [class]="p.severity">{{ p.severityLabel }}</span>
                <button type="button" class="linkbtn prio-title" (click)="open.emit(p.id)">{{ p.title }}</button>
              </div>
              <p class="prio-meta">{{ p.service }} · <span class="code">{{ p.id }}</span>@if (p.affected) { · {{ p.affected }} }</p>
              <dl class="prio-dl">
                @if (p.risk) { <dt>Risco</dt><dd>{{ p.risk }}</dd> }
                @if (p.impact) { <dt>Impacto</dt><dd>{{ p.impact }}</dd> }
                <dt>O que fazer</dt><dd>{{ p.guidance || 'Orientação não registrada para este controle; consulte o detalhe.' }}</dd>
              </dl>
              <button type="button" class="linkbtn small" (click)="open.emit(p.id)" [attr.aria-label]="'Abrir evidência e detalhe de ' + p.title">Ver evidência, afetados e plano →</button>
            </li>
          }
        </ol>
        @if (k.failed > priorities().length) {
          <button type="button" class="linkbtn" (click)="filter.emit({ status: 'Exposed' })">Ver os {{ k.failed }} controles reprovados</button>
        }
      }
    </section>

    <!-- 4. Resultados por serviço (barras empilhadas, contagens inteiras) e controles com achados por severidade. -->
    <div class="ov-grid">
      <section class="panel" aria-labelledby="ov-svc">
        <div class="hd"><h3 id="ov-svc">Resultados por serviço</h3>
          <span class="hint">{{ byService().length }} serviço(s) · {{ k.total }} controle(s)</span></div>
        @for (r of servicesShown(); track r.key) {
          <button type="button" class="bar" (click)="filter.emit({ service: r.key })" [attr.aria-label]="rowLabel(r.label, r.counts, r.total)">
            <span class="lbl">{{ r.label }}</span>
            <span class="track">
              @for (st of statuses; track st) {
                @if (r.counts[st]) { <span class="seg" [class]="st" [style.width.%]="share(r.counts[st], maxService())" [title]="statusLabel(st) + ': ' + r.counts[st]"></span> }
              }
            </span>
            <span class="num">{{ r.total }}</span>
          </button>
        }
        @if (byService().length > serviceLimit) {
          <button type="button" class="linkbtn" [attr.aria-expanded]="allServices()" (click)="allServices.set(!allServices())">
            {{ allServices() ? 'Mostrar só os ' + serviceLimit + ' primeiros' : 'Mostrando ' + serviceLimit + ' de ' + byService().length + ' serviços · ver todos' }}
          </button>
        }
        <div class="legend">
          @for (st of statuses; track st) { <span><span class="dot" [class]="st" aria-hidden="true"></span>{{ statusLabel(st) }}</span> }
        </div>
        <details class="values">
          <summary>Tabela de valores</summary>
          <div class="table-wrap">
            <table class="data-table">
              <caption class="sr-only">Controles por resultado em cada serviço</caption>
              <thead><tr><th scope="col">Serviço</th>@for (st of statuses; track st) { <th scope="col">{{ statusLabel(st) }}</th> }<th scope="col">Total</th></tr></thead>
              <tbody>
                @for (r of byService(); track r.key) {
                  <tr><th scope="row">{{ r.label }}</th>@for (st of statuses; track st) { <td>{{ r.counts[st] }}</td> }<td>{{ r.total }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </details>
      </section>
      <section class="panel" aria-labelledby="ov-sev">
        <div class="hd"><h3 id="ov-sev">Controles com achados por severidade</h3></div>
        <p class="muted small">{{ k.findings }} controle(s) reprovados ou mitigados. Clique para abrir a lista.</p>
        @for (s of k.findingsBySeverity; track s.key) {
          <button type="button" class="bar" (click)="filter.emit({ status: 'findings', severity: s.key })"
                  [attr.aria-label]="s.label + ': ' + s.count + ' controle(s) com achado'">
            <span class="lbl"><span class="sev" [class]="s.key">{{ s.label }}</span></span>
            <span class="track"><span class="seg" [class]="'sev-' + s.key" [style.width.%]="share(s.count, maxSeverity())"></span></span>
            <span class="num">{{ s.count }}</span>
          </button>
        }
      </section>
    </div>

    <!-- 5. Limitações relevantes: equivalentes agrupadas, cada capacidade consultável com o detalhe técnico seguro. -->
    <section class="panel" aria-labelledby="ov-lim">
      <div class="hd"><h3 id="ov-lim">Limitações relevantes</h3>
        @if (limitationCount()) { <span class="hint">{{ limitationCount() }} capacidade(s) não coletada(s) em {{ groups().length }} grupo(s)</span> }</div>
      @if (groups().length === 0) {
        <p class="muted small">Nenhuma limitação de coleta registrada: todas as capacidades desta fonte foram lidas.</p>
      } @else {
        <ul class="lim-groups">
          @for (g of groups(); track g.key) {
            <li>
              <p class="lim-hd"><span class="badge" [class.warn]="g.affectedControls.length > 0" [class.neutral]="!g.affectedControls.length">{{ g.cause }}</span>
                @if (g.kind) { <span class="muted small">{{ g.kind }}</span> }
                @if (g.sources.length) { <span class="muted small">· {{ g.sources.join(', ') }}</span> }</p>
              <p class="small">{{ g.items.length === 1 ? g.items[0].label : g.items.length + ' capacidades' }} ·
                {{ g.affectedControls.length ? g.affectedControls.length + ' controle(s) sem avaliação por isso' : 'nenhum controle ficou sem avaliação por isso' }}</p>
              <p class="small">{{ g.guidance }}</p>
              <details class="more">
                <summary>Capacidades e controles afetados</summary>
                <ul class="plain caps">
                  @for (l of g.items; track l.capability) {
                    <li><b>{{ l.label }}</b>@if (l.detail) { <span class="meta">{{ l.detail }}</span> }
                      @if (l.requirement) { <span class="meta">Requisito: {{ l.requirement }}</span> }
                      @if (l.previewApi) { <span class="meta">Leitura em versão preview: {{ l.previewApi }}</span> }
                      @if (l.technical.length) {
                        <details class="tech"><summary>Detalhe técnico</summary><ul>@for (t of l.technical; track $index) { <li>{{ t }}</li> }</ul></details>
                      }
                    </li>
                  }
                </ul>
                @if (g.affectedControls.length) { <app-knight-control-refs [refs]="refs(g.affectedControls)" (open)="open.emit($event)" /> }
              </details>
            </li>
          }
        </ul>
      }
    </section>

    <!-- Leituras consultivas da página (interpretação e risco de identidade atual), fora dos números da avaliação. -->
    <ng-content />

    <!-- 6. Detalhamento técnico, cobertura e metodologia — completo e consultável, sem ficar no caminho dos resultados. -->
    <details class="panel tech-section">
      <summary><h3>Detalhamento técnico, cobertura e metodologia</h3></summary>

      <h4>Controles por resultado</h4>
      <div class="chips">
        @for (st of statuses; track st) {
          <button type="button" class="filter-chip" (click)="filter.emit({ status: st })">
            <b>{{ countOf(st) }}</b> <span class="dot" [class]="st" aria-hidden="true"></span>{{ statusLabel(st) }}
          </button>
        }
      </div>
      <p class="muted small">
        {{ k.total }} controle(s) no escopo. Não avaliado: a evidência faltou, foi insuficiente ou inconclusiva
        (inclui dado ou permissão ausente) — o motivo aparece em cada controle; reduz a cobertura e nunca aprova.
        Erro na avaliação: a regra falhou ao avaliar um dado coletado (diferente de falha de coleta). Mitigado: exposição com controle compensatório comprovado.
      </p>

      <div class="ov-grid">
        <div>
          <h4>Resultado por domínio de segurança</h4>
          @for (r of byDomain(); track r.key) {
            <button type="button" class="bar" (click)="filter.emit({ domain: r.key })" [attr.aria-label]="rowLabel(r.label, r.counts, r.total)">
              <span class="lbl">{{ r.label }}</span>
              <span class="track">
                @for (st of statuses; track st) {
                  @if (r.counts[st]) { <span class="seg" [class]="st" [style.width.%]="share(r.counts[st], maxDomain())" [title]="statusLabel(st) + ': ' + r.counts[st]"></span> }
                }
              </span>
              <span class="num">{{ r.total }}</span>
            </button>
          }
        </div>
        @if (byPlatform().length > 1) {
          <div>
            <h4>Resultado por plataforma</h4>
            @for (r of byPlatform(); track r.key) {
              <button type="button" class="bar" (click)="filter.emit({ platform: r.key })" [attr.aria-label]="rowLabel(r.label, r.counts, r.total)">
                <span class="lbl">{{ r.label }}</span>
                <span class="track">
                  @for (st of statuses; track st) {
                    @if (r.counts[st]) { <span class="seg" [class]="st" [style.width.%]="share(r.counts[st], maxPlatform())" [title]="statusLabel(st) + ': ' + r.counts[st]"></span> }
                  }
                </span>
                <span class="num">{{ r.total }}</span>
              </button>
            }
          </div>
        }
      </div>

      <h4>Contas, aplicativos e demais itens mais recorrentes</h4>
      @if (summaryState() === 'loading') {
        <p class="muted">Carregando…</p>
      } @else if (summaryState() === 'error' || !summary()) {
        <p class="muted">O resumo de itens afetados não pôde ser carregado agora. Os controles continuam na outra aba.</p>
      } @else if (summary()!.top.length === 0) {
        <p class="muted">Nenhum item afetado preservado nesta avaliação.</p>
      } @else {
        <div class="table-wrap">
          <table class="data-table stack">
            <thead><tr><th>Conta ou recurso afetado</th><th>Tipo</th><th>Quantidade de controles</th><th>Controles relacionados</th></tr></thead>
            <tbody>
              @for (o of summary()!.top; track o.kind + o.externalId) {
                <tr>
                  <td data-l="Conta ou recurso afetado">{{ o.displayName || o.userPrincipalName || o.externalId }}
                    @if (o.displayName || o.userPrincipalName) { <span class="meta">{{ o.externalId }}</span> }</td>
                  <td data-l="Tipo">{{ o.kindLabel || kindLabel(o.kind) }}</td>
                  <td data-l="Quantidade de controles">{{ o.controlCount }}</td>
                  <td data-l="Controles relacionados"><app-knight-control-refs [refs]="refs(o.indicatorIds)" (open)="open.emit($event)" /></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (composition(); as comp) {
        <h4>Composição do resultado consolidado</h4>
        <p class="muted small">
          Cada fonte tem a própria nota, cobertura e data — não são somadas. A nota consolidada aplica a mesma fórmula sobre a união
          dos controles das fontes incluídas; não é a média. Uma fonte disponível e não incluída, ou sem avaliação concluída, aparece
          aqui sem virar aprovação.
        </p>
        <p class="small" [class.warn-text]="!collection(comp).complete"><b>Estado da coleta:</b> {{ collection(comp).text }}</p>
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Fonte</th><th>Situação</th><th>Controles avaliados</th><th>Nota</th><th>Cobertura</th><th>Catálogo</th><th>Coleta</th><th>Estado da coleta</th><th>Limitações da fonte</th></tr></thead>
            <tbody>
              @for (s of comp; track s.source) {
                <tr>
                  <td>{{ s.label }}</td>
                  <td><span class="avail" [class]="s.availabilityState">{{ availabilityLabel(s.availabilityState) }}</span></td>
                  <td>{{ s.included ? evaluatedOf(s.source) : '—' }}</td>
                  <td>{{ s.score === null ? '—' : round(s.score) }}</td>
                  <td>{{ s.coverage === null ? '—' : pct(s.coverage) }}</td>
                  <td>{{ s.catalogVersion || '—' }}</td>
                  <td>{{ s.capturedAt ? (s.capturedAt | date: 'dd/MM/yyyy HH:mm') : '—' }}</td>
                  <td>{{ s.sourceState ? stateLabel(s.sourceState) : '—' }}</td>
                  <td>
                    @if (s.collectionLimitations.length) {
                      <ul class="lim">@for (l of s.collectionLimitations; track $index) { <li>{{ readable(l) }}</li> }</ul>
                    } @else { — }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      <h4>Fonte, datas e escopo</h4>
      <p class="small">
        <b>{{ sourceTypeLabel(a.sourceType) }}</b> · {{ a.source }} · coleta de
        {{ (a.completedAt || a.startedAt) | date: 'dd/MM/yyyy HH:mm' }} · catálogo {{ a.catalogVersion }} · fórmula {{ a.scoreFormulaVersion }}.
        Serviços ainda não coletados não aparecem como avaliados — o que falta está na cobertura do catálogo abaixo.
      </p>

      <h4>Três medidas que não se somam</h4>
      @let m = measures();
      <div class="metric-grid three">
        <div class="metric">
          <span class="metric-label">Cobertura do catálogo de referência</span>
          <span class="metric-value">{{ pct(m.catalogFull) }}</span>
          <span class="metric-unit">
            integral · {{ pct(m.catalogPartial) }} parcial · {{ pct(m.catalogAnyAutomated) }} com alguma avaliação automatizada (não é cobertura completa)
          </span>
          <span class="metric-foot">Capacidade do produto, não do ambiente.</span>
        </div>
        <div class="metric">
          <span class="metric-label">Cobertura desta avaliação</span>
          <span class="metric-value">{{ pct(m.assessmentCoverage) }}</span>
          <span class="metric-unit">{{ sc.fraction }}</span>
          <span class="metric-foot">O que a coleta conseguiu avaliar neste ambiente.</span>
        </div>
        <div class="metric">
          <span class="metric-label">Aprovação</span>
          <span class="metric-value">{{ pct(m.approval) }}</span>
          <span class="metric-unit">{{ approval() }}</span>
          <span class="metric-foot">Dos controles avaliados, os que estão conformes.</span>
        </div>
      </div>
      @if (coverage(); as c) {
        <p class="muted small">
          Catálogo: o que o AEGIS consegue avaliar (propriedade do produto, {{ c.frameworks.join(' · ') }}). Sem método na API
          oficial, verificação manual e acesso que o conector não tem nunca contam como avaliados. Leituras em versão preview são
          usadas e identificadas em cada controle: {{ c.total.previewBacked ?? 0 }} das referências avaliadas dependem delas.
          Resultados de verificação manual ficam à parte, na aba Cobertura e verificação manual.
        </p>
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Plataforma</th><th>Total</th><th>Integral</th><th>Parcial</th><th>Depende de preview</th><th>Sem método na API</th><th>Manual</th><th>Outro acesso</th></tr></thead>
            <tbody>
              @for (g of c.byPlatform; track g.key) {
                <tr>
                  <td>{{ g.label }}</td><td>{{ g.total }}</td>
                  <td>{{ g.implemented }} ({{ pct(g.fullPercent) }})</td><td>{{ g.partial }} ({{ pct(g.partialPercent) }})</td>
                  <td>{{ g.previewBacked ?? 0 }}</td><td>{{ g.apiLimitation }}</td><td>{{ g.manualOnly }}</td><td>{{ g.requiresAccess }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <p class="muted small">A cobertura do catálogo de referência não pôde ser carregada agora; as outras duas medidas continuam valendo.</p>
      }
      <p class="muted small">
        O score KNIGHT resume os controles de configuração desta avaliação — desta fonte e desta coleta. A maturidade do AEGIS NIST é
        outra medida; as duas não se somam.
      </p>
    </details>
  `,
  styles: [
    `
      :host { display: flex; flex-direction: column; gap: var(--sp-4); }
      .ident .hd { margin-bottom: var(--sp-2); }
      .ident-line { margin: 0; font-size: var(--fs-sm); overflow-wrap: anywhere; }
      .ov-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-4); }
      .chips, .legend { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
      .legend { margin-top: var(--sp-2); font-size: var(--fs-meta); color: var(--text-2); }
      .small { font-size: var(--fs-meta); }
      .warn-text { color: var(--amber); }
      .partial { margin: 0; }
      .metric-value small.of { margin-left: 2px; font-size: 0.5em; font-weight: 600; color: var(--muted); letter-spacing: 0; }
      ul.lim { margin: 0; padding-left: 16px; font-size: var(--fs-meta); }
      ul.plain { margin: var(--sp-2) 0 0; padding-left: 18px; font-size: var(--fs-sm); }
      ul.plain li { margin-bottom: 4px; overflow-wrap: anywhere; }
      /* Rótulo em linha própria e sem corte: nomes de serviço longos aparecem inteiros em qualquer largura. */
      .bar { display: grid; grid-template-columns: minmax(0, 1fr) 40px; grid-template-areas: "lbl lbl" "track num"; gap: 2px var(--sp-2);
        align-items: center; width: 100%; padding: 4px 0; border: 0; background: none; color: var(--text); text-align: left; cursor: pointer; }
      .bar .lbl { grid-area: lbl; font-size: var(--fs-sm); overflow-wrap: anywhere; }
      .bar .track { grid-area: track; }
      .bar .num { grid-area: num; text-align: right; font-weight: 700; }
      .bar:hover .track { box-shadow: 0 0 0 1px var(--cyan); }
      .bar:focus-visible { outline: none; box-shadow: var(--focus); border-radius: var(--radius-xs); }
      .track { display: flex; height: 14px; border-radius: 4px; overflow: hidden; background: var(--hover); }
      .seg { height: 100%; }
      .seg.Passed, .dot.Passed { background: var(--cyan); }
      .seg.Exposed, .dot.Exposed { background: var(--red); }
      .seg.Mitigated, .dot.Mitigated { background: var(--amber); }
      .seg.Error, .dot.Error { background: var(--violet-text); }
      .seg.NotEvaluated, .dot.NotEvaluated { background: var(--muted); }
      .seg.NotApplicable, .dot.NotApplicable { background: #3b4458; }
      .seg.sev-Critical { background: var(--red); } .seg.sev-High { background: #ff9a3d; }
      .seg.sev-Medium { background: var(--amber); } .seg.sev-Low { background: var(--cyan); }
      .seg.sev-Informational { background: var(--text-2); }
      .help { font-size: var(--fs-meta); color: var(--text-2); margin-top: 4px; }
      .help summary, .more summary, .values summary { cursor: pointer; color: var(--cyan); width: fit-content; font-size: var(--fs-meta); }
      .values { margin-top: var(--sp-2); }
      .dot { display: inline-block; width: 10px; height: 10px; margin-right: 6px; border-radius: 2px; }
      .sev { padding: 1px 8px; border: 1px solid currentColor; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; white-space: nowrap; }
      .sev.Critical { color: var(--red-text); } .sev.High { color: #ff9a3d; } .sev.Medium { color: var(--amber); }
      .sev.Low { color: var(--cyan); } .sev.Informational { color: var(--text-2); }
      .avail { padding: 1px 8px; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; }
      .avail.Included { color: var(--cyan); background: color-mix(in srgb, var(--cyan) 16%, transparent); }
      .avail.Available { color: var(--amber); background: color-mix(in srgb, var(--amber) 16%, transparent); }
      .avail.NotAssessed { color: var(--text-2); background: var(--hover); }
      .prio { margin: 0; padding-left: 22px; display: flex; flex-direction: column; gap: var(--sp-3); }
      .prio li { padding-bottom: var(--sp-3); border-bottom: 1px solid var(--line-2); }
      .prio li:last-child { border-bottom: 0; padding-bottom: 0; }
      .prio-hd { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px var(--sp-2); }
      .prio-title { padding: 0; font-size: var(--fs-sm); font-weight: 600; text-align: left; overflow-wrap: anywhere; }
      .prio-meta { margin: 2px 0 var(--sp-2); font-size: var(--fs-meta); color: var(--muted); overflow-wrap: anywhere; }
      .code { font-family: var(--mono); }
      .prio-dl { display: grid; grid-template-columns: 96px minmax(0, 1fr); gap: 4px var(--sp-3); margin: 0 0 4px; font-size: var(--fs-sm); }
      .prio-dl dt { color: var(--muted); }
      .prio-dl dd { margin: 0; overflow-wrap: anywhere; }
      .lim-groups { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--sp-3); }
      .lim-groups > li { padding-bottom: var(--sp-3); border-bottom: 1px solid var(--line-2); }
      .lim-groups > li:last-child { border-bottom: 0; padding-bottom: 0; }
      .lim-groups p { margin: 0 0 4px; }
      .lim-hd { display: flex; flex-wrap: wrap; align-items: center; gap: 4px var(--sp-2); }
      .caps .meta, td .meta { display: block; font-family: var(--mono); font-size: var(--fs-caps); color: var(--muted); overflow-wrap: anywhere; }
      details.tech { margin-top: 4px; font-size: var(--fs-caps); color: var(--muted); }
      details.tech summary { cursor: pointer; }
      details.tech ul { margin: 4px 0 0; padding-left: 16px; font-family: var(--mono); overflow-wrap: anywhere; }
      .tech-section > summary { cursor: pointer; list-style-position: inside; }
      .tech-section > summary h3 { display: inline; margin: 0; font-size: var(--fs-panel); }
      .tech-section[open] > summary { margin-bottom: var(--sp-3); }
      .tech-section h4 { margin: var(--sp-4) 0 var(--sp-2); font-size: var(--fs-sm); color: var(--text-2); text-transform: uppercase; letter-spacing: var(--tracking-caps); }
      .metric-grid.three { grid-template-columns: repeat(3, minmax(0, 1fr)); }
      @media (max-width: 600px) {
        table.stack, table.stack tbody { display: block; width: 100%; }
        table.stack thead { display: none; }
        table.stack tr { display: block; padding: 6px 0; border-bottom: 1px solid var(--line); }
        table.stack td { display: block; border: 0; padding: 3px 0; }
        table.stack td[data-l]::before { content: attr(data-l); display: block; font-size: var(--fs-caps); color: var(--muted); text-transform: uppercase; }
        .prio-dl { grid-template-columns: minmax(0, 1fr); }
        .prio-dl dd { margin-bottom: 4px; }
      }
      @media (max-width: 700px) { .metric-grid.three { grid-template-columns: minmax(0, 1fr); } }
      @media (max-width: 900px) { .ov-grid { grid-template-columns: minmax(0, 1fr); } }
    `,
  ],
})
export class KnightOverviewComponent {
  readonly assessment = input.required<KnightAssessment>();
  readonly summary = input<KnightAffectedSummary | null>(null);
  readonly summaryState = input<'loading' | 'ok' | 'error'>('loading');
  /** [AEGIS-KNIGHT-COVERAGE-01] Cobertura do catálogo de referência (propriedade do produto) — nula se não carregou. */
  readonly coverage = input<KnightReferenceCoverage | null>(null);
  /** Pedido de abrir a aba de controles já com um recorte. */
  readonly filter = output<Partial<KnightControlFilters>>();
  /** Pedido de abrir UM controle. */
  readonly open = output<string>();

  protected readonly statuses = STATUS_ORDER;
  protected readonly statusLabel = statusLabel;
  protected readonly sourceTypeLabel = sourceTypeLabel;
  protected readonly kindLabel = affectedKindLabel;

  readonly kpis = computed(() => overviewKpis(this.assessment()));
  readonly scoreCov = computed(() => scoreCoverage(this.assessment()));
  readonly inclusion = computed(() => sourceInclusion(this.assessment()));
  readonly approval = computed(() => approvalBasis(this.assessment()));
  readonly groups = computed(() => limitationGroups(this.assessment()));
  readonly limitationCount = computed(() => this.groups().reduce((n, g) => n + g.items.length, 0));
  /** Serviços: os primeiros na visão inicial e todos a um clique, dizendo quantos ficaram de fora. */
  readonly serviceLimit = 8;
  readonly allServices = signal(false);
  readonly servicesShown = computed(() => (this.allServices() ? this.byService() : this.byService().slice(0, this.serviceLimit)));
  readonly byDomain = computed(() => distributionBy(this.assessment(), 'domain'));
  readonly byService = computed(() => distributionBy(this.assessment(), 'service'));
  readonly byPlatform = computed(() => distributionBy(this.assessment(), 'platform'));
  readonly maxPlatform = computed(() => Math.max(1, ...this.byPlatform().map((r) => r.total)));
  readonly measures = computed(() => threeMeasures(this.assessment(), this.coverage()));
  readonly priorities = computed(() => priorityViews(this.assessment()));
  readonly maxSeverity = computed(() => Math.max(1, ...this.kpis().findingsBySeverity.map((s) => s.count)));
  readonly maxDomain = computed(() => Math.max(1, ...this.byDomain().map((r) => r.total)));
  readonly maxService = computed(() => Math.max(1, ...this.byService().map((r) => r.total)));

  /** Três unidades que não se confundem: controles, ocorrências (objeto × controle) e objetos únicos. */
  readonly unitsLine = computed(() => knightUnitsLine(this.summary()));
  /** [AEGIS-KNIGHT-CONSOLIDATED-01] Presente só quando a avaliação exibida é o relatório consolidado. */
  readonly composition = computed(() => this.assessment().sources ?? null);

  countOf(st: KnightIndicatorStatus): number {
    return this.assessment().indicators.filter((i) => i.status === st).length;
  }

  share(n: number, max: number): number {
    return max > 0 ? (100 * n) / max : 0;
  }

  round(n: number): number {
    return Math.round(n);
  }

  /** Controles com veredito que vieram de UMA fonte da composição (cada indicador preserva a fonte real). */
  evaluatedOf(source: KnightAssessment['sourceType']): number {
    return this.assessment().indicators.filter((i) => i.sourceType === source && isEvaluated(i)).length;
  }

  pct(n: number | null): string {
    return n === null ? '—' : `${String(Math.round(n * 10) / 10).replace('.', ',')}%`;
  }

  rowLabel(label: string, counts: Record<KnightIndicatorStatus, number>, total: number): string {
    return `${label}: ${counts.Exposed} reprovado(s), ${counts.Passed} aprovado(s), ${counts.NotEvaluated + counts.Error} não avaliado(s) ou erro, de ${total}`;
  }

  /** [AEGIS-KNIGHT-PRESENTATION-01] Códigos → título desta avaliação + código, para a lista clicável. */
  refs(ids: string[]): KnightControlRef[] {
    return controlRefs(ids, this.assessment());
  }

  readable(raw: string): string {
    return readableLimitation(raw);
  }

  stateLabel(state: KnightSourceState): string {
    return sourceStateLabel(state);
  }

  collection(sources: KnightConsolidatedSource[]): { complete: boolean; incomplete: string[]; text: string } {
    return consolidatedCollection(sources);
  }

  availabilityLabel(state: 'Included' | 'Available' | 'NotAssessed'): string {
    switch (state) {
      case 'Included':
        return 'Incluída';
      case 'Available':
        return 'Disponível, não incluída';
      case 'NotAssessed':
        return 'Sem avaliação concluída';
    }
  }
}
