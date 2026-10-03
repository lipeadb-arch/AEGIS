import { inject } from '@angular/core';
import { Params, Router, Routes } from '@angular/router';
import { DashboardsComponent } from './pages/dashboards.component';
import { AssetInventoryComponent } from './pages/asset-inventory.component';
import { DocumentHubComponent } from './pages/document-hub.component';
import { AegisKnightComponent } from './pages/aegis-knight.component';
import { HistoryPageComponent } from './pages/history-page.component';
import { PostureExposuresComponent } from './pages/posture-exposures.component';
import { VulnerabilitiesComponent } from './pages/vulnerabilities.component';
import { PrioritiesComponent } from './pages/priorities.component';
import { NistHomeComponent } from './pages/nist/nist-home.component';
import { NistFunctionComponent } from './pages/nist/nist-function.component';
import { NistSubcategoryComponent } from './pages/nist/nist-subcategory.component';
import { LoginComponent } from './pages/login.component';
import { IntegrationsComponent } from './pages/integrations.component';
import { SettingsComponent } from './pages/settings/settings.component';
import { SettingsGeneralComponent } from './pages/settings/settings-general.component';
import { SettingsUsersComponent } from './pages/settings/settings-users.component';
import { SettingsTenantsComponent } from './pages/settings/settings-tenants.component';
import { authGuard } from './guards/auth.guard';
import { tenantAdminGuard } from './guards/tenant-admin.guard';
import { platformAdminGuard } from './guards/platform-admin.guard';

/** Trilha exibida pela casca acima das telas de apoio (as telas do NIST desenham a própria). */
export interface Crumb {
  label: string;
  link: string | null;
}

/** [AEGIS-NIST-JOURNEY-01] Redireciona preservando os parâmetros da URL antiga e acrescentando os do destino. */
function redirectWith(path: string, extra: Params = {}) {
  return ({ queryParams }: { queryParams: Params }) =>
    inject(Router).createUrlTree([path], { queryParams: { ...queryParams, ...extra } });
}

const nistCrumbs = (fn: string, label: string, page: string): Crumb[] => [
  { label: 'AEGIS NIST', link: '/nist' },
  { label, link: `/nist/${fn}` },
  { label: page, link: null },
];

/**
 * [AEGIS-NIST-JOURNEY-01] Navegação organizada em torno dos dois assessments: Dashboards · AEGIS KNIGHT · AEGIS NIST (seis
 * funções) · Histórico de postura · Configurações. As telas que eram módulos à parte (ativos, vulnerabilidades,
 * recomendações, prioridades, documentos, controles e tendência) vivem agora DENTRO do NIST ou do histórico, e os
 * endereços antigos REDIRECIONAM para o destino equivalente — nenhum link emitido quebra, e nenhuma rota antiga continua
 * expondo um terceiro módulo desconectado.
 */
