import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { ConnectorConfig, ConnectorHealth, SaveConnectorRequest } from '../models/connector.models';

/**
 * Cliente das rotas de integração.
 *
 * ⚠️ NENHUM método recebe ou envia TenantId. O tenant ativo é resolvido no servidor a partir do claim
 * `tenant_id` do JWT, e o `authInterceptor` já anexa Bearer + X-Tenant derivados do mesmo token — foi
 * a refatoração de segurança das §20/§22 (o id de tenant na rota era IDOR latente). Repor um id aqui
 * reabriria a porta que fechamos.
 */
@Injectable({ providedIn: 'root' })
export class ConnectorService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1`;

  /** Conectores do tenant ativo. Sem segredo: o DTO traz apenas `hasCredentials`. */
  list(): Observable<ConnectorConfig[]> {
    return this.http
      .get<ConnectorConfig[]>(`${this.base}/connectors`)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /**
   * Cria OU reconfigura (UPSERT pela chave natural provider+capability — §20.3). O backend devolve
   * 201 na criação e 200 na reconfiguração; para o cliente os dois são sucesso.
   *
   * ⚠️ `settings` viaja em CLARO dentro do TLS e é cifrado NO SERVIDOR (Data Protection). Nunca
   * ciframos no cliente: um blob "já cifrado" vindo do browser não é confiável, e a chave não mora aqui.
   */
  save(body: SaveConnectorRequest): Observable<ConnectorConfig> {
    return this.http
      .post<ConnectorConfig>(`${this.base}/tenants/connectors`, body)
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Health check sob demanda do conector (não persiste sinais). */
  test(connectorId: string): Observable<ConnectorHealth> {
    return this.http
      .post<ConnectorHealth>(`${this.base}/connectors/${connectorId}/test`, {})
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /** Coleta sob demanda: grava os sinais e carimba LastSyncAt/LastStatus numa transação. */
  sync(connectorId: string): Observable<{ signalsCollected: number }> {
    return this.http
      .post<{ signalsCollected: number }>(`${this.base}/connectors/${connectorId}/sync`, {})
      .pipe(catchError((err) => throwError(() => this.describe(err))));
  }

  /**
   * Traduz o erro HTTP em mensagem acionável. Os códigos aqui são os que estas rotas realmente emitem —
   * 403 vem do `[Authorize(Roles = "TenantAdmin")]`, 501 do registry sem adaptador para o par
   * provider+capability (caso real enquanto os conectores são stubs).
   */
  private describe(err: { status?: number; error?: unknown }): Error {
    switch (err?.status) {
      case 0:
        return new Error('API inacessível. Verifique se o servidor está no ar.');
      case 401:
        return new Error('Sessão expirada. Entre novamente.');
      case 403:
        return new Error('Somente administradores do cliente podem alterar integrações.');
      case 404:
        return new Error('Conector não encontrado neste cliente.');
      case 501:
        return new Error('Ainda não há adaptador implementado para este provedor/capacidade.');
      default:
        return new Error(
          typeof err?.error === 'string' && err.error
            ? err.error
            : 'Não foi possível concluir a operação. Tente novamente.',
        );
    }
  }
}
