import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, TimeoutError, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  KnightAffectedObjects,
  KnightAssessment,
  KnightLatest,
  KnightSources,
  KnightSourceType,
} from '../models/knight.models';

/** Slug de rota para cada fonte real/demo (espelha o parser do controller). */
const SOURCE_SLUG: Record<KnightSourceType, string> = {
  Demo: 'demo',
  MicrosoftEntraId: 'entra',
  GoogleWorkspace: 'google',
};

/**
 * Cliente do AEGIS KNIGHT (/api/v1/knight/assessments). O X-Tenant e o Bearer são injetados pelo
 * authInterceptor. Todas as chamadas têm TIMEOUT explícito (sem carregamento infinito); o erro é normalizado
 * num Error limpo para o componente renderizar estado + retry. Uma coleta real NÃO configurada (409) vira um
 * erro identificável — o componente jamais apresenta o Demo no lugar de uma coleta real que falhou.
 */
/**
 * [AEGIS-KNIGHT-DURABLE-01] O navegador desistiu de esperar — NÃO o servidor. O servidor grava o veredito
 * determinístico ANTES de pedir a narrativa à IA, então um corte aqui costuma significar que o resultado já
 * existe do outro lado. Distinguir esse erro dos demais é o que permite RECUPERAR a avaliação em vez de
 * oferecer "tentar de novo" e disparar uma segunda coleta na fonte.
 */
export class KnightRunTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'KnightRunTimeoutError';
  }
}

@Injectable({ providedIn: 'root' })
export class KnightService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/knight/assessments`;

  private readonly RUN_TIMEOUT_MS = 60_000; // coleta real pode paginar o Graph
  private readonly READ_TIMEOUT_MS = 15_000;

  /** Dispara um assessment de DEMONSTRAÇÃO e devolve o resultado completo já persistido. */
  runDemo(): Observable<KnightAssessment> {
    return this.http.post<KnightAssessment>(`${this.base}/demo`, {}).pipe(
      timeout(this.RUN_TIMEOUT_MS),
      catchError((err: unknown) => {
        if (err instanceof TimeoutError) return throwError(() => this.runTimeout());
        return this.normalize('Não foi possível executar a avaliação de demonstração.')(err);
      }),
    );
  }

  /** Dispara um assessment da FONTE indicada (ex.: coleta real do Entra ID). */
  runSource(source: KnightSourceType): Observable<KnightAssessment> {
    return this.http.post<KnightAssessment>(`${this.base}/run/${SOURCE_SLUG[source]}`, {}).pipe(
      timeout(this.RUN_TIMEOUT_MS),
      catchError((err: unknown) => {
        if (err instanceof TimeoutError) return throwError(() => this.runTimeout());
        if (err instanceof HttpErrorResponse && err.status === 409) {
          return throwError(() => new Error(`A fonte ${source} não está configurada para este tenant.`));
        }
        return this.normalize(`Não foi possível executar a coleta da fonte ${source}.`)(err);
      }),
    );
  }

  /** O corte é do NAVEGADOR: a execução pode ter concluído do outro lado e ser recuperável por leitura. */
  private runTimeout(): KnightRunTimeoutError {
    return new KnightRunTimeoutError(
      'O navegador parou de esperar pela resposta. A execução pode ter concluído no servidor.',
    );
  }

  /** Disponibilidade das fontes para o tenant (Demo sempre; reais conforme configuração). */
  getSources(): Observable<KnightSources> {
    return this.http.get<KnightSources>(`${this.base}/sources`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar as fontes disponíveis.')),
    );
  }

  /**
   * [AEGIS-KNIGHT-DURABLE-01] Último RESULTADO CONCLUÍDO do tenant e, à parte, a tentativa mais recente que
   * não concluiu. 204 = nenhuma execução ainda (as duas metades vazias). Somente leitura: NÃO dispara coleta.
   */
  getLatest(): Observable<KnightLatest> {
    return this.http.get<KnightLatest>(`${this.base}/latest`, { observe: 'response' }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      map((resp) =>
        resp.status === 204 || !resp.body
          ? { assessment: null, unfinishedAttempt: null }
          : resp.body,
      ),
      catchError(this.normalize('Não foi possível carregar a última avaliação.')),
    );
  }

  /** Assessment por Id (restrito ao tenant do contexto no servidor). */
  getById(id: string): Observable<KnightAssessment> {
    return this.http.get<KnightAssessment>(`${this.base}/${id}`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar a avaliação.')),
    );
  }

  /**
   * [AEGIS-MVP-PRODUCT-02] Objetos que sustentam UM achado de UMA avaliação. A paginação e a busca são
   * PARÂMETROS DE SERVIDOR — o cliente nunca baixa a lista inteira para filtrar depois. Somente leitura:
   * abrir o detalhe não dispara coleta na fonte.
   */
  getAffected(
    runId: string,
    indicatorId: string,
    page: number,
    pageSize: number,
    search: string | null,
  ): Observable<KnightAffectedObjects> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    const term = (search ?? '').trim();
    if (term) params = params.set('search', term);

    return this.http
      .get<KnightAffectedObjects>(
        `${this.base}/${runId}/indicators/${encodeURIComponent(indicatorId)}/affected`,
        { params },
      )
      .pipe(
        timeout(this.READ_TIMEOUT_MS),
        catchError(this.normalize('Não foi possível carregar os objetos afetados por este achado.')),
      );
  }

  private normalize(message: string) {
    return (err: unknown) => {
      console.error(`KNIGHT: ${message}`, err);
      return throwError(() => new Error(message));
    };
  }
}
