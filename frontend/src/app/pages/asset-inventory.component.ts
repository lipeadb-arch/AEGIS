import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AssetService } from '../services/asset.service';
import {
  ASSET_CATEGORIES,
  AssetCategory,
  AssetDto,
  AssetSources,
  RISK_LEVELS,
  RiskLevel,
  categoryLabel,
  conflictObservation,
  contradictoryObservedIds,
  crossSourceTone,
  joinSourceFacts,
  recordResolutionTone,
  sourcesCell,
} from '../models/asset.models';
import { NIST_FUNCTION_DESCRIPTIONS } from '../models/nist-glossary';
import { riskColor } from '../lib/scales';
import { environment } from '../../environments/environment';
import { PostureSummaryComponent } from '../components/scoring/posture-summary.component';
import { ControlComplianceCardComponent } from '../components/scoring/control-compliance-card.component';
import { AegisPillarChecklistComponent } from '../components/scoring/aegis-pillar-checklist.component';
import { CrossSourceSituationsComponent } from '../components/cross-source/cross-source-situations.component';
import { DevicePriorityComponent } from '../components/device-priority/device-priority.component';
import {
  DevicePriorityCriticalityChange,
  applyDeclaredCriticality,
  inventoryPageStep,
} from '../models/device-priority.models';
import { AegisScoreService } from '../services/aegis-score.service';
import { ScoringService } from '../services/scoring.service';
import { FunctionPosture, functionOf } from '../models/workspace.models';
import {
  PILLARS,
  TenantControlStateDto,
  buildPillarGapAnalysis,
  buildPillarView,
} from '../models/scoring.models';

/**
 * IDENTIFY (ID.AM) — inventário tático de ativos.
 * Smart data table sobre GET /api/v1/assets: filtros NIST combinados (categoria, risco,
 * criticidade, busca), paginação, e a coluna "Risco Associado" (score/nível da IA) em destaque.
 * Sem @angular/forms: usa eventos nativos + bindings [value]/[checked].
 */
