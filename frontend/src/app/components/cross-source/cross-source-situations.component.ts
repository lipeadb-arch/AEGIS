import { DatePipe } from '@angular/common';
import { Component, Input, OnChanges, inject, signal } from '@angular/core';
import { AssetService } from '../../services/asset.service';
import {
  AssetCrossSource,
  CROSS_SOURCE_HEADING,
  acquisitionTone,
  crossSourceStateTone,
  cvePageText,
} from '../../models/cross-source.models';
import { severityPt } from '../../models/vulnerability.models';

/**
 * [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes de UM ativo — o mesmo bloco no detalhe do inventário e
 * na Central de Prioridades. Responde, por regra: "O que foi identificado?", "Quais dados sustentam isso?" e "O que deve
 * ser verificado ou tratado?", com as evidências de cada fonte (datas próprias), as ressalvas, os critérios e as
 * limitações. Todo o texto e o estado vêm do backend; aqui só há apresentação. Carregamento, falha e vazio são
 * estados distintos — uma falha nunca parece "nenhuma situação".
 */
@Component({
  selector: 'app-cross-source-situations',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="xsc" [attr.aria-label]="heading">
      <div class="xsc-head">
        <h4>{{ heading }}</h4>
        @if (state() === 'loaded') {
          <span class="xsc-when">calculado em {{ data()!.evaluatedAt | date: 'dd/MM/yy HH:mm' }}</span>
        }
      </div>

      @switch (state()) {
        @case ('loading') {
          <span class="xsc-pulse">Avaliando as regras entre fontes…</span>
        }
        @case ('error') {
          <div class="xsc-err">
            <span>Não foi possível avaliar as situações entre fontes agora — nada é exibido, para que a falha não pareça ausência de situação.</span>
            <button type="button" class="xsc-retry" (click)="load()">Tentar novamente</button>
          </div>
        }
        @case ('loaded') {
          @let d = data()!;
          <p class="xsc-scope">{{ d.scope }}</p>

          @for (r of d.rules; track r.ruleCode) {
            <article class="xsc-rule tone-{{ tone(r.state) }}">
              <header class="xsc-rule-head">
                <span class="xsc-badge tone-{{ tone(r.state) }}">{{ r.stateLabel }}</span>
                <b>{{ r.title }}</b>
                <span class="xsc-code">{{ r.ruleCode }} · versão {{ r.ruleVersion }}</span>
              </header>
              <dl class="xsc-qa">
                <dt>O que foi identificado?</dt>
                <dd>{{ r.summary }}</dd>
                <dt>Quais dados sustentam isso?</dt>
                <dd>{{ r.supportingData }}</dd>
                <dt>O que deve ser verificado ou tratado?</dt>
                <dd>{{ r.whatToVerify }}</dd>
              </dl>
              @if (r.caveats.length) {
                <div class="xsc-caveats">
                  <span class="xsc-k">Ressalvas</span>
                  <ul>
                    @for (c of r.caveats; track c.code + c.text) {
                      <li>{{ c.text }}</li>
                    }
                  </ul>
                </div>
              }
              <p class="xsc-limit">{{ r.limitation }}</p>
              <details class="xsc-criteria">
                <summary>Critérios da regra (versão {{ r.ruleVersion }})</summary>
                <ol>
                  @for (c of r.criteria; track $index) {
                    <li>{{ c }}</li>
                  }
                </ol>
              </details>
            </article>
          }

          <div class="xsc-block">
            <span class="xsc-k">Evidências de cada fonte</span>
            <p class="xsc-note">{{ d.associationReason }}</p>
            @if (d.evidence.length === 0) {
              <p class="xsc-note">Este ativo não tem registro das fontes usadas por estas regras.</p>
            } @else {
              <div class="xsc-scroll">
                <table class="xsc-table">
                  <thead>
                    <tr>
                      <th>Fonte</th>
                      <th>Informação</th>
                      <th>Obtida pelo AEGIS</th>
                      <th>Atividade informada pela fonte</th>
                      <th>Aquisição</th>
                      <th>Na combinação</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (e of d.evidence; track $index) {
                      <tr [class.off]="!e.eligible">
                        <td>
                          <b>{{ e.source }}</b>
                          <span class="xsc-sub">{{ e.connectorName }} · {{ e.roleLabel }}</span>
                        </td>
                        <td>
                          @if (e.role === 'deviceManagement') {
                            {{ e.complianceLabel }} · {{ e.encryptionLabel }}
                          } @else {
                            vulnerabilidades por dispositivo (lista abaixo)
                          }
                          <span class="xsc-sub">{{ e.resolutionLabel }}@if (e.linkedAt) { · vinculado em {{ e.linkedAt | date: 'dd/MM/yy HH:mm' }} }</span>
                        </td>
                        <td class="xsc-date">{{ e.acquiredAt | date: 'dd/MM/yy HH:mm' }}</td>
                        <td class="xsc-date">{{ e.sourceActivityAt ? (e.sourceActivityAt | date: 'dd/MM/yy HH:mm') : 'não informada' }}</td>
                        <td>
                          <span class="xsc-badge tone-{{ acqTone(e.acquisitionState) }}">{{ e.acquisitionLabel }}</span>
                          @if (e.latestAttemptFailed) {
                            <span class="xsc-sub warn">tentativa mais recente da fonte falhou</span>
                          }
                        </td>
                        <td>
                          <span class="xsc-badge" [class.tone-ok]="e.eligible" [class.tone-muted]="!e.eligible">{{ e.eligibilityLabel }}</span>
                          @if (e.exclusionReason) {
                            <span class="xsc-sub">{{ e.exclusionReason }}</span>
                          }
                          @if (e.noLongerObservedSince) {
                            <span class="xsc-sub">não mais observado desde {{ e.noLongerObservedSince | date: 'dd/MM/yy HH:mm' }}</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </div>

          @if (d.cves; as cv) {
            <div class="xsc-block">
              <span class="xsc-k">CVEs em aberto nas evidências elegíveis</span>
              <span class="xsc-note">{{ cvePageText(cv) }} · uma linha por CVE, com cada fonte que a reporta</span>
              @if (cv.items.length > 0) {
                <div class="xsc-scroll">
                  <table class="xsc-table">
                    <thead>
                      <tr>
                        <th>CVE</th>
                        <th>Severidade (fonte)</th>
                        <th>Reportada por</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (c of cv.items; track c.cveId) {
                        <tr>
                          <td>
                            <b class="xsc-mono">{{ c.cveId }}</b>
                            @if (c.title) { <span class="xsc-sub">{{ c.title }}</span> }
                          </td>
                          <td>
                            {{ sev(c.severity) }}
                            @if (c.cvssScore !== null) { <span class="xsc-sub">CVSS {{ c.cvssScore }}</span> }
                          </td>
                          <td>
                            @for (s of c.sources; track $index) {
                              <span class="xsc-sub">
                                {{ s.source }} · {{ s.connectorName }} — aquisição de {{ s.acquiredAt | date: 'dd/MM/yy HH:mm' }}
                                <span class="xsc-badge tone-{{ acqTone(s.acquisitionState) }}">{{ s.acquisitionLabel }}</span>
                              </span>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                @if (cv.total > cv.pageSize) {
                  <div class="xsc-pager">
                    <button type="button" (click)="goCves(cv.page - 1)" [disabled]="cv.page <= 1">‹ Anterior</button>
                    <button type="button" (click)="goCves(cv.page + 1)" [disabled]="cv.page * cv.pageSize >= cv.total">Próxima ›</button>
                  </div>
                }
              }
              @if (cv.noLongerReported > 0) {
                <span class="xsc-note">{{ cv.noLongerReported }} observação(ões) não mais reportadas pela fonte não sustentam condição aberta.</span>
              }
              @if (cv.excludedOutOfPolicy > 0) {
                <span class="xsc-note">{{ cv.excludedOutOfPolicy }} observação(ões) em aberto anteriores à política temporal não foram consideradas.</span>
              }
            </div>
          }

          <p class="xsc-note">{{ d.policy.description }}</p>
        }
      }
    </section>
  `,
  styles: [
    `
      .xsc { display: flex; flex-direction: column; gap: 10px; margin-top: 14px; padding-top: 12px; border-top: 1px dashed var(--line, rgba(255,255,255,0.15)); }
      .xsc-head { display: flex; justify-content: space-between; align-items: baseline; gap: 10px; flex-wrap: wrap; }
      .xsc-head h4 { margin: 0; font-size: 13.5px; font-weight: 600; }
      .xsc-when, .xsc-note, .xsc-pulse, .xsc-code, .xsc-sub, .xsc-k { font-family: var(--mono, ui-monospace, monospace); font-size: 11px; color: var(--muted, #9aa7c7); }
      .xsc-k { text-transform: uppercase; letter-spacing: 0.08em; }
      .xsc-note { margin: 0; max-width: 900px; line-height: 1.5; display: block; }
      .xsc-scope { margin: 0; font-size: 12.5px; line-height: 1.55; max-width: 900px; }
      .xsc-err { display: flex; flex-direction: column; gap: 8px; font-family: var(--mono, monospace); font-size: 12px; color: var(--muted, #9aa7c7); }
      .xsc-retry { align-self: flex-start; cursor: pointer; font-family: var(--mono, monospace); font-size: 11px; color: var(--cyan, #26e0ff); background: rgba(38,224,255,0.06); border: 1px solid rgba(38,224,255,0.35); border-radius: 8px; padding: 5px 12px; }
      .xsc-rule { border: 1px solid var(--line, rgba(255,255,255,0.15)); border-radius: 10px; padding: 10px 12px; display: flex; flex-direction: column; gap: 6px; }
      .xsc-rule.tone-attention { border-color: rgba(255, 61, 154, 0.45); }
      .xsc-rule.tone-warn { border-color: rgba(255, 176, 32, 0.45); }
      .xsc-rule-head { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
      .xsc-qa { margin: 0; display: grid; grid-template-columns: minmax(180px, 230px) 1fr; gap: 4px 12px; font-size: 12.5px; line-height: 1.5; }
      .xsc-qa dt { color: var(--muted, #9aa7c7); font-size: 11.5px; }
      .xsc-qa dd { margin: 0; }
      .xsc-caveats ul { margin: 4px 0 0; padding-left: 18px; font-size: 12px; line-height: 1.5; color: var(--amber, #ffb020); }
      .xsc-limit { margin: 0; font-size: 11.5px; line-height: 1.5; color: var(--muted, #9aa7c7); font-style: italic; }
      .xsc-criteria { font-size: 11.5px; color: var(--muted, #9aa7c7); }
      .xsc-criteria ol { margin: 6px 0 0; padding-left: 18px; line-height: 1.5; }
      .xsc-badge { display: inline-block; max-width: 100%; white-space: normal; font-family: var(--mono, monospace); font-size: 10.5px; line-height: 1.4; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--line, rgba(255,255,255,0.2)); color: var(--muted, #9aa7c7); }
      .xsc-badge.tone-attention { color: var(--magenta, #ff3d9a); border-color: rgba(255, 61, 154, 0.5); }
      .xsc-badge.tone-warn { color: var(--amber, #ffb020); border-color: rgba(255, 176, 32, 0.5); }
      .xsc-badge.tone-ok { color: var(--cyan, #26e0ff); border-color: rgba(38, 224, 255, 0.45); }
      .xsc-block { display: flex; flex-direction: column; gap: 6px; }
      .xsc-scroll { overflow-x: auto; }
      /* Layout fixo: a tabela ocupa a largura disponível e quebra o texto — nunca alarga o contêiner que a hospeda. */
      .xsc-table { width: 100%; border-collapse: collapse; font-size: 12px; table-layout: fixed; }
      .xsc-table td { overflow-wrap: anywhere; }
      .xsc-table th { text-align: left; font-family: var(--mono, monospace); font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted, #9aa7c7); font-weight: 500; padding: 6px 8px; border-bottom: 1px solid var(--line, rgba(255,255,255,0.15)); }
      .xsc-table td { padding: 7px 8px; vertical-align: top; border-bottom: 1px solid var(--line-2, rgba(255,255,255,0.07)); }
      .xsc-table tr.off td { opacity: 0.75; }
      .xsc-sub { display: block; margin-top: 2px; }
      .xsc-sub.warn { color: var(--amber, #ffb020); }
      .xsc-date { white-space: nowrap; font-family: var(--mono, monospace); font-size: 11px; }
      .xsc-mono { font-family: var(--mono, monospace); }
      .xsc-pager { display: flex; gap: 8px; }
      .xsc-pager button { font-family: var(--mono, monospace); font-size: 11px; color: var(--text, inherit); background: var(--panel-2, transparent); border: 1px solid var(--line, rgba(255,255,255,0.2)); border-radius: 8px; padding: 5px 12px; cursor: pointer; }
      .xsc-pager button:disabled { opacity: 0.35; cursor: not-allowed; }
      @media (max-width: 760px) { .xsc-qa { grid-template-columns: 1fr; } }
    `,
  ],
})
export class CrossSourceSituationsComponent implements OnChanges {
  @Input({ required: true }) assetId!: string;

  private readonly svc = inject(AssetService);

  protected readonly heading = CROSS_SOURCE_HEADING;
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');
  protected readonly data = signal<AssetCrossSource | null>(null);
  private readonly cvePageSize = 10;
  private cvePage = 1;

  protected readonly tone = crossSourceStateTone;
  protected readonly acqTone = acquisitionTone;
  protected readonly cvePageText = cvePageText;
  protected readonly sev = severityPt;

  ngOnChanges(): void {
    this.cvePage = 1;
    this.load();
  }

  load(): void {
    const assetId = this.assetId;
    this.state.set('loading');
    this.svc.correlations(assetId, this.cvePage, this.cvePageSize).subscribe({
      next: (d) => {
        if (this.assetId !== assetId) return;   // resposta de outro ativo (linha trocada)
        this.data.set(d);
        this.state.set('loaded');
      },
      error: () => {
        if (this.assetId !== assetId) return;
        this.data.set(null);
        this.state.set('error');
      },
    });
  }

  protected goCves(page: number): void {
    if (page < 1) return;
    this.cvePage = page;
    this.load();
  }
}
