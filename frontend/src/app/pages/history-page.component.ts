import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { EMPTY, Subject, catchError, map, switchMap } from 'rxjs';
import { MonthlyEvolutionComponent } from '../components/monthly-evolution.component';
import { NistHistoryItem } from '../models/nist.models';
import { HistoryWindowRequest, PostureMonthlyHistory } from '../models/posture-history.models';
import { NistService } from '../services/nist.service';
import { PostureHistoryService } from '../services/posture-history.service';
import { AegisDashboardComponent } from './aegis-dashboard.component';
import { PostureHistoryComponent } from './posture-history.component';

type View = 'mensal' | 'fotografias' | 'tendencia';

/**
 * [AEGIS-NIST-JOURNEY-01] Histórico de postura UNIFICADO — uma entrada para tudo que é temporal:
 *   • Evolução mensal: KNIGHT e NIST em séries separadas (última publicação do mês; mês sem publicação sem ponto;
 *     versões incompatíveis não ligadas) e a maturidade NIST por avaliação, à parte;
 *   • Fotografias publicadas: publicação, comparação e downloads existentes, sem alteração;
 *   • Tendência diária: a leitura atual do AEGIS Score (antiga "Tendência de postura").
 * A aba vive na URL (?vista=), para que links antigos e favoritos caiam no lugar certo.
 */
@Component({
  selector: 'app-history-page',
  standalone: true,
  imports: [RouterLink, MonthlyEvolutionComponent, PostureHistoryComponent, AegisDashboardComponent],
  template: `
    <section class="page">
      <header class="page-head">
        <div>
          <p class="page-eyebrow">Histórico de postura</p>
          <h1>Histórico de postura</h1>
          <p class="page-desc">
            Evolução mês a mês do <strong>AEGIS KNIGHT</strong> e do <strong>AEGIS NIST</strong>, registros publicados e
            imutáveis, e a tendência diária do AEGIS Score. Instrumentos e metodologias diferentes nunca formam uma série única.
          </p>
        </div>
      </header>

      <nav class="tabbar" aria-label="Visões do histórico">
        <a routerLink="/history" [queryParams]="{ vista: 'mensal' }" [class.on]="view() === 'mensal'" [attr.aria-current]="view() === 'mensal' ? 'page' : null">Evolução mensal</a>
        <a routerLink="/history" [queryParams]="{ vista: 'fotografias' }" [class.on]="view() === 'fotografias'" [attr.aria-current]="view() === 'fotografias' ? 'page' : null">Fotografias publicadas</a>
        <a routerLink="/history" [queryParams]="{ vista: 'tendencia' }" [class.on]="view() === 'tendencia'" [attr.aria-current]="view() === 'tendencia' ? 'page' : null">Tendência diária</a>
      </nav>

      @switch (view()) {
        @case ('fotografias') { <app-posture-history [embedded]="true" /> }
        @case ('tendencia') { <app-aegis-dashboard [embedded]="true" /> }
        @default {
          <section class="panel" aria-label="Evolução mensal">
            @if (error()) {
              <p class="notice error" role="alert">{{ error() }}</p>
            } @else if (!monthly()) {
              <div class="state" role="status"><span class="spinner" aria-hidden="true"></span><p>Carregando a evolução mensal…</p></div>
            } @else {
              <app-monthly-evolution [history]="monthly()" [nist]="nistItems()" [period]="period()" (periodChange)="setPeriod($event)" />
            }
          </section>
        }
      }
    </section>
  `,
})
export class HistoryPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly history = inject(PostureHistoryService);
  private readonly nist = inject(NistService);

  private readonly vista = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('vista'))), { initialValue: null });
  protected readonly view = computed<View>(() => {
    const v = this.vista();
    return v === 'fotografias' || v === 'tendencia' ? v : 'mensal';
  });

  protected readonly monthly = signal<PostureMonthlyHistory | null>(null);
  /** [AEGIS-ASSESSMENT-VISUALS-01] Período escolhido; cada troca descarta a resposta anterior ainda em voo. */
  protected readonly period = signal<HistoryWindowRequest>({ months: 12, until: null });
  private readonly periodRequests = new Subject<HistoryWindowRequest>();
  protected readonly nistItems = signal<NistHistoryItem[] | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.periodRequests
      .pipe(
        switchMap((p) =>
          this.history.monthly(p.months ?? 12, p.until).pipe(
            catchError((e: Error) => {
              this.error.set(e.message);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((h) => this.monthly.set(h));
    this.periodRequests.next(this.period());
    this.nist.history().pipe(takeUntilDestroyed()).subscribe({
      next: (items) => this.nistItems.set(items),
      error: () => this.nistItems.set([]),
    });
  }

  protected setPeriod(p: HistoryWindowRequest): void {
    this.period.set(p);
    this.monthly.set(null);
    this.error.set(null);
    this.periodRequests.next(p);
  }
}