@Component({
  selector: 'app-asset-inventory',
  standalone: true,
  imports: [
    DatePipe, PostureSummaryComponent, ControlComplianceCardComponent, AegisPillarChecklistComponent,
    CrossSourceSituationsComponent,
    DevicePriorityComponent,
  ],
  template: `
    <div class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Ambiente</p>
          <h1>Inventário de ativos</h1>
          <!-- Subtítulo tático da Função Identify (mesmo padrão dos painéis de pilar / Govern) -->
          <p class="page-desc">{{ idDescription }}</p>
          <p class="page-meta">NIST CSF 2.0 · Identify (ID.AM) · ativos cadastrados e descobertos por integrações</p>
        </div>
        <div class="head-stat">
          <span class="head-stat-k">Ativos</span>
          <!-- Sem resposta do inventário (carregando ou falha) o total é desconhecido: "—", nunca 0. -->
          <span class="head-stat-v">{{ loaded() ? total() : '—' }}</span>
        </div>
      </header>

      <!-- Seção COMUM de postura + controles da Função Identify (mesmo contrato/painel das demais Funções);
           o inventário de ativos abaixo permanece como área especializada. -->
      <section class="panel">
        <div class="idw-head">
          <h3>Postura Identify <span class="code">ID</span></h3>
          <span class="hint">controles ID.* avaliados — score, cobertura, evidência e pendências</span>
        </div>

        <div class="idw-grid">
          <div class="idw-summary">
            @switch (idPostureState()) {
              @case ('loaded') { <app-posture-summary [posture]="idPosture()!" label="Identify" code="ID" /> }
              @case ('loading') { <span class="idw-pulse">Carregando o resumo de postura…</span> }
              @case ('notFound') { <span class="idw-pulse">Sem catálogo ativo para a Função Identify.</span> }
              @case ('error') {
                <div class="idw-err">
                  <span>Não foi possível carregar o resumo de postura.</span>
                  <button type="button" class="ghost sm" (click)="loadWorkspacePosture()">Tentar novamente</button>
                </div>
              }
            }
          </div>

          <div class="idw-controls">
            @switch (idControlsState()) {
              @case ('loading') { <span class="idw-pulse">Carregando os controles ID…</span> }
              @case ('error') {
                <div class="idw-err">
                  <span>Não foi possível carregar os controles ID.</span>
                  <button type="button" class="ghost sm" (click)="loadIdControls()">Tentar novamente</button>
                </div>
              }
              @case ('loaded') {
                <div class="tabbar" role="tablist">
                  <button type="button" role="tab" class="tab" [class.on]="idTab() === 'controls'"
                    [attr.aria-selected]="idTab() === 'controls'" (click)="idTab.set('controls')">Controles</button>
                  <button type="button" role="tab" class="tab blind" [class.on]="idTab() === 'blind'"
                    [attr.aria-selected]="idTab() === 'blind'" (click)="idTab.set('blind')">
                    Pontos Cegos @if (idBlindCount() > 0) { <i>{{ idBlindCount() }}</i> }
                  </button>
                </div>
                @if (idTab() === 'controls') {
                  @if (idView().controls.length > 0) {
                    <app-control-compliance-card [controls]="idView().controls" />
                  } @else {
                    <p class="idw-empty">Nenhum controle ID avaliado ainda — sem evidência para exibir (não é 0%).</p>
                  }
                } @else {
                  <app-aegis-pillar-checklist pillar="ID" />
                }
              }
            }
          </div>
        </div>
      </section>

      <!-- ---- Barra de filtros combinados ---- -->
      <section class="filter-bar" aria-label="Filtros do inventário">
        <div class="filter-row">
          <span class="filter-label">Categoria</span>
          @for (c of categories; track c.value) {
            <button
              type="button"
              class="filter-chip"
              [class.on]="selectedCategories().has(c.value)"
              [attr.aria-pressed]="selectedCategories().has(c.value)"
              (click)="toggleCategory(c.value)"
            >
              {{ c.label }}
            </button>
          }
        </div>

        <div class="filter-row">
          <label class="ctl">
            <span>Risco</span>
            <select [value]="riskLevel() ?? ''" (change)="setRisk($any($event.target).value)">
              <option value="">Todos</option>
              @for (r of riskLevels; track r) {
                <option [value]="r">{{ r }}</option>
              }
            </select>
          </label>

          <label class="ctl">
            <span>Criticidade</span>
            <select [value]="criticality() ?? ''" (change)="setCriticality($any($event.target).value)">
              <option value="">Todas</option>
              @for (n of criticalities; track n) {
                <option [value]="n">{{ n }}</option>
              }
            </select>
          </label>

          <label class="ctl chk">
            <input type="checkbox" [checked]="activeOnly()" (change)="setActiveOnly($any($event.target).checked)" />
            <span>Somente ativos</span>
          </label>

          <input
            class="search"
            type="search"
            placeholder="Buscar nome / tipo / ref…"
            aria-label="Buscar ativos por nome, tipo ou referência"
            [value]="search()"
            (input)="onSearch($any($event.target).value)"
          />

          @if (hasAnyFilter()) {
            <button type="button" class="ghost sm" (click)="clearFilters()">Limpar filtros</button>
          }
        </div>
      </section>

      <!-- ---- Estados ---- -->
      @if (loadError()) {
        <div class="notice error" role="alert">
          <b>Não foi possível carregar o inventário.</b> O serviço não respondeu agora — nenhum ativo é
          exibido, para que uma lista antiga não seja lida como o inventário atual.
        </div>
      }

      @if (inventoryNotice(); as n) {
        <div class="notice warn" role="status">{{ n }}</div>
      }

      <!-- ---- Tabela ---- -->
      <section class="panel flush">
        <table class="asset-table">
          <thead>
            <tr>
              <th>Ativo</th>
              <th>Categoria</th>
              <th class="num">Crit.</th>
              <th title="Nível de risco registrado para o ativo no AEGIS — estimativa, não probabilidade de incidente nem vulnerabilidade confirmada">Risco registrado</th>
              <th>Responsável</th>
              <th>Origem</th>
              <th title="Fontes que observaram o ativo e o vínculo entre seus registros — clique na linha para ver o detalhe">Fontes · vínculo</th>
              <th>Visto por último</th>
              <th class="num">Status</th>
            </tr>
          </thead>
          <tbody>
            @for (a of rows(); track a.id) {
              <tr class="asset-row" [class.open]="expanded() === a.id" (click)="toggleSources(a.id)"
                  [attr.aria-expanded]="expanded() === a.id" title="Ver as fontes que observaram este ativo">
                <td>
                  <div class="asset-name">{{ a.name }}</div>
                  @if (a.nameIsPlaceholder) {
                    <div class="asset-sub">nome não coletado pela fonte</div>
                  }
                  @if (a.subType) {
                    <div class="asset-sub">{{ a.subType }}</div>
                  }
                </td>
                <td><span class="cat">{{ label(a.category) }}</span></td>
                <td class="num">
                  <!-- [AEGIS-RISK-PRIORITIZATION-01] Sem declaração com autor e data (inclui o padrão 1), o valor não é criticidade confirmada. -->
                  <span class="crit crit-{{ a.criticality }}"
                    [attr.title]="a.criticalityConfirmed ? 'Criticidade declarada com proveniência' : 'Criticidade não confirmada — valor cadastrado sem proveniência'">{{ a.criticality }}</span>
                  @if (!a.criticalityConfirmed) { <div class="asset-sub">não confirmada</div> }
                </td>
                <td>
                  @if (a.riskLevel) {
                    <span
                      class="risk-pill"
                      [style.color]="riskColor(a.riskLevel)"
                      [style.borderColor]="riskColor(a.riskLevel)"
                    >
                      <span class="risk-dot" [style.background]="riskColor(a.riskLevel)"></span>
                      <b>{{ a.riskScore?.toFixed(0) }}</b> · {{ a.riskLevel }}
                    </span>
                  } @else {
                    <span class="risk-none">— não avaliado</span>
                  }
                </td>
                <td>{{ a.ownerName || '—' }}</td>
                <td><span class="src">{{ a.discoverySource }}</span></td>
                <td>
                  @let cell = sourcesCell(a.sources);
                  <div class="xs-cell">
                    <span class="xs-srcs">{{ cell.text }}</span>
                    @if (cell.badge) {
                      <span class="xs-badge tone-{{ cell.tone }}" [attr.title]="cell.title">{{ cell.badge }}</span>
                    }
                  </div>
                </td>
                <td class="dim">{{ a.lastSeenAt ? (a.lastSeenAt | date: 'dd/MM/yy HH:mm') : '—' }}</td>
                <td class="num">
                  <span class="status" [class.off]="!a.isActive">{{ a.isActive ? 'Ativo' : 'Inativo' }}</span>
                </td>
              </tr>
              @if (expanded() === a.id) {
                <tr class="detail-row">
                  <td colspan="9">
                    @switch (detailState()) {
                      @case ('loading') { <span class="idw-pulse">Carregando as fontes deste ativo…</span> }
                      @case ('error') {
                        <div class="idw-err">
                          <span>Não foi possível carregar as fontes deste ativo agora — nada é exibido, para que a falha não pareça ausência de fonte.</span>
                          <button type="button" class="ghost sm" (click)="loadSources(a.id)">Tentar novamente</button>
                        </div>
                      }
                      @case ('loaded') {
                        @let d = detail()!;
                        <div class="xs-detail">
                          <div class="xs-head">
                            <span class="xs-badge tone-{{ tone(d.crossSourceState) }}">{{ d.crossSourceLabel }}</span>
                            <p>{{ d.explanation }}</p>
                          </div>
                          @if (d.sources.length > 0) {
                            <ul class="xs-list">
                              @for (s of d.sources; track $index) {
                                <li [class.off]="!s.isActive">
                                  <div class="xs-title">
                                    <b>{{ s.sourceLabel }}</b>
                                    <span class="xs-presence">
                                      {{ s.presenceLabel }}
                                      @if (s.noLongerObservedSince) { desde {{ s.noLongerObservedSince | date: 'dd/MM/yy HH:mm' }} }
                                    </span>
                                  </div>
                                  <div class="xs-meta">
                                    Última observação pelo AEGIS: {{ s.lastObservedAt | date: 'dd/MM/yy HH:mm' }} ·
                                    Última atividade informada pela fonte:
                                    {{ s.sourceLastSeenAt ? (s.sourceLastSeenAt | date: 'dd/MM/yy HH:mm') : 'não informada' }}
                                  </div>
                                  @if (s.observedName || s.platform) {
                                    <div class="xs-meta">
                                      Na fonte: {{ s.observedName || 'nome não coletado' }}
                                      @if (s.platform) { · {{ s.platform }} }
                                    </div>
                                  }
                                  @if (s.sourceComplianceLabel || s.sourceEncryptionLabel) {
                                    <div class="xs-meta">
                                      Informação da fonte (não é veredito do AEGIS):
                                      {{ joinFacts(s.sourceComplianceLabel, s.sourceEncryptionLabel) }}
                                    </div>
                                  }
                                  <div class="xs-link">
                                    <span class="xs-badge tone-{{ recordTone(s.resolutionState) }}">{{ s.resolutionLabel }}</span>
                                    <span>{{ s.resolutionExplanation }}</span>
                                  </div>
                                  @if (s.relatedAssetName) {
                                    <div class="xs-meta">Ativo envolvido na contradição: {{ s.relatedAssetName }}</div>
                                  }
                                  @if (s.diagnostics; as diag) {
                                    <details class="xs-diag">
                                      <summary>Diagnóstico técnico</summary>
                                      <div>Id na fonte: <code>{{ diag.externalId }}</code></div>
                                      <div>Diretório de origem: <code>{{ diag.directoryNamespace ?? 'não confirmado' }}</code></div>
                                      <div>
                                        Identificador de dispositivo (Entra): <code>{{ diag.directoryDeviceId ?? '—' }}</code>
                                        · {{ s.identifierStatusLabel }}
                                      </div>
                                      @if (conflictPair(diag); as pair) {
                                        <div>
                                          Observação que contradiz o vínculo: diretório
                                          @if (pair.directoryRecorded) { <code>{{ pair.directory }}</code> } @else { {{ pair.directory }} }
                                          · identificador <code>{{ pair.identifier }}</code>
                                        </div>
                                      }
                                      @if (contradictoryIds(diag); as ids) {
                                        @if (ids.length) {
                                          <div>
                                            Identificadores contraditórios na mesma coleta:
                                            @for (v of ids; track v) { <code>{{ v }}</code> }
                                          </div>
                                        }
                                      }
                                    </details>
                                  }
                                </li>
                              }
                            </ul>
                            @if (d.truncated) {
                              <p class="xs-note">Exibindo {{ d.sources.length }} de {{ d.sourceRecords }} registros de fonte.</p>
                            }
                          }
                          <p class="xs-note">{{ d.linkMeaning }}</p>
                          <p class="xs-note">{{ d.directoryNote }}</p>
                          <p class="xs-note">{{ d.curatedNote }}</p>
                        </div>
                      }
                    }
                    <!-- [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento: carga e falha próprias; usa as situações abaixo como contexto. -->
                    <app-device-priority [assetId]="a.id" (criticalityDeclared)="onCriticalityDeclared($event)" />
                    <!-- [AEGIS-CROSS-SOURCE-01] Situações entre fontes: carga e falha próprias, independentes das fontes acima. -->
                    <app-cross-source-situations [assetId]="a.id" />
                  </td>
                </tr>
              }
            } @empty {
              <tr class="empty">
                <td colspan="9">
                  @if (loading()) {
                    Carregando inventário…
                  } @else if (loadError()) {
                    Inventário indisponível no momento — veja o aviso acima.
                  } @else if (hasAnyFilter()) {
                    Nenhum ativo corresponde aos filtros atuais.
                  } @else {
                    Nenhum ativo no inventário deste ambiente. Cadastre manualmente ou conecte uma integração.
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </section>

      <!-- ---- Paginação ---- -->
      <footer class="pager">
        <span class="range">
          @if (loaded()) {
            Página {{ page() }} de {{ totalPages() || 1 }} · {{ total() }} ativos
          } @else {
            Total indisponível
          }
        </span>
        <div class="pg-ctl">
          <label class="ctl">
            <span>Por página</span>
            <select [value]="pageSize()" (change)="setPageSize($any($event.target).value)">
              @for (s of pageSizes; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </label>
          <button type="button" class="ghost sm" (click)="goTo(page() - 1)" [disabled]="page() <= 1">‹ Anterior</button>
          <button type="button" class="ghost sm" (click)="goTo(page() + 1)" [disabled]="page() >= (totalPages() || 1)">
            Próxima ›
          </button>
        </div>
      </footer>
    </div>
  `,
  styles: [
    `
      /* Página, cabeçalho, filtros, avisos, painéis e botões: sistema visual global (styles.css). */

      /* Seção comum Identify (postura + controles) — compacta, acima do inventário. */
      .idw-head {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        justify-content: space-between;
        gap: var(--sp-1) var(--sp-3);
        margin-bottom: var(--sp-4);
      }
      .idw-head h3 {
        font-size: var(--fs-panel);
        font-weight: 600;
      }
      .idw-head .code {
        margin-left: 6px;
        font-family: var(--mono);
        font-size: var(--fs-meta);
        color: var(--cyan);
      }
      .idw-grid {
        display: grid;
        grid-template-columns: minmax(260px, 320px) 1fr;
        gap: var(--sp-5);
        align-items: start;
      }
      .idw-pulse,
      .idw-empty {
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .idw-empty {
        padding: var(--sp-4) 2px;
      }
      .idw-err {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .tabbar {
        margin-bottom: var(--sp-3);
      }
      .tabbar > button.blind.on {
        color: var(--red-text);
        border-bottom-color: var(--red);
      }
      .tabbar i {
        padding: 1px 7px;
        border: 1px solid rgba(255, 45, 111, 0.45);
        border-radius: var(--radius-pill);
        font-style: normal;
        font-size: var(--fs-caps);
        font-weight: 600;
        color: var(--red-text);
      }
      @media (max-width: 900px) {
        .idw-grid {
          grid-template-columns: 1fr;
        }
      }

      /* Rótulos dos filtros de seleção. */
      .ctl {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
      }
      .ctl.chk {
        font-size: var(--fs-sm);
        font-weight: 500;
        letter-spacing: 0;
        text-transform: none;
        color: var(--text-2);
        cursor: pointer;
      }
      .search {
        flex: 1 1 16rem;
        min-width: 0;
      }

      /* ⚠️ NÃO renomear esta classe para "grid": o seletor casaria com utilitários de grade e quebraria o layout nativo
         da tabela (thead/tbody virariam itens de grade e as colunas do cabeçalho descolariam das do corpo). */
      table.asset-table {
        width: 100%;
        border-collapse: collapse;
        font-size: var(--fs-sm);
      }
      table.asset-table thead th {
        padding: 10px var(--sp-3);
        border-bottom: 1px solid var(--line);
        text-align: left;
        font-size: var(--fs-caps);
        font-weight: 600;
        letter-spacing: var(--tracking-caps);
        text-transform: uppercase;
        color: var(--muted);
        white-space: nowrap;
      }
      /* Os thead/tbody explícitos NÃO são decoração: sem eles o seletor perde para "table.asset-table thead th" (que
         fixa text-align:left) — a encapsulação empata a contagem de classes e o desempate vai para mais elementos. */
      table.asset-table thead th.num,
      table.asset-table tbody td.num {
        text-align: center;
      }
      table.asset-table tbody td {
        padding: var(--sp-3);
        border-bottom: 1px solid var(--line-2);
        vertical-align: middle;
      }
      table.asset-table tbody tr:hover td {
        background: rgba(38, 224, 255, 0.03);
      }
      /* Nome de host inteiro numa linha: quebrar em cada hífen tornava "pc-laboratorio-06" ilegível.
         A tabela rola dentro do próprio painel quando faltar largura. */
      .asset-name {
        font-weight: 600;
        white-space: nowrap;
      }
      .asset-sub {
        margin-top: 2px;
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .cat {
        font-size: var(--fs-meta);
        font-weight: 500;
        color: var(--cyan-2);
      }
      .dim {
        font-size: var(--fs-meta);
        color: var(--muted);
        white-space: nowrap;
      }
      .src {
        font-family: var(--mono);
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .crit {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 24px;
        height: 24px;
        border-radius: 6px;
        font-size: var(--fs-sm);
        font-weight: 700;
      }
      .crit-1 {
        color: var(--cyan);
        background: rgba(38, 224, 255, 0.1);
      }
      .crit-2 {
        color: var(--amber);
        background: rgba(255, 176, 32, 0.1);
      }
      .crit-3 {
        color: #ff7a3d;
        background: rgba(255, 122, 61, 0.12);
      }
      .crit-4 {
        color: var(--red-text);
        background: rgba(255, 45, 111, 0.14);
      }
      .risk-pill {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        padding: 3px 10px;
        border: 1px solid currentColor;
        border-radius: var(--radius-pill);
        background: rgba(0, 0, 0, 0.25);
        font-size: var(--fs-meta);
        white-space: nowrap;
      }
      .risk-pill b {
        font-weight: 700;
      }
      .risk-dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        box-shadow: 0 0 10px 1px currentColor;
      }
      .risk-none {
        font-size: var(--fs-meta);
        color: var(--muted);
      }

      /* [AEGIS-ENTITY-RESOLUTION-01] Fontes · vínculo (coluna) e detalhe das fontes (linha expandida). */
      tr.asset-row {
        cursor: pointer;
      }
      table.asset-table tbody tr.asset-row.open td {
        background: rgba(38, 224, 255, 0.05);
      }
      .xs-cell {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: var(--sp-1);
      }
      .xs-srcs {
        font-size: var(--fs-meta);
      }
      .xs-badge {
        display: inline-block;
        max-width: 260px;
        padding: 2px 8px;
        border: 1px solid var(--line-strong);
        border-radius: var(--radius-pill);
        font-size: var(--fs-caps);
        font-weight: 600;
        line-height: 1.45;
        color: var(--text-2);
      }
      .xs-badge.tone-ok {
        color: var(--cyan);
        border-color: rgba(38, 224, 255, 0.45);
      }
      .xs-badge.tone-info {
        color: var(--cyan-2);
        border-color: rgba(38, 224, 255, 0.25);
      }
      .xs-badge.tone-warn {
        color: var(--amber);
        border-color: rgba(255, 176, 32, 0.5);
      }
      .xs-badge.tone-muted {
        color: var(--muted);
      }
      table.asset-table tbody tr.detail-row td {
        padding: var(--sp-4) var(--sp-5);
        background: var(--panel-2);
        cursor: default;
      }
      .xs-detail {
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
      }
      .xs-head {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 6px;
      }
      .xs-head p {
        max-width: 100ch;
        font-size: var(--fs-sm);
        line-height: 1.55;
        color: var(--text-2);
      }
      .xs-list {
        list-style: none;
        margin: 0;
        padding: 0;
        display: grid;
        gap: 10px;
      }
      .xs-list li {
        padding: var(--sp-3) 14px;
        border: 1px solid var(--line);
        border-radius: var(--radius);
        background: rgba(5, 7, 15, 0.3);
      }
      .xs-list li.off {
        opacity: 0.78;
      }
      .xs-title {
        display: flex;
        flex-wrap: wrap;
        justify-content: space-between;
        gap: 10px;
      }
      .xs-presence,
      .xs-meta {
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .xs-meta {
        margin-top: var(--sp-1);
      }
      .xs-link {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--sp-2);
        margin-top: 6px;
        font-size: var(--fs-sm);
        line-height: var(--lh);
      }
      .xs-diag {
        margin-top: 6px;
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .xs-diag summary {
        cursor: pointer;
        color: var(--text-2);
      }
      .xs-diag code {
        padding: 1px 5px;
        border-radius: var(--radius-xs);
        background: rgba(255, 255, 255, 0.06);
        color: var(--text);
      }
      .xs-note {
        max-width: 100ch;
        font-size: var(--fs-meta);
        color: var(--muted);
      }
      .status {
        font-size: var(--fs-meta);
        font-weight: 500;
        color: var(--cyan);
      }
      .status.off {
        color: var(--muted);
      }
      tr.empty td {
        padding: var(--sp-8);
        text-align: center;
        font-size: var(--fs-sm);
        color: var(--text-2);
      }
      .pg-ctl {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
      }
    `,
  ],
})
export class AssetInventoryComponent implements OnInit {
  private readonly svc = inject(AssetService);
  private readonly scoreSvc = inject(AegisScoreService);
  private readonly scoring = inject(ScoringService);

