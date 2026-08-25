import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { ConnectorConfig, ConnectorHealth, SaveConnectorRequest, SyncResult } from '../models/connector.models';

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

  /**
   * Coleta sob demanda. VulnerabilityScanner pode responder 202 e continuar em segundo plano para não manter
   * uma requisição HTTP aberta durante centenas de milhares de relações machine×CVE.
   */
  sync(connectorId: string): Observable<SyncResult> {
    return this.http
      .post<SyncResult | { queued: boolean; message?: string }>(`${this.base}/connectors/${connectorId}/sync`, {})
      .pipe(
        map((result) => {
          if ('queued' in result && result.queued) {
            // A tela atual trata mensagens de ação pelo caminho de erro para poder encerrar o estado busy sem
            // fingir que a coleta já terminou. É uma notificação operacional, não o body bruto do gateway.
            throw new Error(result.message || 'Sincronização iniciada em segundo plano. Atualize em alguns minutos.');
          }
          return result as SyncResult;
        }),
        catchError((err) => throwError(() => (err instanceof Error ? err : this.describe(err)))),
      );
  }

  /**
   * Traduz erro HTTP em mensagem curta e acionável. Nunca ecoa HTML de proxy/gateway, stack trace ou body bruto
   * para o DOM — um 502 do Render anteriormente fazia a página inteira de erro aparecer dentro do card.
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
      case 502:
      case 503:
      case 504:
        return new Error('Serviço temporariamente indisponível no gateway. A sincronização pode continuar em segundo plano; atualize o status em alguns minutos.');
      default: {
        const raw = typeof err?.error === 'string' ? err.error.trim() : '';
        const looksLikeHtml = /^(?:<!doctype\s+html|<html\b)/i.test(raw) || /<title>\s*50[234]\s*<\/title>/i.test(raw);
        if (raw && !looksLikeHtml && raw.length <= 500) return new Error(raw);
        return new Error('Não foi possível concluir a operação. Consulte os logs e tente novamente.');
      }
    }
  }
}
