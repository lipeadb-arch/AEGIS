import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import { KnightAssessment } from '../models/knight.models';

/**
 * Cliente do AEGIS KNIGHT (/api/v1/knight/assessments). O X-Tenant e o Bearer são injetados pelo
 * authInterceptor — não os repetimos. Todas as chamadas têm TIMEOUT explícito para nunca deixar a tela em
 * carregamento infinito; o erro é normalizado num Error limpo para o componente renderizar estado + retry.
 */
@Injectable({ providedIn: 'root' })
export class KnightService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1/knight/assessments`;

  /** A execução demo roda de forma síncrona (coleta + avaliação + IA consultiva) — janela mais folgada. */
  private readonly RUN_TIMEOUT_MS = 30_000;
  /** Leituras são rápidas. */
  private readonly READ_TIMEOUT_MS = 15_000;

  /** Dispara um assessment de DEMONSTRAÇÃO e devolve o resultado completo já persistido. */
  runDemo(): Observable<KnightAssessment> {
    return this.http.post<KnightAssessment>(`${this.base}/demo`, {}).pipe(
      timeout(this.RUN_TIMEOUT_MS),
      catchError(this.normalize('Não foi possível executar o assessment de demonstração.')),
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