  // ---- Dados ----
  rows = signal<AssetDto[]>([]);

  // ---- Seção comum Identify (postura + controles), pela projeção única + matriz de controles ----
  /** Postura da Função Identify (ID) — cabeçalho compartilhado das seis Funções. */
  idPosture = signal<FunctionPosture | null>(null);
  idPostureState = signal<'loading' | 'loaded' | 'notFound' | 'error'>('loading');
  /** Controles ID.* (matriz de conformidade filtrada pelo prefixo). */
  private readonly idControls = signal<TenantControlStateDto[]>([]);
  idControlsState = signal<'loading' | 'loaded' | 'error'>('loading');
  idTab = signal<'controls' | 'blind'>('controls');
  readonly idView = computed(() => buildPillarView(PILLARS.ID, this.idControls()));
  readonly idBlindCount = computed(() => buildPillarGapAnalysis(PILLARS.ID, this.idControls()).blindSpots.length);
  total = signal(0);
  totalPages = signal(0);
  page = signal(1);
  pageSize = signal(25);
  loading = signal(false);
  loadError = signal(false);
  /** Há uma resposta VÁLIDA do inventário para os filtros atuais? Sem ela, total e contagem ficam "—". */
  loaded = signal(false);

  // ---- Filtros ----
  selectedCategories = signal<Set<AssetCategory>>(new Set());
  riskLevel = signal<RiskLevel | null>(null);
  criticality = signal<number | null>(null);
  activeOnly = signal(false);
  search = signal('');

