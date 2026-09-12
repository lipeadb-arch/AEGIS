import { DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AssetService } from '../../services/asset.service';
import { AuthService } from '../../services/auth.service';
import {
  AssetDevicePriority,
  DEVICE_PRIORITY_HEADING,
  DevicePriorityCriticality,
  DevicePriorityCriticalityChange,
  DevicePriorityPolicy,
  bandCountsText,
  bandTone,
  canDeclareCriticality,
  casePageText,
  declarationErrorText,
  declarationSavedNotRefreshedText,
  dispositionText,
  epssText,
  factorEffectLabel,
  factorKindLabel,
} from '../../models/device-priority.models';

/**
 * [AEGIS-RISK-PRIORITIZATION-01] Como a ordem é decidida — a política versionada, igual na Central e no detalhe do ativo.
 * Todo o texto vem do backend (DevicePriorityNarrative.Policy); aqui só há apresentação.
 */
@Component({
  selector: 'app-device-priority-policy',
  standalone: true,
  template: `
    <details class="dpp">
      <summary>Como a ordem é decidida · {{ policy.code }} versão {{ policy.version }}</summary>
      <p class="dpp-p">{{ policy.rationale }}</p>
      <div class="dpp-scroll">
        <table class="dpp-table">
          <caption>Faixa base — severidade técnica × exploit informado pela fonte</caption>
          <thead>
            <tr>
              <th>Severidade técnica</th>
              <th>Exploit verificado</th>
              <th>Exploit público</th>
              <th>Sem exploit informado</th>
            </tr>
          </thead>
          <tbody>
            @for (r of policy.table; track r.severity) {
              <tr>
                <td>{{ r.severity }}</td>
                <td>{{ r.exploitVerified }}</td>
                <td>{{ r.exploitPublic }}</td>
                <td>{{ r.exploitNotInformed }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <span class="dpp-k">Agravantes (contexto comprovado)</span>
      <ul>@for (a of policy.aggravation; track $index) { <li>{{ a }}</li> }</ul>
      <span class="dpp-k">Desempates</span>
      <ul>@for (t of policy.tieBreaks; track $index) { <li>{{ t }}</li> }</ul>
      <span class="dpp-k">Fora da decisão nesta versão</span>
      <ul>@for (n of policy.notUsed; track $index) { <li>{{ n }}</li> }</ul>
      <p class="dpp-p">{{ policy.temporal.description }}</p>
    </details>
  `,
  styles: [
    `
      .dpp { font-size: 12px; color: var(--muted, #9aa7c7); }
      .dpp summary { cursor: pointer; font-family: var(--mono, monospace); font-size: 11px; }
      .dpp-p { margin: 6px 0; line-height: 1.5; max-width: 900px; }
      .dpp-k { display: block; margin-top: 6px; font-family: var(--mono, monospace); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.08em; }
      .dpp ul { margin: 4px 0 0; padding-left: 18px; line-height: 1.5; max-width: 900px; }
      .dpp-scroll { overflow-x: auto; }
      .dpp-table { border-collapse: collapse; font-size: 11.5px; margin-top: 6px; }
      .dpp-table caption { text-align: left; font-family: var(--mono, monospace); font-size: 10.5px; padding-bottom: 4px; }
      .dpp-table th, .dpp-table td { padding: 4px 10px; border-bottom: 1px solid var(--line-2, rgba(255,255,255,0.08)); text-align: left; }
      .dpp-table th { font-weight: 500; font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.06em; }
    `,
  ],
})
export class DevicePriorityPolicyComponent {
  @Input({ required: true }) policy!: DevicePriorityPolicy;
}

/**
 * [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento de UM dispositivo — o mesmo bloco no detalhe do inventário e na
 * Central de Prioridades. Responde: qual a posição e por quê (o caso determinante), quais fatores foram usados (valor,
 * origem, data e natureza), o que é desconhecido e poderia alterar a avaliação, as ressalvas, a próxima ação e os casos.
 * Carregamento, falha e vazio são estados distintos — uma falha nunca parece "sem prioridade".
 */
