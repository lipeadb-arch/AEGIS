import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { PostureSnapshotActionItem, PostureSnapshotDetail } from '../../models/posture-history.models';

/**
 * [AEGIS-MVP-PRODUCT-03] O que ficou CONGELADO numa publicação KNIGHT: as limitações da coleta e as ações
 * com a validação de cada uma, exatamente como estavam no instante da publicação.
 *
 * Componente PRÓPRIO por duas razões, nesta ordem: (1) é um bloco coeso com uma tese própria — "isto é o
 * passado, e não muda mais" —, e misturá-lo às tabelas de controles/indicadores da página apagaria essa
 * distinção; (2) o histórico já ocupava quase todo o orçamento de CSS por componente, e engordar aquele
 * arquivo tornaria qualquer ajuste futuro de estilo um jogo de soma zero.
 *
 * O que o bloco existe para NÃO deixar a tela fazer:
 *   • ler o estado ATUAL dos planos (isso vive na Central de Prioridades) — aqui tudo vem do snapshot;
 *   • colapsar etapa do plano e resultado no achado num "resolvido";
 *   • tratar ausência de validação como silêncio: uma ação encerrada sem validação diz isso, em voz alta.
 */
@Component({
  selector: 'app-knight-frozen-report',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="frozen">
      <h4>Limitações da coleta</h4>
      @if (limitations().length) {
        <ul>
          @for (l of limitations(); track l) { <li>{{ l }}</li> }
        </ul>
        <p class="note">
          Indicadores que dependem destas capacidades ficam sem veredito — o que reduz a cobertura e
          <b>não</b> significa conformidade.
        </p>
      } @else {
        <p class="note">
          Nenhuma limitação declarada por esta coleta. Cobertura abaixo de 100% continua significando
          indicadores sem veredito, e esses não são conformidade.
        </p>
      }
    </div>

    <div class="frozen">
      <h4>Ações e validações congeladas nesta publicação</h4>
      @if (actions().length) {
        <!-- [AEGIS-MVP-PRODUCT-03] A contagem segue a validação APLICÁVEL ao ciclo vigente de cada ação.
             Somar a comprovação de um ciclo já encerrado ao total de hoje apresentaria como resolvido um
             trabalho que ainda está em curso. Fotografias anteriores a esta distinção não sabem responder e
             por isso não recebem esta linha — inventar a resposta seria pior do que não dá-la. -->
        @if (cycleSummary(); as resumo) { <p class="note">{{ resumo }}</p> }
        <div class="wrap">
          <table>
            <thead>
              <tr>
                <th>Ação</th><th>Responsável</th><th>Prazo</th><th>Situação do plano</th>
                <th>Resultado no achado</th><th>Base da conclusão</th>
              </tr>
            </thead>
            <tbody>
              @for (a of actions(); track a.actionPlanId) {
                <tr>
                  <td><span class="tt">{{ a.title }}</span><span class="mono">{{ a.indicatorId }}</span></td>
                  <td>{{ a.responsiblePerson || 'não designado' }}@if (a.responsibleArea) { · {{ a.responsibleArea }} }</td>
                  <td>{{ a.dueDate || 'sem prazo' }}</td>
                  <td>
                    {{ statusLabel(a.status) }}
                    @if (a.wasOverdue) { <span class="late">· em atraso</span> }
                  </td>
                  <td>
                    {{ result(a) }}
                    @if (historical(a)) {
                      <span class="past">
                        Validação de um ciclo ANTERIOR desta ação — permanece no registro, mas está fora da
                        comprovação do ciclo em curso.
                      </span>
                    }
                    @if (currentCycleProof(a); as atual) { <span class="mono">{{ atual }}</span> }
                  </td>
                  <td class="basis">
                    {{ basis(a) }}
                    @if (reopenedWithoutProof(a)) {
                      <span class="past">
                        Ação reaberta{{ a.cycleStartedAt ? ' em ' + (a.cycleStartedAt | date: 'dd/MM/yyyy HH:mm') : '' }}
                        — sem comprovação aplicável a este ciclo.
                      </span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <p class="note">
          Estes valores foram <b>congelados na publicação</b>: alterar um plano depois não muda este
          relatório. O estado atual das ações fica na Central de Prioridades.
        </p>
      } @else {
        <p class="note">Nenhuma ação registrada até a publicação desta fotografia.</p>
      }
    </div>
  `,
  styles: [
    `
      .frozen { margin-top: 14px; display: flex; flex-direction: column; gap: 8px; }
      h4 { margin: 0; font-family: var(--mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); }
      ul { margin: 0; padding-left: 18px; }
      li, .note { margin: 0; font-size: 12px; line-height: 1.55; color: var(--muted); }
      li { font-family: var(--mono); font-size: 11.5px; }
      .note b { color: var(--text); }
      .wrap { overflow-x: auto; }
      table { width: 100%; border-collapse: collapse; font-size: 12px; }
      th { text-align: left; font-family: var(--mono); font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); padding: 8px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
      td { padding: 9px 10px; border-bottom: 1px solid rgba(122, 145, 190, 0.12); color: var(--text); vertical-align: top; }
      .tt { display: block; }
      .mono { display: block; font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
      .basis { font-size: 11.5px; color: var(--muted); }
      .late { color: #ff5c8a; }
      .past { display: block; margin-top: 4px; font-size: 11px; line-height: 1.5; color: var(--amber); }
    `,
  ],
})
export class KnightFrozenReportComponent {
  /** A fotografia publicada. Os campos congelados são OPCIONAIS: fotografias antigas não os têm. */
  readonly snapshot = input.required<PostureSnapshotDetail>();

  /** Ausência é lida como ausência (lista vazia) — jamais preenchida com o estado atual dos planos. */
  readonly limitations = computed<string[]>(() => this.snapshot().collectionLimitations ?? []);
  readonly actions = computed<PostureSnapshotActionItem[]>(() => this.snapshot().actionItems ?? []);

  /**
   * A fotografia congelou a APLICABILIDADE ao ciclo? O início do ciclo é o discriminador: ele passou a ser
   * gravado com as ações, e a sua ausência identifica uma publicação anterior a esta distinção — da qual
   * não se pode afirmar nem que a validação vale para o ciclo em curso, nem que não vale.
   */
  private readonly freezesCycle = computed(() => this.actions().some((a) => !!a.cycleStartedAt));

  /** Esta linha exibe uma validação de um ciclo já encerrado? */
  protected historical(a: PostureSnapshotActionItem): boolean {
    return this.freezesCycle() && a.validationAppliesToCurrentCycle === false;
  }

  /** Ação retomada que ainda não tem comprovação alguma aplicável ao ciclo em curso. */
  protected reopenedWithoutProof(a: PostureSnapshotActionItem): boolean {
    return this.freezesCycle() && a.wasReopened === true && !a.applicableValidationOutcome;
  }

  /**
   * A comprovação do ciclo em curso, quando ela existe E é diferente da validação exibida na linha. Sem esta
   * frase, uma linha marcada como histórica se leria como "nada foi comprovado neste ciclo" — que é outra
   * afirmação.
   */
  protected currentCycleProof(a: PostureSnapshotActionItem): string | null {
    if (!this.historical(a) || !a.applicableValidationOutcome) return null;
    return `Comprovação do ciclo em curso: ${this.outcomeLabel(a.applicableValidationOutcome)}.`;
  }

  /**
   * Uma linha de resumo contada pelo CICLO VIGENTE — nula nas fotografias que não congelaram a distinção.
   */
  protected cycleSummary(): string | null {
    if (!this.freezesCycle()) return null;
    const itens = this.actions();
    const comprovadas = itens.filter(
      (a) =>
        a.applicableValidationMethod === 'NewAssessment' &&
        (a.applicableValidationOutcome === 'ExposureCleared' ||
          a.applicableValidationOutcome === 'ReductionObserved'),
    ).length;
    const historicas = itens.filter(
      (a) => a.validationAppliesToCurrentCycle === false && !a.applicableValidationOutcome,
    ).length;
    const base =
      `${itens.length} ação(ões) congelada(s); ${comprovadas} com melhora comprovada por nova coleta ` +
      'no ciclo em curso.';
    return historicas > 0
      ? `${base} Outras ${historicas} trazem apenas validação de um ciclo anterior — esse registro continua ` +
          'verdadeiro, mas não comprova o trabalho em curso.'
      : base;
  }

  /** Rótulo da etapa congelada. O enum viaja como NOME; um valor desconhecido aparece cru, não some. */
  protected statusLabel(status: string): string {
    switch (status) {
      case 'Aberto': return 'Aberta';
      case 'EmAndamento': return 'Em andamento';
      case 'AguardandoValidacao': return 'Aguardando validação';
      case 'Concluido': return 'Concluída';
      case 'Vencido': return 'Vencida (legado)';
      default: return status;
    }
  }

  /**
   * O RESULTADO no achado, separado da etapa do plano. Sem validação, a tela diz explicitamente que não há
   * comprovação — inclusive quando a ação foi encerrada, que é justamente o caso em que o silêncio enganaria.
   */
  protected result(a: PostureSnapshotActionItem): string {
    if (!a.validationOutcome) {
      return a.status === 'Concluido'
        ? 'Encerrada sem validação registrada — a correção não foi comprovada.'
        : 'Ainda não validado.';
    }
    const quantidade =
      a.observedBefore !== null && a.observedAfter !== null
        ? ` (${a.observedBefore} → ${a.observedAfter} afetado(s))`
        : '';
    return `${this.outcomeLabel(a.validationOutcome)}${quantidade}`;
  }

  /** A base da conclusão: só os CONJUNTOS preservados sustentam "estes objetos foram corrigidos". */
  protected basis(a: PostureSnapshotActionItem): string {
    if (!a.validationOutcome) return '—';
    if (a.validationMethod === 'HumanEvidence') {
      return 'Atestação humana com evidência referenciada; o AEGIS não verificou o ambiente.';
    }
    return a.comparedBySets
      ? 'Comparação dos conjuntos preservados nas duas coletas.'
      : 'Comparação de quantidade — não identifica quais objetos foram corrigidos.';
  }

  private outcomeLabel(outcome: string): string {
    switch (outcome) {
      case 'ExposureCleared': return 'Exposição encerrada na nova avaliação';
      case 'ReductionObserved': return 'Redução observada (achado ainda exposto)';
      case 'NoChangeObserved': return 'Sem melhora observada';
      case 'EvidenceInsufficient': return 'Evidência insuficiente para comprovar';
      case 'HumanAttested': return 'Atestação humana (não é comprovação técnica)';
      default: return outcome;
    }
  }
}