  hasAnyFilter = computed(
    () =>
      this.selectedCategories().size > 0 ||
      this.riskLevel() !== null ||
      this.criticality() !== null ||
      this.activeOnly() ||
      this.search().trim().length > 0,
  );

  // Constantes de UI expostas ao template.
  // Subtítulo tático da Função Identify (ID): a tela de inventário É a landing da Função (ID.AM/ID.RA). O
  // texto NÃO é mais hardcoded — vem do dicionário único NIST_FUNCTION_DESCRIPTIONS (nist-glossary.ts, DRY).
  protected readonly idDescription = NIST_FUNCTION_DESCRIPTIONS.ID;
  protected readonly categories = ASSET_CATEGORIES;
  protected readonly riskLevels = RISK_LEVELS;
  protected readonly criticalities = [1, 2, 3, 4];
  protected readonly pageSizes = [10, 25, 50, 100];
  protected readonly riskColor = riskColor;
  protected readonly label = categoryLabel;
  protected readonly apiBase = environment.apiBase;

  private searchTimer?: ReturnType<typeof setTimeout>;

  // ---- [AEGIS-ENTITY-RESOLUTION-01] Fontes do ativo: detalhe SOB DEMANDA (uma linha por vez, nunca N+1) ----
  expanded = signal<string | null>(null);
  detailState = signal<'loading' | 'loaded' | 'error'>('loading');
  detail = signal<AssetSources | null>(null);
  protected readonly sourcesCell = sourcesCell;
  protected readonly tone = crossSourceTone;
  protected readonly recordTone = recordResolutionTone;
  protected readonly joinFacts = joinSourceFacts;
  protected readonly conflictPair = conflictObservation;
  protected readonly contradictoryIds = contradictoryObservedIds;

