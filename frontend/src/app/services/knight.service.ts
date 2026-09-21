import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, TimeoutError, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  KnightAffectedObjects,
  KnightAffectedSummary,
  KnightAssessment,
  KnightLatest,
  KnightLatestBySource,
  KnightReferenceCoverage,
  KnightSources,
  KnightSourceType,
} from '../models/knight.models';

/** Slug de rota para cada fonte real/demo (espelha o parser do controller). */
const SOURCE_SLUG: Record<KnightSourceType, string> = {
  Demo: 'demo',
  MicrosoftEntraId: 'entra',
  // [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams: fonte própria, credencial do mesmo conector Microsoft.
  MicrosoftTeams: 'teams',
  GoogleWorkspace: 'google',
};

/**
 * [AEGIS-KNIGHT-DURABLE-01] O NAVEGADOR deixou de esperar — o que aconteceu no servidor NÃO é conhecido. A
 * tentativa pode ter concluído, pode ainda estar em andamento ou pode não ter sido registrada, e a requisição
 * não devolveu nenhum identificador que permita reconhecê-la depois. Distinguir este erro serve para NÃO
 * tratá-lo como falha da avaliação e NÃO oferecer "tentar de novo" (outra coleta na fonte) como saída.
 */
export class KnightRunTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'KnightRunTimeoutError';
  }
}

/**
 * Cliente do AEGIS KNIGHT (/api/v1/knight/assessments). O X-Tenant e o Bearer são injetados pelo
 * authInterceptor. Todas as chamadas têm TIMEOUT explícito (sem carregamento infinito); o erro é normalizado
 * num Error limpo para o componente renderizar estado + retry. Uma coleta real NÃO configurada (409) vira um
 * erro identificável — o componente jamais apresenta o Demo no lugar de uma coleta real que falhou.
 */
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

  /** O corte é do NAVEGADOR: o desfecho no servidor é desconhecido e a resposta não identificou a execução. */
  private runTimeout(): KnightRunTimeoutError {
    return new KnightRunTimeoutError(
      'O navegador deixou de aguardar a resposta desta execução. O desfecho dela não foi confirmado.',
    );
  }

  /**
   * [AEGIS-KNIGHT-COVERAGE-01] Cobertura de IMPLEMENTAÇÃO do catálogo de referência (propriedade do produto). Leitura
   * pura do catálogo no servidor: não consulta o cliente e NÃO inicia coleta.
   */
  getReferenceCoverage(): Observable<KnightReferenceCoverage> {
    return this.http.get<KnightReferenceCoverage>(`${this.base}/reference-coverage`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar a cobertura do catálogo de referência.')),
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
   * Último assessment CONCLUÍDO do tenant — <c>null</c> quando o servidor responde 204. Contrato público
   * preservado de `GET /latest`.
   */
  getLatest(): Observable<KnightAssessment | null> {
    return this.http.get<KnightAssessment>(`${this.base}/latest`, { observe: 'response' }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      map((resp) => (resp.status === 204 ? null : resp.body)),
      catchError(this.normalize('Não foi possível carregar a última avaliação.')),
    );
  }

  /**
   * [AEGIS-KNIGHT-DURABLE-01] Leitura COMPOSTA (`GET /latest-state`): o último resultado concluído e, à parte,
   * a tentativa não finalizada que o sucede. Somente leitura: NÃO dispara coleta.
   */
  getLatestState(): Observable<KnightLatest> {
    return this.http.get<KnightLatest>(`${this.base}/latest-state`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      map((body) => ({
        assessment: body?.assessment ?? null,
        unfinishedAttempt: body?.unfinishedAttempt ?? null,
      })),
      catchError(this.normalize('Não foi possível consultar a última avaliação disponível.')),
    );
  }

  /**
   * [AEGIS-KNIGHT-COVERAGE-02] A última avaliação concluída de CADA fonte (`GET /latest-by-source`), com a
   * tentativa não finalizada que a sucede. É o que a tela lê quando há mais de uma fonte avaliada: apresentar
   * apenas a sincronização mais recente esconderia a avaliação da outra. Somente leitura: NÃO dispara coleta.
   */
  getLatestBySource(): Observable<KnightLatestBySource> {
    return this.http.get<KnightLatestBySource>(`${this.base}/latest-by-source`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      map((body) => ({ sources: body?.sources ?? [] })),
      catchError(this.normalize('Não foi possível carregar as avaliações por fonte.')),
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
    relation: 'affected' | 'evidence' = 'affected',
  ): Observable<KnightAffectedObjects> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    const term = (search ?? '').trim();
    if (term) params = params.set('search', term);
    // [AEGIS-KNIGHT-MULTICLOUD-01] "evidence" = a configuração que sustentou o veredito (políticas, papéis).
    if (relation === 'evidence') params = params.set('relation', 'evidence');

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

  /**
   * [AEGIS-KNIGHT-MULTICLOUD-01] Ocorrências (objeto × controle), objetos únicos e os que mais se repetem entre
   * controles reprovados — da avaliação indicada. Somente leitura.
   */
  getAffectedSummary(runId: string): Observable<KnightAffectedSummary> {
    return this.http.get<KnightAffectedSummary>(`${this.base}/${runId}/affected-summary`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar o resumo de objetos afetados.')),
    );
  }

  private normalize(message: string) {
    return (err: unknown) => {
      console.error(`KNIGHT: ${message}`, err);
      return throwError(() => new Error(message));
    };
  }
}