@Component({
  selector: 'app-device-priority',
  standalone: true,
  imports: [DatePipe, FormsModule, DevicePriorityPolicyComponent],
  template: `
    <section class="dp" [attr.aria-label]="heading">
      <div class="dp-head">
        <h4>{{ heading }}</h4>
        @if (state() === 'loaded') {
          <span class="dp-when">calculado em {{ data()!.evaluatedAt | date: 'dd/MM/yy HH:mm' }}</span>
        }
      </div>
      <!-- Resultado da declaração fica FORA dos estados de carga: gravação confirmada nunca vira "nada foi alterado". -->
      @if (saveNotice(); as n) {
        <p class="dp-sub" [class.warn]="n.tone === 'warn'" role="status">{{ n.text }}</p>
      }

      @switch (state()) {
        @case ('loading') {
          <span class="dp-pulse">Calculando a prioridade de tratamento…</span>
        }
        @case ('error') {
          <div class="dp-err">
            <span>Não foi possível calcular a prioridade deste dispositivo agora — nada é exibido, para que a falha não pareça ausência de prioridade.</span>
            <button type="button" class="dp-btn" (click)="retry()">Tentar novamente</button>
          </div>
        }
        @case ('loaded') {
          @let d = data()!;
          <div class="dp-position">
            <span class="dp-badge tone-{{ tone(d.band) }}">{{ d.bandLabel }}</span>
            <span class="dp-info">{{ d.informationLabel }}</span>
          </div>
          <p class="dp-reason">{{ d.positionReason }}</p>

          @if (d.determiningCase; as c) {
            <div class="dp-case">
              <span class="dp-k">Caso que determinou a posição</span>
              <div class="dp-case-row">
                <b class="dp-mono">{{ c.cveId }}</b>
                @if (c.title) { <span class="dp-sub">{{ c.title }}</span> }
              </div>
              <div class="dp-facts">
                <span>Severidade técnica: <b>{{ c.severityLabel }}</b></span>
                <span>{{ c.exploitLabel }}</span>
                <span>{{ epss(c.epss) }}</span>
              </div>
              @if (c.attackVectorLabel) { <span class="dp-sub">{{ c.attackVectorLabel }}</span> }
              @if (c.knownExploitedMarkedWithoutOrigin) {
                <span class="dp-sub warn">Marca de "explorada ativamente" no catálogo sem origem verificável — não usada na decisão.</span>
              }
              <span class="dp-sub">{{ c.source }} · aquisição de {{ c.acquiredAt | date: 'dd/MM/yy HH:mm' }} · {{ c.acquisitionLabel }} · observada desde {{ c.firstSeenAt | date: 'dd/MM/yy' }}</span>
            </div>
          }

          <div class="dp-next">
            <span class="dp-k">Próxima ação sugerida</span>
            <p>{{ d.nextAction }}</p>
          </div>

          @if (d.caveats.length) {
            <div class="dp-caveats">
              <span class="dp-k">Ressalvas</span>
              <ul>@for (n of d.caveats; track n.code + n.text) { <li>{{ n.text }}</li> }</ul>
            </div>
          }

          @if (d.absenceLabel) {
            <div class="dp-block">
              <span class="dp-k">Completude da fonte neste dispositivo</span>
              <p class="dp-note" [class.warn-text]="d.absenceState === 'notVerifiable'">{{ d.absenceLabel }}</p>
            </div>
          }

          <div class="dp-block">
            <span class="dp-k">Fatores da avaliação</span>
            <div class="dp-scroll">
              <table class="dp-table">
                <thead>
                  <tr>
                    <th>Fator</th>
                    <th>Valor ou estado</th>
                    <th>Natureza</th>
                    <th>Origem · data</th>
                    <th>Efeito</th>
                  </tr>
                </thead>
                <tbody>
                  @for (f of d.factors; track f.code) {
                    <tr [class.unknown]="f.kind === 'unknown'">
                      <td><b>{{ f.label }}</b></td>
                      <td>
                        {{ f.value }}
                        @if (f.note) { <span class="dp-sub">{{ f.note }}</span> }
                      </td>
                      <td><span class="dp-badge kind-{{ f.kind }}">{{ kind(f.kind) }}</span></td>
                      <td class="dp-date">
                        {{ f.source || '—' }}
                        @if (f.availableAt) { <span class="dp-sub">{{ f.availableAtLabel }}: {{ f.availableAt | date: 'dd/MM/yy HH:mm' }}</span> }
                      </td>
                      <td><span class="dp-effect effect-{{ f.effect }}">{{ effect(f.effect) }}</span></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>

          @if (d.couldChange.length) {
            <div class="dp-block">
              <span class="dp-k">O que poderia alterar a avaliação</span>
              <ul class="dp-list">@for (x of d.couldChange; track $index) { <li>{{ x }}</li> }</ul>
            </div>
          }

          <div class="dp-block">
            <span class="dp-k">Criticidade do ativo</span>
            <p class="dp-note">{{ d.criticality.label }}@if (d.criticality.note) { — {{ d.criticality.note }} }</p>
            @if (canDeclare()) {
              <div class="dp-declare">
                <label>
                  Declarar criticidade
                  <select [(ngModel)]="declValue" [attr.aria-label]="'Criticidade declarada'">
                    @for (v of [1, 2, 3, 4]; track v) { <option [ngValue]="v">{{ v }}</option> }
                  </select>
                </label>
                <input type="text" maxlength="500" [(ngModel)]="declNote" placeholder="Justificativa (opcional)" aria-label="Justificativa" />
                <button type="button" class="dp-btn" [disabled]="saving()" (click)="declare()">
                  {{ saving() ? 'Salvando…' : 'Registrar declaração' }}
                </button>
              </div>
              <span class="dp-sub">Opcional. A declaração registra quem e quando; sem ela a prioridade continua funcionando e mostra a lacuna.</span>
              @if (declError(); as e) { <span class="dp-sub warn">{{ e }}</span> }
            }
          </div>

          @if (d.situations.length) {
            <div class="dp-block">
              <span class="dp-k">Situações entre fontes usadas como contexto</span>
              <ul class="dp-list">
                @for (s of d.situations; track s.ruleCode) {
                  <li><span class="dp-mono">{{ s.ruleCode }} v{{ s.ruleVersion }}</span> — {{ s.stateLabel }}</li>
                }
              </ul>
              <span class="dp-sub">As evidências de cada situação estão em “Situações identificadas entre fontes”.</span>
            </div>
          }

          @if (d.cases; as cp) {
            <div class="dp-block">
              <span class="dp-k">Casos em aberto (ativo × CVE) na ordem da política</span>
              <span class="dp-note">{{ pageText(cp) }} · {{ counts(d.casesByBand) }}@if (d.insufficientCases > 0) { · {{ d.insufficientCases }} sem severidade informada }</span>
              @if (cp.items.length) {
                <div class="dp-scroll">
                  <table class="dp-table">
                    <thead>
                      <tr>
                        <th>CVE</th>
                        <th>Faixa</th>
                        <th>Severidade · exploit</th>
                        <th>Aquisição</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (c of cp.items; track c.cveId) {
                        <tr>
                          <td>
                            <b class="dp-mono">{{ c.cveId }}</b>
                            @if (c.title) { <span class="dp-sub">{{ c.title }}</span> }
                          </td>
                          <td>
                            <span class="dp-badge tone-{{ tone(c.band) }}">{{ c.bandLabel }}</span>
                            <span class="dp-sub">{{ c.reason }}</span>
                          </td>
                          <td>{{ c.severityLabel }}<span class="dp-sub">{{ c.exploitLabel }}</span></td>
                          <td class="dp-date">{{ c.acquiredAt | date: 'dd/MM/yy HH:mm' }}<span class="dp-sub">{{ c.acquisitionLabel }}</span></td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                @if (cp.total > cp.pageSize) {
                  <div class="dp-pager">
                    <button type="button" class="dp-btn" (click)="goCases(cp.page - 1)" [disabled]="cp.page <= 1">‹ Anterior</button>
                    <button type="button" class="dp-btn" (click)="goCases(cp.page + 1)" [disabled]="cp.page * cp.pageSize >= cp.total">Próxima ›</button>
                  </div>
                }
              }
            </div>
          }

          @if (disposition(d); as t) {
            <p class="dp-note">Fora da fila por disposição registrada, com a evidência preservada: {{ t }}.</p>
          }
          @if (d.noLongerReported > 0) {
            <p class="dp-note">{{ d.noLongerReported }} observação(ões) não mais reportadas pela fonte — isso não é correção validada e não entra na fila.</p>
          }
          @if (d.excludedOutOfPolicy > 0) {
            <p class="dp-note">{{ d.excludedOutOfPolicy }} observação(ões) em aberto anteriores à política temporal não sustentam prioridade atual.</p>
          }

          <details class="dp-limits">
            <summary>Limitações desta avaliação</summary>
            <ul>@for (l of d.limitations; track $index) { <li>{{ l }}</li> }</ul>
          </details>
          <app-device-priority-policy [policy]="d.policy" />
          <p class="dp-scope">{{ d.scope }}</p>
        }
      }
    </section>
  `,
  styles: [
    `
      .dp { display: flex; flex-direction: column; gap: 10px; margin-top: 14px; padding-top: 12px; border-top: 1px dashed var(--line, rgba(255,255,255,0.15)); }
      .dp-head { display: flex; justify-content: space-between; align-items: baseline; gap: 10px; flex-wrap: wrap; }
      .dp-head h4 { margin: 0; font-size: 13.5px; font-weight: 600; }
      .dp-when, .dp-note, .dp-pulse, .dp-sub, .dp-k, .dp-info { font-family: var(--mono, ui-monospace, monospace); font-size: 11px; color: var(--muted, #9aa7c7); }
      .dp-k { text-transform: uppercase; letter-spacing: 0.08em; display: block; }
      .dp-note { margin: 0; max-width: 900px; line-height: 1.5; display: block; }
      .dp-sub { display: block; margin-top: 2px; line-height: 1.45; }
      .dp-sub.warn, .dp-note.warn-text { color: var(--amber, #ffb020); }
      .dp-err { display: flex; flex-direction: column; gap: 8px; font-family: var(--mono, monospace); font-size: 12px; color: var(--muted, #9aa7c7); }
      .dp-position { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; }
      .dp-reason { margin: 0; font-size: 12.5px; line-height: 1.55; max-width: 900px; }
      .dp-case { border: 1px solid var(--line, rgba(255,255,255,0.15)); border-radius: 10px; padding: 8px 12px; display: flex; flex-direction: column; gap: 4px; max-width: 900px; }
      .dp-case-row { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
      .dp-facts { display: flex; gap: 14px; flex-wrap: wrap; font-size: 12px; }
      .dp-next p { margin: 2px 0 0; font-size: 12.5px; line-height: 1.5; max-width: 900px; }
      .dp-caveats ul, .dp-list, .dp-limits ul { margin: 4px 0 0; padding-left: 18px; font-size: 12px; line-height: 1.5; max-width: 900px; }
      .dp-caveats ul { color: var(--amber, #ffb020); }
      .dp-block { display: flex; flex-direction: column; gap: 4px; }
      .dp-scroll { overflow-x: auto; }
      .dp-table { width: 100%; border-collapse: collapse; font-size: 12px; table-layout: fixed; }
      .dp-table td { overflow-wrap: anywhere; padding: 6px 8px; vertical-align: top; border-bottom: 1px solid var(--line-2, rgba(255,255,255,0.07)); }
      .dp-table th { text-align: left; font-family: var(--mono, monospace); font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted, #9aa7c7); font-weight: 500; padding: 6px 8px; border-bottom: 1px solid var(--line, rgba(255,255,255,0.15)); }
      .dp-table tr.unknown td { opacity: 0.85; }
      .dp-date { font-family: var(--mono, monospace); font-size: 11px; }
      .dp-mono { font-family: var(--mono, monospace); }
      .dp-badge { display: inline-block; max-width: 100%; white-space: normal; font-family: var(--mono, monospace); font-size: 10.5px; line-height: 1.4; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--line, rgba(255,255,255,0.2)); color: var(--muted, #9aa7c7); }
      .dp-badge.tone-attention { color: var(--magenta, #ff3d9a); border-color: rgba(255, 61, 154, 0.55); }
      .dp-badge.tone-warn { color: var(--amber, #ffb020); border-color: rgba(255, 176, 32, 0.55); }
      .dp-badge.tone-info { color: var(--cyan, #26e0ff); border-color: rgba(38, 224, 255, 0.45); }
      .dp-badge.kind-unknown { border-style: dashed; }
      .dp-effect { font-size: 11px; }
      .dp-effect.effect-aggravating, .dp-effect.effect-determinant { color: var(--amber, #ffb020); }
      .dp-effect.effect-notUsed { opacity: 0.7; }
      .dp-declare { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; font-size: 12px; }
      .dp-declare select, .dp-declare input { font: inherit; font-size: 12px; color: inherit; background: transparent; border: 1px solid var(--line, rgba(255,255,255,0.2)); border-radius: 6px; padding: 3px 8px; }
      .dp-declare input { min-width: 16rem; }
      .dp-btn { cursor: pointer; font-family: var(--mono, monospace); font-size: 11px; color: var(--cyan, #26e0ff); background: rgba(38,224,255,0.06); border: 1px solid rgba(38,224,255,0.35); border-radius: 8px; padding: 5px 12px; align-self: flex-start; }
      .dp-btn:disabled { opacity: 0.4; cursor: not-allowed; }
      .dp-pager { display: flex; gap: 8px; }
      .dp-limits { font-size: 12px; color: var(--muted, #9aa7c7); }
      .dp-limits summary { cursor: pointer; font-family: var(--mono, monospace); font-size: 11px; }
      .dp-scope { margin: 0; font-size: 11.5px; line-height: 1.5; color: var(--muted, #9aa7c7); max-width: 900px; font-style: italic; }
    `,
  ],
})
export class DevicePriorityComponent implements OnChanges {
  @Input({ required: true }) assetId!: string;