  toggleSources(assetId: string): void {
    if (this.expanded() === assetId) {
      this.expanded.set(null);
      return;
    }
    this.expanded.set(assetId);
    this.loadSources(assetId);
  }

  loadSources(assetId: string): void {
    this.detail.set(null);
    this.detailState.set('loading');
    this.svc.sources(assetId).subscribe({
      next: (d) => {
        if (this.expanded() !== assetId) return;   // resposta de uma linha que já foi fechada
        this.detail.set(d);
        this.detailState.set('loaded');
      },
      error: () => {
        if (this.expanded() !== assetId) return;
        this.detailState.set('error');
      },
    });
  }

  ngOnInit(): void {
    this.load();
    this.loadWorkspacePosture();
    this.loadIdControls();
  }

  /** Cabeçalho de postura ID pela projeção única, com estados explícitos (init + retry). */
  loadWorkspacePosture(): void {
    this.idPostureState.set('loading');
    this.scoreSvc.fetchWorkspace().subscribe({
      next: (w) => {
        const f = functionOf(w, 'ID') ?? null;
        this.idPosture.set(f);
        this.idPostureState.set(f ? 'loaded' : 'notFound');
      },
      error: () => {
        this.idPosture.set(null);
        this.idPostureState.set('error');
      },
    });
  }

