import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import { KnightAssessment, KnightSources, KnightSourceType } from '../models/knight.models';

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
      catchError(this.normalize('Não foi possível executar o assessment de demonstração.')),
    );
  }

  /** Dispara um assessment da FONTE indicada (ex.: coleta real do Entra ID). */
  runSource(source: KnightSourceType): Observable<KnightAssessment> {
    return this.http.post<KnightAssessment>(`${this.base}/run/${SOURCE_SLUG[source]}`, {}).pipe(
      timeout(this.RUN_TIMEOUT_MS),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse && err.status === 409) {
          return throwError(() => new Error(`A fonte ${source} não está configurada para este tenant.`));
        }
        return this.normalize(`Não foi possível executar a coleta da fonte ${source}.`)(err);
      }),
    );
  }

  /** Disponibilidade das fontes para o tenant (Demo sempre; reais conforme configuração). */
  getSources(): Observable<KnightSources> {
    return this.http.get<KnightSources>(`${this.base}/sources`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar as fontes disponíveis.')),
    );
  }

  /** Último assessment do tenant — <c>null</c> quando o servidor responde 204 (nenhum ainda). */
  getLatest(): Observable<KnightAssessment | null> {
    return this.http.get<KnightAssessment>(`${this.base}/latest`, { observe: 'response' }).pipe(
      timeout(this.READ_TIMEOUT_MS),
      map((resp) => (resp.status === 204 ? null : resp.body)),
      catchError(this.normalize('Não foi possível carregar o último assessment.')),
    );
  }

  /** Assessment por Id (restrito ao tenant do contexto no servidor). */
  getById(id: string): Observable<KnightAssessment> {
    return this.http.get<KnightAssessment>(`${this.base}/${id}`).pipe(
      timeout(this.READ_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível carregar o assessment.')),
    );
  }

  private normalize(message: string) {
    return (err: unknown) => {
      console.error(`KNIGHT: ${message}`, err);
      return throwError(() => new Error(message));
    };
  }
}
