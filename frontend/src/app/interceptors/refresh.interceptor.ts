import { HttpErrorResponse, HttpHandlerFn, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { BehaviorSubject, Observable, catchError, filter, switchMap, take, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { ALREADY_RETRIED, isAuthEndpoint } from './api-endpoints';

/**
 * Estado do portão de refresh, compartilhado por todas as requisições:
 *  - pending: um refresh está em andamento; os concorrentes aguardam.
 *  - valid:   refresh concluído; carrega o novo token para liberar a fila.
 *  - failed:  refresh falhou; a fila é liberada com erro (dispara logout).
 */
type TokenState =
  | { kind: 'pending' }
  | { kind: 'valid'; token: string }
  | { kind: 'failed' };

// Estado de módulo (singleton de fato): sobrevive entre requisições, que é justamente o que faz o
// "lock" funcionar. isRefreshing é o mutex; gate$ é o canal por onde a fila é liberada.
let isRefreshing = false;
const gate$ = new BehaviorSubject<TokenState>({ kind: 'pending' });

/**
 * Interceptor EXTERNO da cadeia. Captura 401, coordena um único refresh e refaz as requisições —
 * as concorrentes ficam bloqueadas no RxJS até o refresh resolver.
 */
export const refreshInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);

  return next(req).pipe(
    catchError((error: unknown) => {
      const is401 = error instanceof HttpErrorResponse && error.status === 401;

      // Não renova em: erros != 401; chamadas de /auth (o /refresh 401 tem de falhar de vez);
      // ou requisições já refeitas uma vez (corta o loop 401→refresh→401).
      if (!is401 || isAuthEndpoint(req.url) || req.context.get(ALREADY_RETRIED)) {
        return throwError(() => error);
      }

      return handle401(req, next, auth);
    }),
  );
};

function handle401(req: HttpRequest<unknown>, next: HttpHandlerFn, auth: AuthService): Observable<any> {
  // ---- Requisições concorrentes: NÃO disparam refresh; esperam o líder no portão ----
  if (isRefreshing) {
    return gate$.pipe(
      filter((s) => s.kind !== 'pending'), // bloqueia enquanto o refresh corre
      take(1), // pega só a resolução e encerra a inscrição
      switchMap((s) => (s.kind === 'valid' ? retry(req, next) : failed())),
    );
  }

  // ---- Líder: a PRIMEIRA requisição a tomar 401 dispara o único refresh ----
  isRefreshing = true;
  gate$.next({ kind: 'pending' }); // fecha o portão para os que chegarem agora

  return auth.refresh().pipe(
    switchMap((newToken) => {
      isRefreshing = false;
      gate$.next({ kind: 'valid', token: newToken }); // libera a fila com o token novo
      return retry(req, next);
    }),
    catchError((err) => {
      isRefreshing = false;
      gate$.next({ kind: 'failed' }); // libera a fila com falha
      auth.forceLogout(); // limpa memória + redireciona ao login
      return throwError(() => err);
    }),
  );
}

/** Refaz a requisição marcando-a como já re-tentada; o auth interceptor injeta o Bearer renovado. */
function retry(req: HttpRequest<unknown>, next: HttpHandlerFn): Observable<any> {
  return next(req.clone({ context: req.context.set(ALREADY_RETRIED, true) }));
}

/** Encerra uma requisição da fila quando o refresh do líder falhou. */
function failed(): Observable<never> {
  return throwError(() => new HttpErrorResponse({ status: 401, statusText: 'Refresh failed' }));
}