  /** Controles ID.* (matriz de conformidade), com estados explícitos (init + retry). */
  loadIdControls(): void {
    this.idControlsState.set('loading');
    this.scoring.getPillarControls('ID').subscribe({
      next: (list) => {
        this.idControls.set(list);
        this.idControlsState.set('loaded');
      },
      error: () => this.idControlsState.set('error'),
    });
  }

  /** Só a resposta da ÚLTIMA leitura pedida é aplicada — filtro, página ou releitura depois de uma declaração. */
  private listSeq = 0;

  /** @param pageCorrected esta leitura já é a da última página válida (uma correção só, nunca um laço). */
  private load(pageCorrected = false): void {
    const seq = ++this.listSeq;
    this.loading.set(true);
    this.expanded.set(null);
    this.svc
      .list({
        category: [...this.selectedCategories()],
        riskLevel: this.riskLevel(),
        criticality: this.criticality(),
        isActive: this.activeOnly() ? true : null,
        search: this.search(),
        page: this.page(),
        pageSize: this.pageSize(),
      })
      .subscribe({
        next: (res) => {
          if (seq !== this.listSeq) return;   // resposta tardia de um filtro, página ou releitura anterior
          const step = inventoryPageStep(this.page(), res.totalPages, pageCorrected);
          if (step.kind === 'reread') {
            // A página pedida deixou de existir (o ativo declarado saiu do filtro): a última válida, com os mesmos filtros.
            this.page.set(step.page);
            this.load(true);
            return;
          }
          this.page.set(step.page);
          this.rows.set(res.items);
          this.total.set(res.totalCount);
          this.totalPages.set(res.totalPages);
          this.loading.set(false);
          this.loadError.set(false);
          this.loaded.set(true);
        },
        error: (err) => {
          if (seq !== this.listSeq) return;
          console.error('Falha ao carregar o inventário de ativos:', err);
          this.rows.set([]);
          this.loading.set(false);
          this.loadError.set(true);
          this.loaded.set(false);
        },
      });
  }

