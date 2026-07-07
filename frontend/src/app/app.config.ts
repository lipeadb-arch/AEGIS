import { ApplicationConfig, provideAppInitializer, inject, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { routes } from './app.routes';
import { authInterceptor } from './interceptors/auth.interceptor';
import { refreshInterceptor } from './interceptors/refresh.interceptor';
import { AuthService } from './services/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),

    // Ordem importa: refresh é o EXTERNO (vê o 401 e refaz a requisição), auth é o INTERNO
    // (injeta X-Tenant + Bearer). Ao refazer, a requisição repassa pelo auth e ganha o token novo.
    provideHttpClient(withInterceptors([refreshInterceptor, authInterceptor])),

    provideRouter(routes),

    // Silent refresh no start: reidrata o access token (que só vive em memória) a partir do cookie
    // HttpOnly. Nunca rejeita — se não houver sessão, o app inicia e o guard leva ao /login.
    provideAppInitializer(() => firstValueFrom(inject(AuthService).restoreSession())),
  ],
};
