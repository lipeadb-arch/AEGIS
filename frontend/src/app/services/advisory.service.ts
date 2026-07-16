import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { AdvisoryDto, GenerateAdvisoryCommand } from '../models/scoring.models';

/**
 * Motor CONSULTIVO do Aegis Score no frontend: gera recomendações de remediação (advisories) a partir de
 * um controle NIST não-conforme. Simples por design (sem NgRx): expõe Observable; o ESTADO fica em Signals
 * no componente que consome. O header X-Tenant e o Bearer do JWT são injetados pelo authInterceptor em
 * toda chamada ao apiBase — por isso NÃO os repetimos aqui.
 *
 * Resiliência: normaliza qualquer erro de transporte num Error limpo (sem vazar o HttpErrorResponse cru),
 * para o card renderizar um estado de erro elegante em vez de quebrar.
 */
@Injectable({ providedIn: 'root' })
export class AdvisoryService {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBase}/api/v1/scoring/advisories`;

  /**
   * POST /api/v1/scoring/advisories — gera e persiste um advisory para a subcategoria NIST informada.
   * Só o código trafega; o título, o risco documentado e o passo a passo técnico são redigidos pelo motor
   * de IA no servidor (Stub canned em DEV, LLM real com a chave). Devolve o advisory criado (201).
   */
  generate(subcategoryCode: string): Observable<AdvisoryDto> {
    const body: GenerateAdvisoryCommand = { subcategoryCode };
    return this.http.post<AdvisoryDto>(this.url, body).pipe(
      catchError((err) => {
        console.error('Aegis Score: falha ao gerar a recomendação (advisory).', err);
        return throwError(() => new Error('Não foi possível gerar a recomendação.'));
      }),
    );
  }
}