  /**
   * Emitido UMA vez por declaração, só depois da confirmação do servidor — para a Central e o inventário atualizarem
   * faixa, ordem, contagens e criticidade. Nunca emitido por recarga, paginação ou falha.
   */
  @Output() readonly criticalityDeclared = new EventEmitter<DevicePriorityCriticalityChange>();

  private readonly svc = inject(AssetService);
  private readonly auth = inject(AuthService);

  protected readonly heading = DEVICE_PRIORITY_HEADING;
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');
  protected readonly data = signal<AssetDevicePriority | null>(null);
  protected readonly saving = signal(false);
  protected readonly declError = signal<string | null>(null);
  protected readonly saveNotice = signal<{ text: string; tone: 'ok' | 'warn' } | null>(null);
  /** Só a resposta da ÚLTIMA leitura pedida é aplicada (troca de ativo, paginação ou releitura após declarar). */
  private seq = 0;
  protected readonly canDeclare = computed(() => canDeclareCriticality(this.auth.activeRole()));
  protected declValue = 3;
  protected declNote = '';
  private readonly casePageSize = 10;
  private casePage = 1;

  protected readonly tone = bandTone;
  protected readonly kind = factorKindLabel;
  protected readonly effect = factorEffectLabel;
  protected readonly counts = bandCountsText;
  protected readonly pageText = casePageText;
  protected readonly epss = epssText;
  protected disposition(d: AssetDevicePriority): string | null {
    return dispositionText(d.dispositions);
  }

