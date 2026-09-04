import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DevicePostureService } from '../../services/device-posture.service';
import {
  DeviceActivityBucket,
  DeviceComplianceBucket,
  DeviceEncryptionBucket,
  DeviceGroup,
  DeviceGroupFilters,
  DevicePolicy,
  DevicePostureDimension,
  DevicePostureView,
  EMPTY_DEVICE_FILTERS,
  canShowNumbers,
  countOrDash,
  dimensionStatePt,
  filterDeviceGroups,
  operatingSystems,
  totalDevices,
  unassignedPolicies,
  unknownAssignmentPolicies,
} from '../../models/device-posture.models';

/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-02] Seção "Dispositivos gerenciados" da área Protect — postura de configuração
 * (políticas) e estado efetivo (conformidade) do gerenciador de dispositivos. CONSULTIVA e DETERMINÍSTICA:
 * todos os rótulos vêm do backend ou de tabelas fixas (nunca de IA), e a tela NUNCA promete avaliação NIST nem
 * alteração do AEGIS Score.
 *
 * As DUAS dimensões são exibidas SEPARADAMENTE, cada uma com seu estado. Uma dimensão bloqueada por permissão
 * mostra a ação para destravá-la — jamais "0 dispositivos não conformes".
 */
@Component({
  selector: 'app-device-posture',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="dp">
      <header class="dp-head">
        <p class="eyebrow">Gerenciamento de dispositivos</p>
        <h2>Dispositivos gerenciados</h2>
        <p class="lede">
          Mostra as políticas configuradas no gerenciador de dispositivos e como os aparelhos estão de fato.
          Política existente e aparelho conforme ajudam a enxergar a postura operacional, mas não comprovam
          controle implementado e não alteram o AEGIS Score.
        </p>
      </header>

      @switch (uiState()) {
        @case ('loading') {
          <div class="panel state"><span class="pulse">Carregando a postura de dispositivos…</span></div>
        }
        @case ('error') {
          <div class="panel state err">
            <b>Não foi possível carregar a postura de dispositivos.</b>
            <span>{{ error() }}</span>
            <button type="button" class="retry" (click)="load()">Tentar novamente</button>
          </div>
        }
        @case ('notConfigured') {
          <div class="panel state muted">
            <b>Nenhum gerenciador de dispositivos conectado</b>
            <span>
              Conecte o serviço de configuração e conformidade de dispositivos na conexão Microsoft
              (Configurações › Integrações) para enxergar políticas e conformidade.
            </span>
          </div>
        }
        @case ('neverSynced') {
          <div class="panel state muted">
            <b>Ainda não sincronizado</b>
            <span>A integração existe, mas nenhuma dimensão foi coletada. Rode uma sincronização.</span>
          </div>
        }
        @case ('data') {
          <!-- Aviso de NÃO pontuação — sempre visível junto dos números. -->
          <p class="banner">{{ view()!.scoreDisclaimer }}</p>

          <!-- ---- Dimensão 1: postura configurada ---- -->
          <div class="dim">
            <div class="dim-head">
              <h3>Postura configurada</h3>
              <span class="badge" [class.ok]="hasData(view()!.configuration)" [class.warn]="!hasData(view()!.configuration)">
                {{ label(view()!.configuration) }}
              </span>
            </div>

            @if (view()!.configuration.isStale) {
              <div class="notice warn">
                Mostrando as últimas políticas coletadas — a tentativa mais recente falhou. Os números podem
                estar defasados.
              </div>
            }

            @if (hasData(view()!.configuration)) {
              <div class="summary">
                <div class="chip">
                  <span class="n">{{ dash(view()!.configurationSummary.totalPolicies) }}</span>
                  <span class="l">Políticas configuradas</span>
                </div>
                <div class="chip">
                  <span class="n">{{ dash(view()!.configurationSummary.compliancePolicies) }}</span>
                  <span class="l">De conformidade</span>
                </div>
                <div class="chip">
                  <span class="n">{{ dash(view()!.configurationSummary.deviceConfigurations) }}</span>
                  <span class="l">De configuração</span>
                </div>
                <div class="chip ok">
                  <span class="n">{{ dash(view()!.configurationSummary.policiesAssigned) }}</span>
                  <span class="l">Com atribuição</span>
                </div>
                <div
                  class="chip"
                  [class.warn]="(view()!.configurationSummary.policiesUnassigned ?? 0) > 0"
                  title="Políticas cuja coleção de atribuições veio vazia — comprovadamente sem alcance">
                  <span class="n">{{ dash(view()!.configurationSummary.policiesUnassigned) }}</span>
                  <span class="l">Sem atribuição</span>
                </div>
                @if ((view()!.configurationSummary.policiesAssignmentUnknown ?? 0) > 0) {
                  <div class="chip" title="A fonte não devolveu as atribuições destas políticas — o alcance não pode ser afirmado">
                    <span class="n">{{ dash(view()!.configurationSummary.policiesAssignmentUnknown) }}</span>
                    <span class="l">Atribuição desconhecida</span>
                  </div>
                }
              </div>

              @if (!hasData(view()!.assignment)) {
                <div class="notice warn">
                  A fonte não devolveu as atribuições das políticas. O alcance não pode ser afirmado — nenhuma
                  política é contada como "sem atribuição" por falta de dado.
                </div>
              }

              @if (view()!.policies.length === 0) {
                <div class="panel state muted">
                  <b>Nenhuma política configurada</b>
                  <span>A coleta foi concluída e o gerenciador de dispositivos não tem políticas configuradas.</span>
                </div>
              } @else {
                <ul class="rows" role="list">
                  @for (p of visiblePolicies(); track p.kind + p.externalId) {
                    <li class="row" [class.attn]="p.assignmentState === 'Unassigned'">
                      <div class="row-main">
                        <span class="rname">{{ p.displayName }}</span>
                        <span class="tag">{{ p.kindLabel }}</span>
                        @if (p.platformLabel) {
                          <span class="tag">{{ p.platformLabel }}</span>
                        }
                      </div>
                      <div class="row-meta">
                        <span class="status" [class.attn]="p.assignmentState === 'Unassigned'">
                          {{ p.assignmentLabel }}
                        </span>
                        @if (p.lastModifiedAt) {
                          <span class="count">Alterada em {{ p.lastModifiedAt | date: 'shortDate' }}</span>
                        }
                      </div>
                    </li>
                  }
                </ul>

                @if (view()!.policies.length > visiblePolicies().length) {
                  <button type="button" class="more" (click)="showAllPolicies.set(true)">
                    Mostrar todas as {{ view()!.policies.length }} políticas
                  </button>
                }
              }
            } @else {
              <div class="panel state warn">
                <b>{{ label(view()!.configuration) }}</b>
                @if (view()!.configuration.actionHint) {
                  <span>{{ view()!.configuration.actionHint }}</span>
                }
                @if (view()!.configuration.requiredPermission) {
                  <span class="detail">Permissão necessária: <code>{{ view()!.configuration.requiredPermission }}</code></span>
                }
              </div>
            }
          </div>

          <!-- ---- Dimensão 2: estado efetivo dos dispositivos ---- -->
          <div class="dim">
            <div class="dim-head">
              <h3>Estado efetivo dos dispositivos</h3>
              <span class="badge" [class.ok]="hasData(view()!.devices)" [class.warn]="!hasData(view()!.devices)">
                {{ label(view()!.devices) }}
              </span>
            </div>

            @if (hasData(view()!.devices)) {
              @if (view()!.devices.isStale) {
                <div class="notice warn">
                  Mostrando o último inventário de dispositivos coletado — a tentativa mais recente falhou.
                </div>
              }

              <div class="summary">
                <div class="chip">
                  <span class="n">{{ dash(view()!.deviceSummary.totalDevices) }}</span>
                  <span class="l">Dispositivos gerenciados</span>
                </div>
                <div class="chip ok">
                  <span class="n">{{ dash(view()!.deviceSummary.compliant) }}</span>
                  <span class="l">Conformes</span>
                </div>
                <div class="chip" [class.warn]="(view()!.deviceSummary.noncompliant ?? 0) > 0">
                  <span class="n">{{ dash(view()!.deviceSummary.noncompliant) }}</span>
                  <span class="l">Não conformes</span>
                </div>
                <div class="chip" title="Não conformes, mas dentro do período de carência da política">
                  <span class="n">{{ dash(view()!.deviceSummary.inGracePeriod) }}</span>
                  <span class="l">Em carência</span>
                </div>
                <div class="chip" title="A fonte não avaliou estes dispositivos — não são conformes nem não conformes">
                  <span class="n">{{ dash(notEvaluated()) }}</span>
                  <span class="l">Não avaliados</span>
                </div>
                <div class="chip" [class.warn]="(view()!.deviceSummary.stale ?? 0) > 0">
                  <span class="n">{{ dash(view()!.deviceSummary.stale) }}</span>
                  <span class="l">Sem sincronizar há {{ view()!.deviceSummary.staleThresholdDays }}d</span>
                </div>
                @if (encryptionIsUsable()) {
                  <div class="chip ok" title="Cobertura de criptografia informada pela fonte">
                    <span class="n">{{ dash(view()!.deviceSummary.encrypted) }}</span>
                    <span class="l">Criptografados</span>
                  </div>
                }
              </div>

              @if ((view()!.deviceSummary.unknownEncryption ?? 0) > 0) {
                <div class="notice warn">
                  {{ view()!.deviceSummary.unknownEncryption }} dispositivo(s) sem informação de criptografia.
                  Eles não são contados como criptografados nem como sem criptografia.
                </div>
              }

              <!-- Filtros sobre os grupos AGREGADOS (sem nenhum identificador de dispositivo). -->
              <div class="filters">
                <label>
                  <span>Conformidade</span>
                  <select [value]="filters().compliance ?? ''" (change)="setCompliance($event)">
                    <option value="">Todas</option>
                    @for (o of complianceOptions(); track o.value) {
                      <option [value]="o.value">{{ o.label }}</option>
                    }
                  </select>
                </label>
                <label>
                  <span>Sistema operacional</span>
                  <select [value]="filters().operatingSystem ?? ''" (change)="setOs($event)">
                    <option value="">Todos</option>
                    @for (os of osOptions(); track os) {
                      <option [value]="os">{{ os }}</option>
                    }
                  </select>
                </label>
                <label>
                  <span>Sincronização</span>
                  <select [value]="filters().activity ?? ''" (change)="setActivity($event)">
                    <option value="">Todas</option>
                    <option value="Active">Sincronizado recentemente</option>
                    <option value="Stale">Sem sincronização recente</option>
                    <option value="Unknown">Não informada</option>
                  </select>
                </label>
                <label>
                  <span>Criptografia</span>
                  <select [value]="filters().encryption ?? ''" (change)="setEncryption($event)">
                    <option value="">Todas</option>
                    <option value="Encrypted">Criptografado</option>
                    <option value="NotEncrypted">Sem criptografia</option>
                    <option value="Unknown">Não informada</option>
                  </select>
                </label>
                <button type="button" class="more" (click)="clearFilters()">Limpar filtros</button>
              </div>

              <p class="filter-total">
                {{ filteredTotal() }} de {{ dash(view()!.deviceSummary.totalDevices) }} dispositivo(s) no recorte atual.
              </p>

              @if (filteredGroups().length === 0) {
                <div class="panel state muted">
                  <b>Nenhum dispositivo neste recorte</b>
                  <span>Os filtros atuais não correspondem a nenhum grupo de dispositivos coletado.</span>
                </div>
              } @else {
                <ul class="rows" role="list">
                  @for (g of filteredGroups(); track groupKey(g)) {
                    <li class="row" [class.attn]="g.compliance === 'Noncompliant'">
                      <div class="row-main">
                        <span class="rname">{{ g.operatingSystem }}</span>
                        <span class="tag">{{ g.complianceLabel }}</span>
                        <span class="tag">{{ g.encryptionLabel }}</span>
                        <span class="tag">{{ g.activityLabel }}</span>
                      </div>
                      <div class="row-meta">
                        <span class="count">{{ g.deviceCount }} dispositivo(s)</span>
                      </div>
                    </li>
                  }
                </ul>
              }
            } @else {
              <div class="panel state warn">
                <b>{{ label(view()!.devices) }}</b>
                @if (view()!.devices.actionHint) {
                  <span>{{ view()!.devices.actionHint }}</span>
                }
                @if (view()!.devices.requiredPermission) {
                  <span class="detail">Permissão necessária: <code>{{ view()!.devices.requiredPermission }}</code></span>
                }
                <span class="detail">
                  Enquanto esta dimensão não estiver disponível, o AEGIS não afirma nada sobre a conformidade dos
                  aparelhos — nem que existem, nem que estão conformes.
                </span>
              </div>
            }
          </div>

          <!-- ---- Lacuna de correlação (registrada, nunca estimada) ---- -->
          @if (!view()!.correlation.deterministicCorrelationAvailable) {
            <div class="notice">{{ view()!.correlation.explanation }}</div>
          }

          <footer class="dp-foot">
            <span>Fonte: {{ view()!.source }}</span>
            @if (view()!.configuration.lastCollectionAt) {
              <span>Políticas coletadas em {{ view()!.configuration.lastCollectionAt | date: 'short' }}</span>
            }
            @if (view()!.devices.lastCollectionAt) {
              <span>Dispositivos coletados em {{ view()!.devices.lastCollectionAt | date: 'short' }}</span>
            }
          </footer>
        }
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
        padding: 0 32px 40px;
      }
      .dp {
        border: 1px solid var(--line, #26304a);
        border-radius: 14px;
        padding: 22px 22px 18px;
        background: rgba(122, 145, 190, 0.03);
      }
      .eyebrow {
        font-family: var(--mono, monospace);
        font-size: 10px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--cyan, #26e0ff);
        margin: 0 0 4px;
      }
      .dp-head h2 {
        font-family: var(--sans, sans-serif);
        font-size: 19px;
        color: var(--text, #e6ecf5);
        margin: 0 0 6px;
      }
      .lede {
        color: var(--muted, #8a97ad);
        font-family: var(--sans, sans-serif);
        font-size: 13px;
        line-height: 1.6;
        margin: 0;
        max-width: 780px;
      }
      .banner {
        margin: 16px 0 12px;
        padding: 9px 13px;
        border: 1px solid rgba(38, 224, 255, 0.28);
        border-left: 3px solid var(--cyan, #26e0ff);
        border-radius: 8px;
        background: rgba(38, 224, 255, 0.05);
        font-family: var(--mono, monospace);
        font-size: 11.5px;
        color: var(--text, #e6ecf5);
      }
      .dim {
        margin-top: 18px;
        padding-top: 14px;
        border-top: 1px solid var(--line, #26304a);
      }
      .dim-head {
        display: flex;
        align-items: center;
        gap: 10px;
        margin: 0 0 10px;
      }
      .dim-head h3 {
        font-family: var(--sans, sans-serif);
        font-size: 15px;
        color: var(--text, #e6ecf5);
        margin: 0;
      }
      .badge {
        font-family: var(--mono, monospace);
        font-size: 10px;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        border-radius: 999px;
        padding: 3px 10px;
        border: 1px solid var(--line, #26304a);
        color: var(--muted, #8a97ad);
      }
      .badge.ok {
        border-color: rgba(38, 224, 255, 0.35);
        color: var(--cyan, #26e0ff);
        background: rgba(38, 224, 255, 0.06);
      }
      .badge.warn {
        border-color: rgba(255, 176, 32, 0.4);
        color: var(--amber, #ffb020);
        background: rgba(255, 176, 32, 0.06);
      }
      .notice {
        margin: 0 0 12px;
        padding: 8px 12px;
        border-radius: 8px;
        border: 1px solid var(--line, #26304a);
        font-family: var(--mono, monospace);
        font-size: 11.5px;
        color: var(--muted, #8a97ad);
        line-height: 1.6;
      }
      .notice.warn {
        border-color: rgba(255, 176, 32, 0.4);
        background: rgba(255, 176, 32, 0.06);
        color: var(--amber, #ffb020);
      }
      .summary {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        margin: 4px 0 14px;
      }
      .chip {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 128px;
        padding: 10px 13px;
        border: 1px solid var(--line, #26304a);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.04);
      }
      .chip .n {
        font-family: var(--display, sans-serif);
        font-weight: 700;
        font-size: 22px;
        color: var(--text, #e6ecf5);
      }
      .chip .l {
        font-family: var(--mono, monospace);
        font-size: 9.5px;
        text-transform: uppercase;
        letter-spacing: 0.1em;
        color: var(--muted, #8a97ad);
      }
      .chip.ok .n {
        color: var(--cyan, #26e0ff);
      }
      .chip.warn {
        border-color: rgba(255, 176, 32, 0.4);
        background: rgba(255, 176, 32, 0.05);
      }
      .chip.warn .n {
        color: var(--amber, #ffb020);
      }
      .filters {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: 10px;
        margin: 0 0 10px;
      }
      .filters label {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .filters span {
        font-family: var(--mono, monospace);
        font-size: 9.5px;
        text-transform: uppercase;
        letter-spacing: 0.1em;
        color: var(--muted, #8a97ad);
      }
      .filters select {
        background: rgba(122, 145, 190, 0.06);
        border: 1px solid var(--line, #26304a);
        border-radius: 8px;
        color: var(--text, #e6ecf5);
        font-family: var(--sans, sans-serif);
        font-size: 12.5px;
        padding: 6px 9px;
        min-width: 170px;
      }
      .filter-total {
        font-family: var(--mono, monospace);
        font-size: 11px;
        color: var(--muted, #8a97ad);
        margin: 0 0 10px;
      }
      .rows {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        justify-content: space-between;
        gap: 8px 14px;
        padding: 11px 13px;
        border: 1px solid var(--line, #26304a);
        border-left: 3px solid var(--line, #26304a);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.02);
      }
      .row.attn {
        border-left-color: var(--amber, #ffb020);
      }
      .row-main {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 8px;
        min-width: 0;
      }
      .rname {
        font-family: var(--sans, sans-serif);
        font-size: 13.5px;
        color: var(--text, #e6ecf5);
      }
      .tag {
        font-family: var(--mono, monospace);
        font-size: 10px;
        color: var(--muted, #8a97ad);
        border: 1px solid var(--line, #26304a);
        border-radius: 999px;
        padding: 2px 8px;
      }
      .row-meta {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 10px;
      }
      .count,
      .status {
        font-family: var(--mono, monospace);
        font-size: 10.5px;
        color: var(--muted, #8a97ad);
      }
      .status.attn {
        color: var(--amber, #ffb020);
      }
      .panel.state {
        display: flex;
        flex-direction: column;
        gap: 6px;
        padding: 16px 18px;
        border: 1px solid var(--line, #26304a);
        border-radius: 10px;
        background: rgba(122, 145, 190, 0.03);
      }
      .panel.state b {
        font-family: var(--sans, sans-serif);
        font-size: 14px;
        color: var(--text, #e6ecf5);
      }
      .panel.state span {
        font-family: var(--sans, sans-serif);
        font-size: 12.5px;
        color: var(--muted, #8a97ad);
        line-height: 1.6;
      }
      .panel.state .detail {
        font-family: var(--mono, monospace);
        font-size: 11px;
      }
      .panel.state.warn {
        border-color: rgba(255, 176, 32, 0.4);
        background: rgba(255, 176, 32, 0.05);
      }
      .panel.state.err {
        border-color: rgba(255, 92, 92, 0.4);
        background: rgba(255, 92, 92, 0.05);
      }
      code {
        font-family: var(--mono, monospace);
        font-size: 11px;
        color: var(--cyan, #26e0ff);
      }
      .more,
      .retry {
        align-self: flex-start;
        margin-top: 10px;
        cursor: pointer;
        font-family: var(--mono, monospace);
        font-size: 11px;
        color: var(--cyan, #26e0ff);
        background: rgba(38, 224, 255, 0.06);
        border: 1px solid rgba(38, 224, 255, 0.35);
        border-radius: 8px;
        padding: 5px 12px;
      }
      .dp-foot {
        display: flex;
        flex-wrap: wrap;
        gap: 14px;
        margin-top: 16px;
        padding-top: 10px;
        border-top: 1px solid var(--line, #26304a);
        font-family: var(--mono, monospace);
        font-size: 10.5px;
        color: var(--muted, #8a97ad);
      }
      .pulse {
        font-family: var(--mono, monospace);
        font-size: 12px;
        color: var(--muted, #8a97ad);
        animation: pulse 1.4s ease-in-out infinite;
      }
      @keyframes pulse {
        0%,
        100% {
          opacity: 0.35;
        }
        50% {
          opacity: 0.75;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .pulse {
          animation: none;
        }
      }
    `,
  ],
})
export class DevicePostureComponent implements OnInit {
  private readonly svc = inject(DevicePostureService);

  /** Limite seguro inicial de políticas exibidas (o resto abre sob demanda). */
  private static readonly InitialPolicyLimit = 25;

  readonly view = signal<DevicePostureView | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly showAllPolicies = signal(false);
  readonly filters = signal<DeviceGroupFilters>({ ...EMPTY_DEVICE_FILTERS });

  /** Estado da UI derivado — uma única fonte para o @switch do template. */
  readonly uiState = computed<'loading' | 'error' | 'notConfigured' | 'neverSynced' | 'data'>(() => {
    if (this.loading()) return 'loading';
    if (this.error()) return 'error';
    const v = this.view();
    if (!v) return 'error';
    if (v.state === 'NotConfigured') return 'notConfigured';
    if (v.state === 'NeverSynced') return 'neverSynced';
    return 'data';
  });

  /**
   * Políticas ordenadas por ACIONABILIDADE: as comprovadamente SEM atribuição primeiro (não alcançam ninguém —
   * é o achado que o operador precisa ver), depois as de atribuição não comprovada, e por fim as atribuídas.
   * Os dois primeiros baldes vêm da mesma lógica pura testada que os separa — atribuição desconhecida NUNCA é
   * apresentada como "sem atribuição".
   */
  readonly orderedPolicies = computed<DevicePolicy[]>(() => {
    const all = this.view()?.policies ?? [];
    const unassigned = unassignedPolicies(all);
    const unknown = unknownAssignmentPolicies(all);
    const assigned = all.filter((p) => p.assignmentState === 'Assigned');
    return [...unassigned, ...unknown, ...assigned];
  });

  readonly visiblePolicies = computed(() => {
    const all = this.orderedPolicies();
    return this.showAllPolicies() ? all : all.slice(0, DevicePostureComponent.InitialPolicyLimit);
  });

  readonly filteredGroups = computed<DeviceGroup[]>(() =>
    filterDeviceGroups(this.view()?.deviceGroups ?? [], this.filters()),
  );

  readonly filteredTotal = computed(() => totalDevices(this.filteredGroups()));

  readonly osOptions = computed(() => operatingSystems(this.view()?.deviceGroups ?? []));

  /** Só oferece filtrar por conformidades REALMENTE presentes no inventário coletado. */
  readonly complianceOptions = computed(() => {
    const groups = this.view()?.deviceGroups ?? [];
    const seen = new Map<DeviceComplianceBucket, string>();
    for (const g of groups) if (!seen.has(g.compliance)) seen.set(g.compliance, g.complianceLabel);
    return [...seen.entries()].map(([value, label]) => ({ value, label }));
  });

  /** Não avaliados = tudo que a fonte não afirmou como conforme/não conforme/em carência. Nunca "conforme". */
  readonly notEvaluated = computed<number | null>(() => {
    const s = this.view()?.deviceSummary;
    if (!s || s.unknownCompliance === null) return null;
    return (s.unknownCompliance ?? 0) + (s.conflict ?? 0) + (s.error ?? 0) + (s.managedExternally ?? 0);
  });

  /** A cobertura de criptografia só é exibida se ao menos um dispositivo trouxe o dado. */
  readonly encryptionIsUsable = computed(() => {
    const s = this.view()?.deviceSummary;
    if (!s) return false;
    return (s.encrypted ?? 0) + (s.notEncrypted ?? 0) > 0;
  });

  /** Ausência (null) vira traço — nunca zero. Delegado à lógica pura testada. */
  protected dash(value: number | null | undefined): string {
    return countOrDash(value);
  }

  /** A dimensão pode mostrar números? Uma única porta, testada, para os dois blocos da tela. */
  protected hasData(dimension: DevicePostureDimension): boolean {
    return canShowNumbers(dimension);
  }

  /**
   * Rótulo do estado. O backend já entrega o texto pt-BR; o fallback determinístico cobre uma resposta antiga ou
   * um estado novo que ele venha a introduzir — e nunca cai em "Disponível" por desconhecimento.
   */
  protected label(dimension: DevicePostureDimension): string {
    return dimension.label || dimensionStatePt(dimension.state);
  }

  protected groupKey(g: DeviceGroup): string {
    return `${g.operatingSystem}|${g.compliance}|${g.encryption}|${g.activity}`;
  }

  protected setCompliance(e: Event): void {
    const v = (e.target as HTMLSelectElement).value;
    this.filters.update((f) => ({ ...f, compliance: (v || null) as DeviceComplianceBucket | null }));
  }

  protected setOs(e: Event): void {
    const v = (e.target as HTMLSelectElement).value;
    this.filters.update((f) => ({ ...f, operatingSystem: v || null }));
  }

  protected setActivity(e: Event): void {
    const v = (e.target as HTMLSelectElement).value;
    this.filters.update((f) => ({ ...f, activity: (v || null) as DeviceActivityBucket | null }));
  }

  protected setEncryption(e: Event): void {
    const v = (e.target as HTMLSelectElement).value;
    this.filters.update((f) => ({ ...f, encryption: (v || null) as DeviceEncryptionBucket | null }));
  }

  protected clearFilters(): void {
    this.filters.set({ ...EMPTY_DEVICE_FILTERS });
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.showAllPolicies.set(false);
    this.clearFilters();
    this.svc.get().subscribe({
      next: (v) => {
        this.view.set(v);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }
}
