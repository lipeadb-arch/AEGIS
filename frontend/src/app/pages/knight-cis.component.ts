import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  CIS_STATUS_ORDER,
  CisFilters,
  CisRecommendation,
  CisStatus,
  EMPTY_CIS_FILTERS,
  KnightCisView,
  benchmarkLabel,
  cisBarLabel,
  cisCounts,
  cisCountsText,
  cisSegments,
  cisStatusLabel,
  countOf,
  describeCisFilters,
  filterCis,
  fixFallback,
  knightReadingLabel,
  knightStatusOf,
  profileOptions,
  ratio,
  ruleRows,
  serviceOptions,
  shortReason,
} from '../models/knight-cis.models';
import { KnightSourceType, sourceSlug } from '../models/knight.models';
import { KnightService } from '../services/knight.service';

/**
 * [AEGIS-KNIGHT-CIS-01] Página "CIS Benchmarks" do AEGIS KNIGHT. Lê a projeção do servidor sobre a composição do escopo salvo (a
 * mesma da aba "Geral") e a apresenta na ordem pedida: a avaliação de origem (data e fontes), o resumo por benchmark com
 * denominadores e barra segmentada rotulada, a reconciliação com os totais do KNIGHT e a lista pesquisável e filtrável das
 * recomendações, cada uma com "O que foi verificado", "O que encontramos", "Por que importa" e "Como corrigir". Somente leitura.
 */