export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Aegis · Entrar' },

  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: DashboardsComponent, canActivate: [authGuard], title: 'Aegis · Dashboards' },

  // AEGIS KNIGHT — navegação interna preservada (?run, ?finding, ?plan, ?tab). /identity era o endereço anterior.
  { path: 'knight', component: AegisKnightComponent, canActivate: [authGuard], title: 'AEGIS KNIGHT' },
  { path: 'identity', redirectTo: redirectWith('/knight') },

  // AEGIS NIST — avaliação, funções, subcategorias e recursos de apoio por função.
  { path: 'nist', component: NistHomeComponent, canActivate: [authGuard], title: 'AEGIS NIST' },
  {
    path: 'nist/gv/documentos', component: DocumentHubComponent, canActivate: [authGuard], title: 'AEGIS NIST · Documentos',
    data: { crumbs: nistCrumbs('gv', 'Govern — Governar', 'Biblioteca de documentos') },
  },
  {
    path: 'nist/id/ativos', component: AssetInventoryComponent, canActivate: [authGuard], title: 'AEGIS NIST · Inventário de ativos',
    data: { crumbs: nistCrumbs('id', 'Identify — Identificar', 'Inventário de ativos') },
  },
  {
    path: 'nist/id/vulnerabilidades', component: VulnerabilitiesComponent, canActivate: [authGuard], title: 'AEGIS NIST · Vulnerabilidades',
    data: { crumbs: nistCrumbs('id', 'Identify — Identificar', 'Vulnerabilidades') },
  },
  {
    path: 'nist/id/prioridades', component: PrioritiesComponent, canActivate: [authGuard], title: 'AEGIS NIST · Prioridades de tratamento',
    data: { crumbs: nistCrumbs('id', 'Identify — Identificar', 'Prioridades de tratamento') },
  },
  {
    path: 'nist/pr/recomendacoes', component: PostureExposuresComponent, canActivate: [authGuard], title: 'AEGIS NIST · Recomendações de postura',
    data: { crumbs: nistCrumbs('pr', 'Protect — Proteger', 'Recomendações de postura') },
  },
  { path: 'nist/:fn', component: NistFunctionComponent, canActivate: [authGuard], title: 'AEGIS NIST · Função', data: { tab: 'avaliacao' } },
  { path: 'nist/:fn/postura', component: NistFunctionComponent, canActivate: [authGuard], title: 'AEGIS NIST · Postura do ambiente', data: { tab: 'postura' } },
  { path: 'nist/:fn/recursos', component: NistFunctionComponent, canActivate: [authGuard], title: 'AEGIS NIST · Recursos de apoio', data: { tab: 'recursos' } },
  { path: 'nist/:fn/:code', component: NistSubcategoryComponent, canActivate: [authGuard], title: 'AEGIS NIST · Subcategoria' },

  // Histórico de postura unificado (evolução mensal, fotografias publicadas e tendência diária).
  { path: 'history', component: HistoryPageComponent, canActivate: [authGuard], title: 'Aegis · Histórico de postura' },

  // ---- Endereços anteriores → destino equivalente na nova organização ----
  { path: 'assets', redirectTo: redirectWith('/nist/id/ativos') },
  { path: 'vulnerabilities', redirectTo: redirectWith('/nist/id/vulnerabilidades') },
  { path: 'priorities', redirectTo: redirectWith('/nist/id/prioridades') },
  { path: 'exposures', redirectTo: redirectWith('/nist/pr/recomendacoes') },
  { path: 'governance', redirectTo: redirectWith('/nist/gv/documentos') },
  { path: 'controls', redirectTo: redirectWith('/nist') },
  { path: 'protect', redirectTo: redirectWith('/nist/pr/postura') },
  { path: 'detect', redirectTo: redirectWith('/nist/de/postura') },
  { path: 'respond', redirectTo: redirectWith('/nist/rs/postura') },
  { path: 'recover', redirectTo: redirectWith('/nist/rc/postura') },
  { path: 'aegis-score', redirectTo: redirectWith('/history', { vista: 'tendencia' }) },

  {
    // Shell de Configurações com abas (Geral, Usuários e acessos, Integrações). As rotas administrativas
    // são guardadas por tenantAdminGuard (visibilidade NÃO substitui o backend). /settings → /settings/general.
    path: 'settings',
    component: SettingsComponent,
    canActivate: [authGuard],
    title: 'Aegis · Configurações',
    children: [
      { path: '', redirectTo: 'general', pathMatch: 'full' },
      { path: 'general', component: SettingsGeneralComponent, title: 'Aegis · Configurações · Geral' },
      {
        path: 'users',
        component: SettingsUsersComponent,
        canActivate: [tenantAdminGuard],
        title: 'Aegis · Usuários e acessos',
      },
      {
        path: 'integrations',
        component: IntegrationsComponent,
        canActivate: [tenantAdminGuard],
        title: 'Aegis · Integrações',
      },
      {
        // [AEGIS-MVP-ADMIN-LIFECYCLE-01] Administração de AMBIENTES — autoridade GLOBAL (PlatformAdmin).
        path: 'tenants',
        component: SettingsTenantsComponent,
        canActivate: [platformAdminGuard],
        title: 'Aegis · Ambientes',
      },
    ],
  },
  { path: '**', redirectTo: 'dashboard' },
];