  ngOnChanges(): void {
    this.casePage = 1;
    this.saveNotice.set(null);
    this.declError.set(null);
    this.load();
  }

  /** @param saved criticalidade recém-confirmada pelo servidor, quando a leitura é a releitura depois da declaração. */
  load(saved?: DevicePriorityCriticality): void {
    const seq = ++this.seq;
    if (!saved) this.saveNotice.set(null);
    this.state.set('loading');
    this.svc.priority(this.assetId, this.casePage, this.casePageSize).subscribe({
      next: (d) => {
        if (seq !== this.seq) return;   // resposta tardia de outro ativo ou de uma leitura anterior
        this.data.set(d);
        this.declValue = d.criticality.declaredValue ?? d.criticality.storedValue;
        this.state.set('loaded');
        if (saved) this.saveNotice.set({ text: `Declaração registrada: ${saved.label}.`, tone: 'ok' });
      },
      error: () => {
        if (seq !== this.seq) return;
        this.data.set(null);
        this.state.set('error');
        if (saved) this.saveNotice.set({ text: declarationSavedNotRefreshedText(saved), tone: 'warn' });
      },
    });
  }

  protected retry(): void {
    this.load();
  }

  protected goCases(page: number): void {
    if (page < 1) return;
    this.casePage = page;
    this.load();
  }

  protected declare(): void {
    const assetId = this.assetId;
    this.saving.set(true);
    this.declError.set(null);
    this.svc.declareCriticality(assetId, this.declValue, this.declNote).subscribe({
      next: (criticality) => {
        this.saving.set(false);
        this.declNote = '';
        // Gravação CONFIRMADA: as superfícies que contêm este bloco se atualizam (mesmo que a linha já seja outra).
        this.criticalityDeclared.emit({ assetId, criticality });
        if (this.assetId === assetId) this.load(criticality);
      },
      error: (err: { status?: number; error?: unknown }) => {
        this.saving.set(false);
        if (this.assetId === assetId) this.declError.set(declarationErrorText(err));
      },
    });
  }
}