@Component({
  selector: 'app-knight-cis',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    <section class="page knight-cis">
      <header class="page-head">
        <div class="titles">
          <p class="page-eyebrow"><a routerLink="/knight" class="crumb">AEGIS KNIGHT</a> · Assessment de postura</p>
          <h1>CIS Benchmarks</h1>
          <p class="page-desc">
            Resultado por recomendação dos CIS Benchmarks registrados no AEGIS, calculado a partir das regras e da coleta da avaliação
            consolidada do escopo. Cada recomendação conta uma vez; o motivo de cada resultado fica no detalhe.
          </p>
        </div>
        <div class="page-actions">
          <a class="btn ghost" routerLink="/knight">Voltar para Geral</a>
        </div>
      </header>

      @if (loading()) {
        <div class="panel"><div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a visão CIS Benchmarks…</p></div></div>
      } @else if (error()) {
        <div class="panel state err" role="alert">
          <b>{{ error() }}</b>
          <span>A visão não foi carregada agora. A avaliação do KNIGHT não foi afetada.</span>
          <button type="button" class="btn ghost" (click)="load()">Tentar novamente</button>
        </div>
      } @else {
        @if (view(); as v) {
        <p class="notice info" role="note">{{ v.disclaimer }}</p>

        <!-- 1. Avaliação de origem: data e fontes da composição. -->
        <section class="panel" aria-labelledby="cis-origin">
          <div class="hd"><h3 id="cis-origin">Avaliação de origem</h3>
            <span class="hint">Composição do escopo salvo · dados de {{ v.origin.dataRecency ? (v.origin.dataRecency | date: 'dd/MM/yyyy HH:mm') : '—' }}</span></div>
          <p class="small">{{ v.origin.label }}</p>
          <div class="table-wrap">
            <table class="data-table stack">
              <thead><tr><th>Fonte</th><th>Situação</th><th>Coleta</th><th>Catálogo</th><th>Mapeamento</th></tr></thead>
              <tbody>
                @for (s of v.origin.sources; track s.source) {
                  <tr>
                    <td data-l="Fonte">{{ s.label }}</td>
                    <td data-l="Situação"><span class="avail" [class]="s.included ? 'Included' : s.availability">{{ s.availabilityLabel }}</span></td>
                    <td data-l="Coleta">@if (s.included) { {{ s.capturedAt | date: 'dd/MM/yyyy HH:mm' }}@if (s.sourceStateLabel) { · {{ s.sourceStateLabel }} } } @else { — }</td>
                    <td data-l="Catálogo">{{ s.catalogVersion || '—' }}</td>
                    <td data-l="Mapeamento">@if (!s.included) { — } @else if (s.mappingReliable) { Vínculos revisados para esta versão } @else { <span class="warn-text">{{ s.note }}</span> }</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <p class="muted small">{{ v.mapping.note }}</p>
          @if (v.mapping.criterionNote) {
            <p class="small"><b>Critério da edição:</b> {{ v.mapping.criterionNote }}
              Fontes oficiais consultadas: {{ v.mapping.officialSources?.length ? v.mapping.officialSources!.join('; ') : 'nenhuma' }}.</p>
          }
        </section>

        <!-- 2. Resumo por benchmark: contagens com denominador e barra segmentada rotulada. -->
        <section aria-labelledby="cis-sum">
          <h2 id="cis-sum" class="sec-title">Resumo por benchmark</h2>
          <p class="muted small">
            Percentuais sobre as recomendações consideradas (“Fora do escopo” não entra). A nota KNIGHT é outra medida — pondera as regras
            do KNIGHT por severidade, não as recomendações — e continua na aba Geral.
          </p>
          <div class="bm-grid">
            @for (b of v.benchmarks; track b.key) {
              <article class="panel bm" [attr.aria-labelledby]="'bm-' + b.key">
                <div class="hd"><h3 [id]="'bm-' + b.key">{{ b.label }}</h3>
                  <a class="hint" [href]="b.url" target="_blank" rel="noopener noreferrer">Página do benchmark</a></div>
                <p class="small muted">{{ b.recommendations }} recomendações · {{ b.catalogEntries }} entradas do catálogo (variantes contam na recomendação)
                  · {{ b.counts.considered }} consideradas@if (b.counts.outOfScope) { · {{ b.counts.outOfScope }} fora do escopo }</p>
                <div class="seg-bar" role="img" [attr.aria-label]="barLabel(b.label, b.counts)">
                  @for (s of segments(b.counts); track s.status) {
                    <span class="seg" [class]="s.status" [style.width.%]="s.percent" [title]="s.label + ': ' + s.count"></span>
                  }
                </div>
                <ul class="legend">
                  @for (st of statuses; track st) {
                    <li>
                      <button type="button" class="leg" (click)="filterBy(b.key, st)" [disabled]="!countOf(b.counts, st)"
                              [attr.aria-label]="b.label + ': ' + statusLabel(st) + ', ' + countOf(b.counts, st) + ' — mostrar na lista'">
                        <span class="dot" [class]="st" aria-hidden="true"></span>{{ statusLabel(st) }} <b>{{ countOf(b.counts, st) }}</b>
                      </button>
                    </li>
                  }
                </ul>
                <dl class="kv">
                  @if (b.criterionConfirmed !== null && b.criterionConfirmed !== undefined) { <dt>Critério confirmado</dt><dd>{{ b.criterionConfirmed }} de {{ b.recommendations }} recomendações</dd> }
                  @if (b.knightCounts; as kc) {
                    <dt>Resultado do KNIGHT</dt>
                    <dd>{{ kc.compliant }} aprovadas · {{ kc.nonCompliant }} reprovadas · {{ kc.notApplicable }} não aplicáveis · {{ kc.notEvaluated }} sem conclusão <span class="muted">(não é resultado CIS)</span></dd>
                  }
                  <dt>Conformes</dt><dd>{{ ratio(b.counts.compliant, b.counts.evaluated, 'com veredito') }}</dd>
                  <dt>Com veredito</dt><dd>{{ ratio(b.counts.evaluated, b.counts.considered, 'consideradas') }}</dd>
                  <dt>Verificação manual</dt>
                  <dd>@if (manualTotal(b.manualCounts); as m) { {{ m }} (conforme {{ b.manualCounts.compliant }} · não conforme {{ b.manualCounts.nonCompliant }} · não aplicável {{ b.manualCounts.notApplicable }}) } @else { nenhuma registrada }</dd>
                  @if (b.mitigated) { <dt>Mitigadas</dt><dd>{{ b.mitigated }} reprovada(s) com controle compensatório na leitura do KNIGHT — mitigação não é conformidade</dd> }
                  <dt>Perfil</dt>
                  <dd>@if (b.profileNotRecorded || b.profileDivergent) {
                        @if (b.profileNotRecorded) { {{ b.profileNotRecorded }} sem perfil registrado na fonte de rastreio }
                        @if (b.profileDivergent) { @if (b.profileNotRecorded) { · } {{ b.profileDivergent }} com perfil divergente }
                      } @else { registrado em todas }</dd>
                </dl>
              </article>
            }
          </div>
        </section>

        <!-- 3. Reconciliação com o KNIGHT. -->
        <details class="panel recon">
          <summary><h3>Por que os totais CIS diferem dos totais do KNIGHT</h3></summary>
          @let r = v.reconciliation;
          <dl class="kv">
            <dt>KNIGHT</dt><dd>{{ r.knightEvaluated }} de {{ r.knightApplicable }} controles aplicáveis avaliados · {{ r.knightNotApplicable }} não aplicáveis · {{ r.knightControls }} no total</dd>
            <dt>Regras na composição</dt><dd>{{ r.rulesInComposition }} · {{ r.rulesLinked }} sustentam recomendações CIS · {{ r.ownChecks }} verificações próprias do KNIGHT</dd>
            <dt>Recomendações CIS</dt><dd>{{ r.evaluated }} com veredito de {{ r.considered }} consideradas · {{ r.outOfScope }} fora do escopo · {{ r.recommendations }} no total</dd>
          </dl>
          <ul class="small">@for (x of r.explanation; track $index) { <li>{{ x }}</li> }</ul>
        </details>

        <!-- 4. Lista pesquisável e filtrável. -->
        <section aria-labelledby="cis-list">
          <h2 id="cis-list" class="sec-title">Recomendações</h2>
          <div class="filter-bar" role="search" aria-label="Filtrar recomendações">
            <div class="filter-row">
              <label class="fl grow">
                <span class="filter-label">Pesquisa</span>
                <input type="search" [value]="filters().q" (input)="set('q', $any($event.target).value)" placeholder="Seção (ex.: 5.2.2.1) ou título" />
              </label>
              <label class="fl">
                <span class="filter-label">Benchmark</span>
                <select [value]="filters().benchmark" (change)="setBenchmark($any($event.target).value)">
                  <option value="">Todos</option>
                  @for (b of v.benchmarks; track b.key) { <option [value]="b.key">{{ b.label }}</option> }
                </select>
              </label>
              <label class="fl">
                <span class="filter-label">Serviço</span>
                <select [value]="filters().service" (change)="set('service', $any($event.target).value)">
                  <option value="">Todos</option>
                  @for (s of services(); track s.key) { <option [value]="s.key">{{ s.label }}</option> }
                </select>
              </label>
              <label class="fl">
                <span class="filter-label">Resultado CIS</span>
                <select [value]="filters().status" (change)="set('status', $any($event.target).value)">
                  <option value="">Todos</option>
                  @for (st of statuses; track st) { <option [value]="st">{{ statusLabel(st) }}</option> }
                </select>
              </label>
              <label class="fl">
                <span class="filter-label">Resultado do KNIGHT</span>
                <select [value]="filters().knight" (change)="set('knight', $any($event.target).value)">
                  <option value="">Todos</option>
                  @for (st of statuses; track st) { <option [value]="st">{{ knightLabel(st) }}</option> }
                </select>
              </label>
              <label class="fl">
                <span class="filter-label">Perfil</span>
                <select [value]="filters().profile" (change)="set('profile', $any($event.target).value)">
                  <option value="">Todos os perfis</option>
                  @for (p of profiles(); track p.key) { <option [value]="p.key">{{ p.label }}</option> }
                  <option value="-">Perfil não registrado na fonte de rastreio</option>
                </select>
              </label>
              <button type="button" class="ghost sm clear" (click)="clear()">Limpar filtros</button>
            </div>
            <p class="recorte" role="status" aria-live="polite">
              Mostrando <b>{{ visible().length }}</b> de {{ v.recommendations.length }} recomendações · {{ countsText() }} · Recorte: {{ recorte() }}
            </p>
          </div>

          @if (visible().length === 0) {
            <p class="muted">Nenhuma recomendação corresponde aos filtros. Use “Limpar filtros”.</p>
          } @else {
            <ul class="rec-list">
              @for (rec of visible(); track rec.id) {
                <li class="rec" [class.open]="open() === rec.id">
                  <button type="button" class="rec-head" [attr.aria-expanded]="open() === rec.id" [attr.aria-controls]="'rec-' + rec.id" (click)="toggle(rec.id)">
                    <span class="rec-main">
                      <span class="tt"><span class="sec">{{ rec.section }}</span>{{ rec.title }}</span>
                      <span class="code">{{ bench(v, rec.benchmark) }} · {{ rec.serviceLabel }}@if (rec.profileLabels.length) { · {{ rec.profileLabels.join(', ') }} }
                        · @if (rec.ruleIds.length) { regras {{ rec.ruleIds.join(', ') }} } @else { sem regra automatizada }</span>
                      @if (rec.reason) { <span class="why">{{ short(rec.reason) }}</span> }
                    </span>
                    <span class="tags">
                      <span class="st" [class]="rec.status">CIS: {{ rec.statusLabel }}@if (rec.mitigated) { (mitigada) }</span>
                      @if (rec.knight; as k) { <span class="st kread" [class]="k.status">{{ k.statusLabel }}@if (k.mitigated) { (mitigado) }</span> }
                      @if ((rec.knight?.origin ?? rec.origin) === 'Manual' || (rec.knight?.origin ?? rec.origin) === 'AutomatedAndManual') { <span class="st manual">{{ rec.knight?.originLabel ?? rec.originLabel }}</span> }
                      <span class="sev" [class]="rec.severity">{{ rec.severityLabel }}</span>
                    </span>
                  </button>
                  @if (open() === rec.id) {
                    <div class="rec-body" [id]="'rec-' + rec.id">
                      @if (rec.criterion; as cr) {
                        <div class="blk result">
                          <h4>Resultado CIS e origem</h4>
                          <dl class="kv">
                            <dt>Resultado CIS</dt><dd><span class="st" [class]="rec.status">{{ rec.statusLabel }}</span></dd>
                            @if (rec.reason) { <dt>Motivo</dt><dd>{{ rec.reason }}</dd> }
                            @if (rec.knight; as k) { <dt>Resultado do KNIGHT</dt><dd>{{ k.statusLabel }} · {{ k.originLabel }}@if (k.mitigated) { · reprovado com mitigação (não é conformidade) }</dd> }
                            <dt>Critério da edição</dt><dd>{{ cr.edition }} · seção {{ cr.section }} — {{ cr.stateLabel }}</dd>
                            <dt>Procedência</dt>
                            <dd>@if (cr.state === 'Confirmed') { {{ cr.officialSource }}@if (cr.location) { · {{ cr.location }} }@if (cr.reviewedBy) { · conferido por {{ cr.reviewedBy }}@if (cr.consultedOn) { em {{ cr.consultedOn }} } }@if (cr.reviewedCatalogVersions.length) { · catálogo {{ cr.reviewedCatalogVersions.join(', ') }} } } @else { {{ cr.trace }} }</dd>
                            @if (rec.mappingOrigin) { <dt>Origem do mapeamento</dt><dd>{{ rec.mappingOrigin }}</dd> }
                          </dl>
                          @if (cr.state !== 'Confirmed') {
                            <p class="muted small">Para concluir: conferir o critério desta seção na edição oficial do CIS e registrar a procedência. A leitura do KNIGHT abaixo (regra, resultado e evidência) não muda.</p>
                          }
                        </div>
                      }
                      <div class="blk">
                        <h4>O que foi verificado</h4>
                        <p>{{ rec.whatChecked }}</p>
                        <dl class="kv">
                          <dt>Recomendação</dt><dd>{{ bench(v, rec.benchmark) }} · seção {{ rec.section }}</dd>
                          <dt>Serviço</dt><dd>{{ rec.serviceLabel }} · {{ rec.platform }}</dd>
                          <dt>Perfil</dt><dd>{{ rec.profileLabels.length ? rec.profileLabels.join(', ') : rec.profileStateLabel }}</dd>
                          <dt>{{ rec.knight ? 'Origem da leitura do KNIGHT' : 'Origem do resultado' }}</dt><dd>{{ rec.knight?.originLabel ?? rec.originLabel }}</dd>
                        </dl>
                      </div>
                      <div class="blk">
                        <h4>O que encontramos</h4>
                        <p>{{ rec.found }}</p>
                        @if (!rec.knight && rec.reason && rec.status !== 'NotEvaluated') { <p class="muted small">{{ rec.reason }}</p> }
                      </div>
                      @if (rec.whyItMatters) { <div class="blk"><h4>Por que importa</h4><p>{{ rec.whyItMatters }}</p></div> }
                      <div class="blk">
                        <h4>Como corrigir</h4>
                        <p>{{ rec.howToFix || fixFallback(rec) }}</p>
                        @if (rec.links.length) {
                          <ul class="links">@for (l of rec.links; track l.url) { <li><a [href]="l.url" target="_blank" rel="noopener noreferrer">{{ l.title }}</a><span class="muted">&nbsp;(Microsoft Learn)</span></li> }</ul>
                        }
                        @if (knightOf(rec) === 'NonCompliant') {
                          <p class="muted small">Orientação para revisão humana: confirme o impacto antes de alterar a configuração e valide pela próxima coleta.</p>
                        }
                      </div>
                      @let rr = rows(v, rec);
                      @if (rr.length) {
                        <div class="blk">
                          <h4>Regras e recursos que sustentam o resultado</h4>
                          <div class="table-wrap">
                            <table class="data-table stack rules">
                              <thead><tr><th>Regra do KNIGHT</th><th>Resultado</th><th>Valor observado</th><th>Valor esperado</th><th>Contas e recursos</th><th>Limitação</th><th>O que prova</th></tr></thead>
                              <tbody>
                                @for (row of rr; track row.ref.indicatorId + (row.variant.variant || '')) {
                                  <tr>
                                    <td data-l="Regra do KNIGHT">
                                      @if (row.rule) {
                                        <a [routerLink]="['/knight']" [queryParams]="{ fonte: slug(row.rule.source), finding: row.ref.indicatorId }">{{ row.rule.title }}</a>
                                        <span class="meta">{{ row.ref.indicatorId }} · {{ row.rule.sourceLabel }} · vínculo {{ row.ref.matchLabel.toLowerCase() }}@if (row.variant.variant) { · variante {{ row.variant.variant }} }</span>
                                      } @else { <span class="code">{{ row.ref.indicatorId }}</span> <span class="meta">vínculo {{ row.ref.matchLabel.toLowerCase() }}</span> }
                                    </td>
                                    <td data-l="Resultado">@if (row.rule) { <span class="st" [class]="row.rule.status">{{ row.rule.statusLabel }}</span> } @else { sem resultado }</td>
                                    <td data-l="Valor observado">{{ row.rule?.evidence || '—' }}</td>
                                    <td data-l="Valor esperado">{{ row.rule?.expected || '—' }}</td>
                                    <td data-l="Contas e recursos">@if (row.rule) { {{ row.rule.affectedComposition || (row.rule.affectedCount ? row.rule.affectedCount + ' afetado(s)' : 'nenhum afetado') }}@if (row.rule.evidenceCount) { · {{ row.rule.evidenceCount }} evidência(s) de configuração } } @else { — }</td>
                                    <td data-l="Limitação">{{ limitationOf(row) }}</td>
                                    <td data-l="O que prova">{{ row.ref.proofLabel }}</td>
                                  </tr>
                                }
                              </tbody>
                            </table>
                          </div>
                          <p class="muted small">O nome da regra abre o achado no KNIGHT, com a lista de contas e recursos.</p>
                        </div>
                      }
                      @if (rec.variants.length > 1 || rec.variants[0].variant) {
                        <div class="blk">
                          <h4>Variantes do catálogo nesta recomendação</h4>
                          <ul class="small">
                            @for (vr of rec.variants; track vr.key) {
                              <li><b>{{ vr.variant || vr.title }}</b> — {{ vr.statusLabel }}@if (vr.reason) { · {{ vr.reason }} } <span class="muted">({{ vr.dispositionLabel }})</span></li>
                            }
                          </ul>
                        </div>
                      }
                      @if (rec.manual; as m) {
                        <div class="blk">
                          <h4>Verificação manual</h4>
                          <dl class="kv">
                            <dt>Resultado</dt><dd>{{ m.resultLabel }}@if (m.expired) { — vencida }@if (m.complement) { (complemento da leitura automatizada) }</dd>
                            <dt>Justificativa</dt><dd>{{ m.justification }}</dd>
                            <dt>Responsável</dt><dd>{{ m.responsibleName }}</dd>
                            <dt>Evidência</dt><dd>{{ m.evidenceReference || m.evidenceDocumentTitle || '—' }}</dd>
                            <dt>Registro</dt><dd>{{ m.recordedByName }} em {{ m.recordedAt | date: 'dd/MM/yyyy HH:mm' }}</dd>
                            <dt>Validade</dt><dd>{{ m.validUntil ? 'até ' + m.validUntil : 'sem data de revisão' }}</dd>
                          </dl>
                        </div>
                      }
                      @if (rec.limitations.length) {
                        <div class="blk"><h4>Limitações</h4><ul class="small">@for (l of rec.limitations; track $index) { <li>{{ l }}</li> }</ul></div>
                      }
                      <details class="tech">
                        <summary>Identificadores e rastreio</summary>
                        <ul>
                          <li>Recomendação: {{ rec.id }}</li>
                          <li>Entradas do catálogo: {{ keys(rec) }}</li>
                          @if (traceRules(rec); as tr) { <li>Regra de rastreio (Monkey365): {{ tr }}</li> }
                          <li>{{ rec.profileStateLabel }}</li>
                        </ul>
                      </details>
                    </div>
                  }
                </li>
              }
            </ul>
          }
        </section>

        @if (v.ownChecks.length) {
          <details class="panel">
            <summary><h3>Verificações próprias do KNIGHT, fora dos totais CIS ({{ v.ownChecks.length }})</h3></summary>
            <p class="muted small">Regras do KNIGHT, inclusive de Entra ID, sem recomendação correspondente nos CIS Benchmarks registrados. Continuam na nota e nos resultados do KNIGHT.</p>
            <div class="table-wrap">
              <table class="data-table stack">
                <thead><tr><th>Regra</th><th>Fonte</th><th>Resultado do KNIGHT</th></tr></thead>
                <tbody>
                  @for (o of v.ownChecks; track o.indicatorId) {
                    <tr>
                      <td data-l="Regra"><a [routerLink]="['/knight']" [queryParams]="{ fonte: slug(o.source), finding: o.indicatorId }">{{ o.title }}</a><span class="meta">{{ o.indicatorId }}</span></td>
                      <td data-l="Fonte">{{ o.sourceLabel }}</td>
                      <td data-l="Resultado do KNIGHT"><span class="st" [class]="o.status">{{ o.statusLabel }}</span></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </details>
        }
        <details class="panel">
          <summary><h3>Limitações da visão CIS</h3></summary>
          <ul class="small">@for (l of v.limitations; track $index) { <li>{{ l }}</li> }</ul>
        </details>
        }
      }
    </section>
  `,
  styles: [
    `
      .crumb { color: inherit; text-decoration: none; } .crumb:hover { text-decoration: underline; }
      .sec-title { margin: var(--sp-5) 0 var(--sp-2); font-size: var(--fs-panel); }
      .hd { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 4px var(--sp-3); }
      .hd h3 { margin: 0; }
      .hint { font-size: var(--fs-meta); color: var(--muted); }
      .small { font-size: var(--fs-sm); } .muted { color: var(--muted); }
      .warn-text { color: var(--amber); }
      .notice.info { margin: 0 0 var(--sp-3); padding: var(--sp-2) var(--sp-3); border-left: 3px solid var(--cyan); background: var(--tint-cyan); border-radius: 0 var(--radius-sm) var(--radius-sm) 0; font-size: var(--fs-sm); }
      .bm-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-3); }
      .bm .kv { margin-top: var(--sp-2); }
      .seg-bar { display: flex; height: 16px; border-radius: 4px; overflow: hidden; background: var(--hover); margin: var(--sp-2) 0 6px; }
      .seg-bar .seg { height: 100%; }
      .seg.Compliant, .dot.Compliant { background: var(--cyan); }
      .seg.NonCompliant, .dot.NonCompliant { background: var(--red); }
      .seg.NotEvaluated, .dot.NotEvaluated { background: var(--muted); }
      .seg.NotApplicable, .dot.NotApplicable { background: #3b4458; }
      .seg.OutOfScope, .dot.OutOfScope { background: repeating-linear-gradient(45deg, var(--line-strong), var(--line-strong) 3px, transparent 3px, transparent 6px); }
      .legend { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 2px 10px; }
      .leg { background: none; border: 0; padding: 2px 0; color: var(--text-2); font-size: var(--fs-meta); cursor: pointer; }
      .leg:hover:not(:disabled) { color: var(--cyan); text-decoration: underline; }
      .leg:disabled { cursor: default; opacity: .6; }
      .leg:focus-visible { outline: none; box-shadow: var(--focus); border-radius: 4px; }
      .dot { display: inline-block; width: 10px; height: 10px; margin-right: 5px; border-radius: 2px; vertical-align: -1px; }
      .kv { display: grid; grid-template-columns: 150px minmax(0, 1fr); gap: 4px var(--sp-3); margin: 0; font-size: var(--fs-sm); }
      .kv dt { color: var(--muted); } .kv dd { margin: 0; overflow-wrap: anywhere; }
      .recon > summary, details.panel > summary { cursor: pointer; }
      details.panel > summary h3 { display: inline; font-size: var(--fs-panel); margin: 0; }
      .fl { display: flex; flex-direction: column; gap: 4px; min-width: 140px; }
      .fl.grow { flex: 1 1 220px; }
      .fl input, .fl select { min-height: var(--control-h); padding: 0 10px; border: 1px solid var(--line-strong); border-radius: var(--radius-sm); background: var(--panel-2); color: var(--text); }
      .fl input:focus-visible, .fl select:focus-visible { outline: none; box-shadow: var(--field-focus); border-color: var(--cyan); }
      .clear { align-self: flex-end; }
      .recorte { margin: var(--sp-2) 0 0; font-size: var(--fs-sm); color: var(--text-2); }
      .rec-list { list-style: none; margin: var(--sp-3) 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
      .rec { border: 1px solid var(--line); border-radius: var(--radius); background: rgba(122, 145, 190, 0.04); }
      .rec.open { border-color: var(--cyan); }
      .rec-head { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: center; width: 100%; padding: var(--sp-3) 14px;
        background: none; border: 0; color: var(--text); text-align: left; cursor: pointer; }
      .rec-head:focus-visible { outline: none; box-shadow: var(--focus); border-radius: var(--radius); }
      .rec-main { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
      .tt { font-weight: 500; overflow-wrap: anywhere; }
      .sec { font-family: var(--mono); font-weight: 700; margin-right: 8px; color: var(--cyan); }
      .code { font-family: var(--mono); font-size: var(--fs-caps); color: var(--muted); overflow-wrap: anywhere; }
      .tags { display: flex; flex-wrap: wrap; gap: 6px; justify-content: flex-end; align-items: center; }
      .st, .sev { padding: 2px 8px; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; white-space: nowrap; }
      .st.Compliant, .st.Passed { color: var(--cyan); background: var(--tint-cyan); }
      .st.NonCompliant, .st.Exposed { color: var(--red-text); background: var(--tint-red); }
      .st.Mitigated, .st.manual { color: var(--amber); background: var(--tint-amber); }
      .st.Error { color: var(--violet-text); background: var(--tint-violet); }
      .st.NotEvaluated, .st.NotApplicable { color: var(--text-2); background: var(--hover); }
      .st.OutOfScope { color: var(--muted); border: 1px dashed var(--line-strong); }
      .st.kread { background: transparent; border: 1px dashed currentColor; }
      .why { font-size: var(--fs-caps); color: var(--amber); overflow-wrap: anywhere; }
      .sev { border: 1px solid currentColor; }
      .sev.Critical { color: var(--red-text); } .sev.High { color: #ff9a3d; } .sev.Medium { color: var(--amber); }
      .sev.Low { color: var(--cyan); } .sev.Informational { color: var(--text-2); }
      .avail { padding: 1px 8px; border-radius: var(--radius-pill); font-size: var(--fs-caps); font-weight: 600; }
      .avail.Included { color: var(--cyan); background: color-mix(in srgb, var(--cyan) 16%, transparent); }
      .avail.Available { color: var(--amber); background: color-mix(in srgb, var(--amber) 16%, transparent); }
      .avail.NotAssessed { color: var(--text-2); background: var(--hover); }
      .avail.OutOfScope { color: var(--muted); background: var(--hover); border: 1px dashed var(--line-strong); }
      .rec-body { padding: 0 14px var(--sp-3); border-top: 1px solid var(--line-2); }
      .blk h4 { margin: var(--sp-3) 0 4px; font-size: var(--fs-meta); color: var(--cyan); text-transform: uppercase; letter-spacing: var(--tracking-caps); }
      .blk p { margin: 0 0 4px; }
      .links { margin: 4px 0 0; padding-left: 18px; font-size: var(--fs-sm); }
      td .meta { display: block; font-family: var(--mono); font-size: var(--fs-caps); color: var(--muted); overflow-wrap: anywhere; }
      .rules td { font-size: var(--fs-meta); vertical-align: top; }
      details.tech { margin-top: var(--sp-3); font-size: var(--fs-caps); color: var(--muted); }
      details.tech summary { cursor: pointer; }
      details.tech ul { margin: 4px 0 0; padding-left: 16px; font-family: var(--mono); overflow-wrap: anywhere; }
      @media (max-width: 900px) { .bm-grid { grid-template-columns: minmax(0, 1fr); } .rec-head { grid-template-columns: minmax(0, 1fr); } .tags { justify-content: flex-start; } }
      @media (max-width: 600px) {
        .kv { grid-template-columns: minmax(0, 1fr); } .kv dd { margin-bottom: 4px; }
        table.stack, table.stack tbody { display: block; width: 100%; }
        table.stack thead { display: none; }
        table.stack tr { display: block; padding: 6px 0; border-bottom: 1px solid var(--line); }
        table.stack td { display: block; border: 0; padding: 3px 0; }
        table.stack td[data-l]::before { content: attr(data-l); display: block; font-size: var(--fs-caps); color: var(--muted); text-transform: uppercase; }
      }
    `,
  ],
})
export class KnightCisComponent implements OnInit {
  private readonly knight = inject(KnightService);

  readonly view = signal<KnightCisView | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly filters = signal<CisFilters>(EMPTY_CIS_FILTERS);
  readonly open = signal<string | null>(null);

  protected readonly statuses = CIS_STATUS_ORDER;
  protected readonly statusLabel = cisStatusLabel;
  protected readonly countOf = countOf;
  protected readonly ratio = ratio;
  protected readonly fixFallback = fixFallback;
  protected readonly bench = benchmarkLabel;
  protected readonly rows = ruleRows;
  protected readonly knightLabel = knightReadingLabel;
  protected readonly knightOf = knightStatusOf;
  protected readonly short = shortReason;

  readonly visible = computed(() => (this.view() ? filterCis(this.view()!, this.filters()) : []));
  readonly visibleCounts = computed(() => cisCounts(this.visible()));
  readonly recorte = computed(() => (this.view() ? describeCisFilters(this.view()!, this.filters()) : ''));
  readonly countsText = computed(() => cisCountsText(this.visibleCounts()));
  readonly profiles = computed(() => (this.view() ? profileOptions(this.view()!, this.filters().benchmark) : []));
  readonly services = computed(() => (this.view() ? serviceOptions(this.view()!) : []));

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.knight.getCis().subscribe({
      next: (v) => { this.view.set(v); this.loading.set(false); },
      error: (e: Error) => { this.error.set(e.message); this.loading.set(false); },
    });
  }

  set<K extends keyof CisFilters>(key: K, value: string): void {
    this.filters.set({ ...this.filters(), [key]: value } as CisFilters);
  }

  /** Trocar o benchmark limpa o perfil: os perfis (E3/E5, nível) são de cada benchmark. */
  setBenchmark(value: string): void {
    this.filters.set({ ...this.filters(), benchmark: value, profile: '' });
  }

  filterBy(benchmark: string, status: CisStatus): void {
    this.filters.set({ ...EMPTY_CIS_FILTERS, benchmark, status });
    this.open.set(null);
    if (typeof document !== 'undefined')
      queueMicrotask(() => document.getElementById('cis-list')?.scrollIntoView({ block: 'start' }));
  }

  clear(): void {
    this.filters.set(EMPTY_CIS_FILTERS);
  }

  toggle(id: string): void {
    this.open.set(this.open() === id ? null : id);
  }

  segments = cisSegments;
  barLabel = cisBarLabel;

  manualTotal(c: { compliant: number; nonCompliant: number; notApplicable: number }): number {
    return c.compliant + c.nonCompliant + c.notApplicable;
  }

  slug(source: string): string {
    return sourceSlug(source as KnightSourceType);
  }

  limitationOf(row: { ref: { note: string | null }; rule: { notEvaluatedReason: string | null; detailLimitation: string | null } | null }): string {
    return [row.rule?.notEvaluatedReason, row.rule?.detailLimitation, row.ref.note].filter((x) => !!x).join(' · ') || '—';
  }

  keys(rec: CisRecommendation): string {
    return rec.variants.map((v) => v.key).join(', ');
  }

  traceRules(rec: CisRecommendation): string {
    return [...new Set(rec.variants.map((v) => v.traceRule).filter((x): x is string => !!x))].join(', ');
  }
}