  /** Filtros reiniciam a paginação para a página 1 e recarregam. */
  private reload(): void {
    this.page.set(1);
    this.inventoryNotice.set(null);
    this.load();
  }

  // ---- [AEGIS-RISK-PRIORITIZATION-01] Declaração de criticidade confirmada no detalhe ----
  inventoryNotice = signal<string | null>(null);

  /**
   * A linha reflete a criticidade CONFIRMADA pelo servidor sem recarregar a página. Se um filtro de criticidade ativo
   * deixar de corresponder, a lista é relida com os mesmos filtros e página — e isso é dito, em vez de o ativo sumir.
   */
  onCriticalityDeclared(change: DevicePriorityCriticalityChange): void {
    const row = this.rows().find((r) => r.id === change.assetId);
    this.rows.set(applyDeclaredCriticality(this.rows(), change));
    const filter = this.criticality();
    if (filter !== null && change.criticality.storedValue !== filter) {
      this.inventoryNotice.set(
        `Criticidade registrada para ${row?.name ?? 'o ativo'} (${change.criticality.label}). Ele não corresponde mais ao ` +
          `filtro de criticidade ${filter} e saiu desta lista.`,
      );
      this.load();
    }
  }

  toggleCategory(c: AssetCategory): void {
    const next = new Set(this.selectedCategories());
    next.has(c) ? next.delete(c) : next.add(c);
    this.selectedCategories.set(next);
    this.reload();
  }

  setRisk(value: string): void {
    this.riskLevel.set((value || null) as RiskLevel | null);
    this.reload();
  }

  setCriticality(value: string): void {
    this.criticality.set(value ? Number(value) : null);
    this.reload();
  }

  setActiveOnly(checked: boolean): void {
    this.activeOnly.set(checked);
    this.reload();
  }

  onSearch(term: string): void {
    this.search.set(term);
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 300);
  }

  clearFilters(): void {
    this.selectedCategories.set(new Set());
    this.riskLevel.set(null);
    this.criticality.set(null);
    this.activeOnly.set(false);
    this.search.set('');
    this.reload();
  }

  setPageSize(value: string): void {
    this.pageSize.set(Number(value));
    this.reload();
  }

  goTo(p: number): void {
    const max = this.totalPages() || 1;
    if (p < 1 || p > max) return;
    this.page.set(p);
    this.load();
  }
}
